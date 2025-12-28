using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NpcVoiceMaster;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;

    private const string Fingerprint = "NpcVoiceMaster | long-term engine switch (standard/openai) | 2025-12-28";

    private readonly VoicePlayer voicePlayer = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(35) };

    public Configuration Configuration { get; }

    private volatile bool npcCaptureEnabled = false;

    private readonly SemaphoreSlim ttsGate = new(1, 1);

    // De-dupe repeated NPC lines
    private string lastSpeaker = "";
    private string lastText = "";
    private long lastTicks;

    // If busy, keep latest NPC line and speak it next
    private volatile PendingLine? pendingLine;
    private sealed class PendingLine
    {
        public readonly string Speaker;
        public readonly string Text;
        public readonly bool IsNpc;
        public PendingLine(string speaker, string text, bool isNpc) { Speaker = speaker; Text = text; IsNpc = isNpc; }
    }

    // Persisted NPC -> voice
    private readonly Dictionary<string, string> npcVoices = new(StringComparer.OrdinalIgnoreCase);
    private string npcVoicesPath = "";

    // Engine mode persistence
    private enum EngineMode { Standard, OpenAi }
    private EngineMode engineMode = EngineMode.Standard;
    private string engineModePath = "";

    // Cached AllTalk voice list (for Standard mode)
    private List<string> allTalkVoices = new();
    private long voicesLastRefreshTick;
    private const int VoicesRefreshMs = 60_000;

    // OpenAI-compatible voice names (AllTalk maps these internally)
    private static readonly string[] OpenAiVoices = new[]
    {
        "alloy","echo","fable","nova","onyx","shimmer"
    };

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        npcVoicesPath = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "npc_voices.json");
        engineModePath = Path.Combine(PluginInterface.GetPluginConfigDirectory(), "engine_mode.txt");

        LoadNpcVoices();
        LoadEngineMode();

        CommandManager.AddHandler("/voicetest", new CommandInfo((_, args) => RunVoiceTest(args))
        {
            HelpMessage = "Test TTS. Usage: /voicetest Hello there"
        });

        CommandManager.AddHandler("/voiceconfig", new CommandInfo((_, args) => VoiceConfigCommand(args))
        {
            HelpMessage = "Status/toggle. Usage: /voiceconfig | /voiceconfig on | /voiceconfig off"
        });

        CommandManager.AddHandler("/voiceengine", new CommandInfo((_, args) => VoiceEngineCommand(args))
        {
            HelpMessage = "Switch engine. Usage: /voiceengine | /voiceengine standard | /voiceengine openai"
        });

        CommandManager.AddHandler("/voicefinger", new CommandInfo((_, _) => PrintFingerprint())
        {
            HelpMessage = "Print plugin fingerprint/status."
        });

        ChatGui.ChatMessage += OnChatMessage;

        ChatGui.Print("[NpcVoiceMaster] Loaded. NPC capture is OFF (safe mode). Type /voiceconfig");
        ChatGui.Print($"[NpcVoiceMaster] {Fingerprint}");
        ChatGui.Print($"[NpcVoiceMaster] Engine mode: {engineMode}");
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= OnChatMessage;
        CommandManager.RemoveHandler("/voicetest");
        CommandManager.RemoveHandler("/voiceconfig");
        CommandManager.RemoveHandler("/voiceengine");
        CommandManager.RemoveHandler("/voicefinger");
        http.Dispose();
        voicePlayer.Stop();
        ttsGate.Dispose();
    }

    // ============================================================
    // COMMANDS
    // ============================================================

    private void PrintFingerprint()
    {
        ChatGui.Print($"[NpcVoiceMaster] {Fingerprint}");
        ChatGui.Print($"[NpcVoiceMaster] NPC capture: {(npcCaptureEnabled ? "ON" : "OFF")}");
        ChatGui.Print($"[NpcVoiceMaster] Engine: {engineMode}");
        ChatGui.Print($"[NpcVoiceMaster] BaseUrl: {(Configuration.AllTalkBaseUrl ?? "(empty)")}");
        ChatGui.Print($"[NpcVoiceMaster] Default Voice (standard mode): {(Configuration.AllTalkVoice ?? "(empty)")}");
        ChatGui.Print($"[NpcVoiceMaster] Persisted NPC voices: {npcVoices.Count}");
    }

    private void VoiceConfigCommand(string args)
    {
        var a = (args ?? "").Trim().ToLowerInvariant();

        if (a == "on" || a == "enable" || a == "1" || a == "true")
            npcCaptureEnabled = true;
        else if (a == "off" || a == "disable" || a == "0" || a == "false")
            npcCaptureEnabled = false;

        ChatGui.Print("— NpcVoiceMaster —");
        ChatGui.Print($"NPC capture: {(npcCaptureEnabled ? "ON" : "OFF")}");
        ChatGui.Print($"Engine: {engineMode}");
        ChatGui.Print($"AllTalkBaseUrl: {(Configuration.AllTalkBaseUrl ?? "(empty)")}");
        ChatGui.Print($"Default voice (standard): {(Configuration.AllTalkVoice ?? "(empty)")}");
        ChatGui.Print($"Persisted NPC voices: {npcVoices.Count} (npc_voices.json)");
        ChatGui.Print("Commands: /voicetest <text> | /voiceconfig on/off | /voiceengine standard/openai");
    }

    private void VoiceEngineCommand(string args)
    {
        var a = (args ?? "").Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(a))
        {
            ChatGui.Print($"[NpcVoiceMaster] Engine mode: {engineMode}");
            ChatGui.Print("[NpcVoiceMaster] Use: /voiceengine standard  OR  /voiceengine openai");
            return;
        }

        if (a == "standard")
        {
            engineMode = EngineMode.Standard;
            SaveEngineMode();
            ChatGui.Print("[NpcVoiceMaster] Engine set to STANDARD (/api/tts-generate) — best for many .wav voices.");
        }
        else if (a == "openai")
        {
            engineMode = EngineMode.OpenAi;
            SaveEngineMode();
            ChatGui.Print("[NpcVoiceMaster] Engine set to OPENAI (/v1/audio/speech) — direct audio bytes, 6 mapped voices.");
        }
        else
        {
            ChatGui.Print("[NpcVoiceMaster] Unknown. Use: /voiceengine standard  OR  /voiceengine openai");
        }
    }

    private void RunVoiceTest(string args)
    {
        var text = (args ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            text = "banana spaceship 123";

        ChatGui.Print($"[NpcVoiceMaster] /voicetest speaking now... (engine={engineMode})");
        _ = SpeakAndPlayAsync("VOICETEST", text, isNpc: false);
    }

    // REQUIRED BY OTHER FILES. DO NOT speak here.
    public void RunVoiceTestFromUI()
    {
        ChatGui.Print("[NpcVoiceMaster] UI voice test ignored. Use /voicetest <text> in game chat.");
    }

    // REQUIRED BY OTHER FILES
    public async Task<List<string>> FetchAllTalkVoicesAsync()
    {
        await EnsureVoicesFreshAsync(force: true).ConfigureAwait(false);
        return new List<string>(allTalkVoices);
    }

    // ============================================================
    // NPC CAPTURE (chat log)
    // ============================================================

    private void OnChatMessage(
        XivChatType type,
        int timestamp,
        ref SeString sender,
        ref SeString message,
        ref bool isHandled)
    {
        if (!npcCaptureEnabled)
            return;

        if (type != XivChatType.NPCDialogue &&
            type != XivChatType.NPCDialogueAnnouncements)
            return;

        var speaker = sender.TextValue.Trim();
        var text = message.TextValue.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var now = Environment.TickCount64;
        if (speaker == lastSpeaker && text == lastText && now - lastTicks < 2500)
            return;

        lastSpeaker = speaker;
        lastText = text;
        lastTicks = now;

        _ = SpeakAndPlayAsync(speaker, text, isNpc: true);
    }

    // ============================================================
    // SPEECH PIPELINE
    // ============================================================

    private async Task SpeakAndPlayAsync(string speaker, string text, bool isNpc)
    {
        // If busy, keep latest NPC line and play next.
        if (!await ttsGate.WaitAsync(0).ConfigureAwait(false))
        {
            if (isNpc)
                pendingLine = new PendingLine(speaker, text, isNpc: true);
            return;
        }

        try
        {
            byte[]? wav = engineMode switch
            {
                EngineMode.OpenAi => await Generate_OpenAiSpeechAsync(speaker, text, isNpc).ConfigureAwait(false),
                _ => await Generate_StandardAsync(speaker, text, isNpc).ConfigureAwait(false),
            };

            if (wav != null && wav.Length >= 256)
                voicePlayer.PlayAudio(wav);
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "[NpcVoiceMaster] SpeakAndPlayAsync failed");
        }
        finally
        {
            ttsGate.Release();
        }

        // Speak queued NPC line after finishing
        var p = pendingLine;
        pendingLine = null;
        if (p != null)
            _ = SpeakAndPlayAsync(p.Speaker, p.Text, p.IsNpc);
    }

    // ============================================================
    // ENGINE: STANDARD (/api/tts-generate) — unlimited .wav voices
    // ============================================================
    private async Task<byte[]?> Generate_StandardAsync(string speaker, string text, bool isNpc)
    {
        var baseUrl = (Configuration.AllTalkBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ChatGui.Print("[NpcVoiceMaster] AllTalkBaseUrl is empty.");
            return null;
        }

        await EnsureVoicesFreshAsync(force: false).ConfigureAwait(false);

        var voice = isNpc ? ResolveOrAssignVoiceForNpc_Standard(speaker) : GetDefaultVoiceFallback_Standard();

        // Unique output name so files don't collide
        var fileBase = "nvm_" + ShortHash($"{speaker}||{voice}||{text}");

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("text_input", text),
            new KeyValuePair<string,string>("text_filtering", "standard"),
            new KeyValuePair<string,string>("character_voice_gen", voice),
            new KeyValuePair<string,string>("output_file_name", fileBase),
            new KeyValuePair<string,string>("output_file_timestamp", "false"),
            new KeyValuePair<string,string>("autoplay", "false"),
        });

        using var resp = await http.PostAsync($"{baseUrl}/api/tts-generate", form).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));

        var status = doc.RootElement.TryGetProperty("status", out var st) ? (st.GetString() ?? "") : "";
        if (!status.Equals("generate-success", StringComparison.OrdinalIgnoreCase))
            return null;

        string? relUrl = null;
        if (doc.RootElement.TryGetProperty("output_cache_url", out var cacheEl))
            relUrl = cacheEl.GetString();
        if (string.IsNullOrWhiteSpace(relUrl) && doc.RootElement.TryGetProperty("output_file_url", out var fileEl))
            relUrl = fileEl.GetString();

        if (string.IsNullOrWhiteSpace(relUrl))
            return null;

        var audioUrl = relUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? relUrl : $"{baseUrl}{relUrl}";
        audioUrl = AddCacheBust(audioUrl);

        return await http.GetByteArrayAsync(audioUrl).ConfigureAwait(false);
    }

    // ============================================================
    // ENGINE: OPENAI-COMPAT (/v1/audio/speech) — direct bytes, 6 voices
    // ============================================================
    private async Task<byte[]?> Generate_OpenAiSpeechAsync(string speaker, string text, bool isNpc)
    {
        var baseUrl = (Configuration.AllTalkBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ChatGui.Print("[NpcVoiceMaster] AllTalkBaseUrl is empty.");
            return null;
        }

        // Voice is one of: alloy/echo/fable/nova/onyx/shimmer (mapped inside AllTalk)
        var voice = isNpc ? ResolveOrAssignVoiceForNpc_OpenAi(speaker) : "nova";

        var payload = new
        {
            model = "any_model_name",          // required by the API, currently ignored
            input = text,                      // your text
            voice = voice,                     // one of the 6 names
            response_format = "wav",
            speed = 1.0
        };

        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        using var resp = await http.PostAsync($"{baseUrl}/v1/audio/speech", content).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;

        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    // ============================================================
    // VOICE RESOLUTION + PERSISTENCE
    // ============================================================

    private string ResolveOrAssignVoiceForNpc_Standard(string npcName)
    {
        npcName = (npcName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(npcName))
            return GetDefaultVoiceFallback_Standard();

        if (npcVoices.TryGetValue(npcName, out var existing) && !string.IsNullOrWhiteSpace(existing))
            return existing;

        string chosen;
        if (allTalkVoices.Count > 0)
            chosen = allTalkVoices[StableIndex(npcName, allTalkVoices.Count)];
        else
            chosen = GetDefaultVoiceFallback_Standard();

        npcVoices[npcName] = chosen;
        SaveNpcVoices();

        ChatGui.Print($"[NpcVoiceMaster] Assigned voice '{chosen}' to NPC '{npcName}'");
        return chosen;
    }

    private string ResolveOrAssignVoiceForNpc_OpenAi(string npcName)
    {
        npcName = (npcName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(npcName))
            return "nova";

        if (npcVoices.TryGetValue(npcName, out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            // If the saved value isn't one of the OpenAI names, ignore it and reassign.
            if (OpenAiVoices.Contains(existing, StringComparer.OrdinalIgnoreCase))
                return existing;
        }

        var chosen = OpenAiVoices[StableIndex(npcName, OpenAiVoices.Length)];
        npcVoices[npcName] = chosen;
        SaveNpcVoices();

        ChatGui.Print($"[NpcVoiceMaster] Assigned OpenAI voice '{chosen}' to NPC '{npcName}'");
        return chosen;
    }

    private string GetDefaultVoiceFallback_Standard()
    {
        var v = (Configuration.AllTalkVoice ?? "").Trim();
        return string.IsNullOrWhiteSpace(v) ? "Mia.wav" : v;
    }

    private static int StableIndex(string key, int modulo)
    {
        // Stable hash -> stable voice choice
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key));
        int raw = BitConverter.ToInt32(bytes, 0);
        if (raw == int.MinValue) raw = 0;
        raw = Math.Abs(raw);
        return modulo <= 1 ? 0 : raw % modulo;
    }

    private static string ShortHash(string s)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes, 0, 10).ToLowerInvariant();
    }

    private static string AddCacheBust(string url)
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return url.Contains("?") ? $"{url}&t={t}" : $"{url}?t={t}";
    }

    // ============================================================
    // VOICE LIST REFRESH (standard mode helper)
    // ============================================================

    private async Task EnsureVoicesFreshAsync(bool force)
    {
        var now = Environment.TickCount64;
        if (!force && (now - voicesLastRefreshTick) < VoicesRefreshMs && allTalkVoices.Count > 0)
            return;

        voicesLastRefreshTick = now;

        var baseUrl = (Configuration.AllTalkBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        try
        {
            using var resp = await http.GetAsync($"{baseUrl}/api/voices").ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("voices", out var voices))
                return;

            var list = voices.EnumerateArray()
                .Where(v => v.ValueKind == JsonValueKind.String)
                .Select(v => v.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.Count > 0)
                allTalkVoices = list;
        }
        catch
        {
            // fine; retry later
        }
    }

    // ============================================================
    // FILES
    // ============================================================

    private void LoadNpcVoices()
    {
        try
        {
            if (!File.Exists(npcVoicesPath))
                return;

            var json = File.ReadAllText(npcVoicesPath);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (data == null) return;

            npcVoices.Clear();
            foreach (var kv in data)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                    npcVoices[kv.Key.Trim()] = kv.Value.Trim();
            }
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "[NpcVoiceMaster] Failed to load npc_voices.json");
        }
    }

    private void SaveNpcVoices()
    {
        try
        {
            var dir = Path.GetDirectoryName(npcVoicesPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(npcVoices, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(npcVoicesPath, json);
        }
        catch (Exception ex)
        {
            PluginLog.Warning(ex, "[NpcVoiceMaster] Failed to save npc_voices.json");
        }
    }

    private void LoadEngineMode()
    {
        try
        {
            if (!File.Exists(engineModePath))
                return;

            var s = File.ReadAllText(engineModePath).Trim().ToLowerInvariant();
            engineMode = (s == "openai") ? EngineMode.OpenAi : EngineMode.Standard;
        }
        catch
        {
            engineMode = EngineMode.Standard;
        }
    }

    private void SaveEngineMode()
    {
        try
        {
            var dir = Path.GetDirectoryName(engineModePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(engineModePath, engineMode == EngineMode.OpenAi ? "openai" : "standard");
        }
        catch
        {
            // ignore
        }
    }
}
