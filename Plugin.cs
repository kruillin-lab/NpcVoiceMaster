// File: Plugin.cs
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NpcVoiceMaster;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "NpcVoiceMaster";

    private const string BuildFingerprint = "NpcVoiceMaster | Talk capture (TextNode scan) | 2025-12-28";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("NpcVoiceMaster");
    private readonly ConfigWindow configWindow;

    private readonly HttpClient http = new();
    private readonly VoicePlayer voicePlayer = new();

    public Configuration Configuration { get; }

    private volatile bool npcCaptureEnabled = false;
    private volatile bool debug = true;

    private readonly SemaphoreSlim ttsGate = new(1, 1);

    private string lastSpokenKey = "";
    private long lastSpokenTick = 0;

    private static readonly string[] TalkAddonNames = new[]
    {
        "Talk",
        "TalkNoBorder",
        "TalkSubtitle",
        "TalkSmall",
        "BattleTalk",
    };

    public Plugin()
    {
        try { voicePlayer.Log = msg => ChatGui.Print(msg); } catch { }

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += () => configWindow.IsOpen = true;

        CommandManager.AddHandler("/voiceconfig", new CommandInfo((_, args) =>
        {
            var a = (args ?? "").Trim();

            if (a.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                npcCaptureEnabled = true;
                ChatGui.Print("[NpcVoiceMaster] NPC capture: ON");
                return;
            }

            if (a.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                npcCaptureEnabled = false;
                ChatGui.Print("[NpcVoiceMaster] NPC capture: OFF");
                return;
            }

            if (a.Equals("debug", StringComparison.OrdinalIgnoreCase))
            {
                debug = !debug;
                ChatGui.Print($"[NpcVoiceMaster] DEBUG is now {(debug ? "ON" : "OFF")}");
                return;
            }

            configWindow.IsOpen = !configWindow.IsOpen;
        })
        {
            HelpMessage = "Open settings, or toggle capture: /voiceconfig on | /voiceconfig off | /voiceconfig debug"
        });

        CommandManager.AddHandler("/voicetest", new CommandInfo((_, args) => RunVoiceTest(args))
        {
            HelpMessage = "Generate and play a test line. Usage: /voicetest any text..."
        });

        CommandManager.AddHandler("/voicefinger", new CommandInfo((_, _) =>
        {
            ChatGui.Print($"[NpcVoiceMaster] BUILD FINGERPRINT: {BuildFingerprint}");
            ChatGui.Print($"[NpcVoiceMaster] LOADED MARKER NOW: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        })
        { HelpMessage = "Print build fingerprint marker to confirm the plugin loaded." });

        foreach (var name in TalkAddonNames)
        {
            AddonLifecycle.RegisterListener(AddonEvent.PostSetup, name, OnAddonEvent);
            AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, name, OnAddonEvent);
            AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, name, OnAddonEvent);
        }

        ChatGui.Print($"[NpcVoiceMaster] LOADED: {BuildFingerprint}");
        ChatGui.Print("[NpcVoiceMaster] Capture is OFF by default. Run: /voiceconfig on");
    }

    public void Dispose()
    {
        try { AddonLifecycle.UnregisterListener(OnAddonEvent); } catch { }

        try { CommandManager.RemoveHandler("/voiceconfig"); } catch { }
        try { CommandManager.RemoveHandler("/voicetest"); } catch { }
        try { CommandManager.RemoveHandler("/voicefinger"); } catch { }

        try { windowSystem.RemoveAllWindows(); } catch { }
        try { PluginInterface.UiBuilder.Draw -= DrawUI; } catch { }

        try { voicePlayer.Stop(); } catch { }
        try { http.Dispose(); } catch { }
    }

    private void DrawUI() => windowSystem.Draw();

    public void RunVoiceTestFromUI() => RunVoiceTest(string.Empty);

    private void RunVoiceTest(string args)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                ChatGui.Print("[NpcVoiceMaster] /voicetest running...");
                var text = string.IsNullOrWhiteSpace(args) ? "VoiceMaster test line." : args.Trim();

                var wav = await SpeakNpcLineAsync("VOICETEST", text);
                if (wav == null || wav.Length < 256)
                {
                    ChatGui.Print("[NpcVoiceMaster] /voicetest: No audio returned (or too small).");
                    return;
                }

                voicePlayer.PlayAudio(wav);
                ChatGui.Print($"[NpcVoiceMaster] /voicetest: Playing ({wav.Length} bytes).");
            }
            catch (Exception ex)
            {
                ChatGui.Print($"[NpcVoiceMaster] /voicetest failed: {ex.Message}");
                PluginLog.Error(ex, "[NpcVoiceMaster] /voicetest failed");
            }
        });
    }

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            var addonName = (args.AddonName ?? "").Trim();

            if (debug)
                ChatGui.Print($"[NpcVoiceMaster] DEBUG: AddonEvent addon={addonName} event={type}");

            if (!npcCaptureEnabled)
                return;

            var addonPtr = args.Addon;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Yield();

                    if (!npcCaptureEnabled)
                        return;

                    if (!TryExtractTalkFromTextNodes(addonPtr, out var speaker, out var text, out var nodeCount))
                    {
                        if (debug)
                            ChatGui.Print($"[NpcVoiceMaster] DEBUG: SCAN addon={addonName} -> 0 textnodes");
                        return;
                    }

                    if (debug)
                        ChatGui.Print($"[NpcVoiceMaster] DEBUG: SCAN addon={addonName} -> textnodes={nodeCount} speaker='{speaker}' textLen={text.Length}");

                    var now = Environment.TickCount64;
                    var key = $"{addonName}|{speaker}|{text}";
                    if (key == lastSpokenKey && (now - lastSpokenTick) < 900)
                        return;

                    lastSpokenKey = key;
                    lastSpokenTick = now;

                    await SpeakCapturedLineAsync(addonName, speaker, text);
                }
                catch (Exception ex)
                {
                    PluginLog.Error(ex, "[NpcVoiceMaster] queued scan failed");
                }
            });
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "[NpcVoiceMaster] OnAddonEvent failed");
        }
    }

    private async Task SpeakCapturedLineAsync(string addonName, string speaker, string text)
    {
        if (!await ttsGate.WaitAsync(0))
            return;

        try
        {
            ChatGui.Print($"[NpcVoiceMaster] DEBUG: CAPTURE addon={addonName} speaker='{speaker}' text='{text}'");

            var wav = await SpeakNpcLineAsync(speaker, text);
            if (wav == null || wav.Length < 256)
            {
                ChatGui.Print("[NpcVoiceMaster] No audio returned.");
                return;
            }

            voicePlayer.PlayAudio(wav);
        }
        finally
        {
            ttsGate.Release();
        }
    }

    private static unsafe bool TryExtractTalkFromTextNodes(AtkUnitBasePtr addon, out string speaker, out string text, out int nodeCount)
    {
        speaker = "";
        text = "";
        nodeCount = 0;

        try
        {
            AtkUnitBase* u = addon;
            if (u == null || u->UldManager.NodeList == null || u->UldManager.NodeListCount <= 0)
                return false;

            var found = new List<string>(32);

            for (int i = 0; i < u->UldManager.NodeListCount; i++)
            {
                var n = u->UldManager.NodeList[i];
                if (n == null)
                    continue;

                if (n->Type != NodeType.Text)
                    continue;

                var tn = (AtkTextNode*)n;
                var s = tn->NodeText.ToString();

                s = CollapseWhitespace(s).Trim();
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (IsNoise(s)) continue;

                found.Add(s);
            }

            nodeCount = found.Count;
            if (found.Count == 0)
                return false;

            var bestText = found
                .Distinct(StringComparer.Ordinal)
                .OrderByDescending(ScoreDialogue)
                .ThenByDescending(s => s.Length)
                .FirstOrDefault() ?? "";

            var bestSpeaker = found
                .Distinct(StringComparer.Ordinal)
                .Where(s => !string.Equals(s, bestText, StringComparison.Ordinal))
                .OrderByDescending(ScoreSpeaker)
                .ThenBy(s => s.Length)
                .FirstOrDefault() ?? "";

            if (string.IsNullOrWhiteSpace(bestText))
                return false;

            speaker = string.IsNullOrWhiteSpace(bestSpeaker) ? "Unknown" : bestSpeaker.Trim();
            text = bestText.Trim();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsNoise(string s)
    {
        var t = (s ?? "").Trim();
        return t.Equals("Next", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("Previous", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("Back", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("Close", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("Cancel", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("Confirm", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("Proceed", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
               t.Equals("No", StringComparison.OrdinalIgnoreCase);
    }

    private static int ScoreDialogue(string s)
    {
        var score = 0;
        if (s.Length >= 15) score += 10;
        if (s.Length >= 60) score += 10;
        if (s.Contains(' ')) score += 5;
        if (s.Contains('.') || s.Contains('?') || s.Contains('!')) score += 5;
        return score;
    }

    private static int ScoreSpeaker(string s)
    {
        var score = 0;
        if (s.Length >= 2 && s.Length <= 40) score += 10;
        if (!(s.Contains('.') || s.Contains('?') || s.Contains('!'))) score += 4;

        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length >= 1 && words.Length <= 3) score += 4;

        return score;
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var wasSpace = false;

        foreach (var ch in s)
        {
            var isWs = ch == '\n' || ch == '\r' || ch == '\t' || ch == ' ';
            if (isWs)
            {
                if (wasSpace) continue;
                sb.Append(' ');
                wasSpace = true;
            }
            else
            {
                sb.Append(ch);
                wasSpace = false;
            }
        }

        return sb.ToString();
    }

    // ConfigWindow needs this
    public async Task<List<string>?> FetchAllTalkVoicesAsync()
    {
        if (string.IsNullOrWhiteSpace(Configuration.AllTalkBaseUrl))
            return new List<string>();

        var baseUrl = (Configuration.AllTalkBaseUrl ?? "").Trim().TrimEnd('/');
        var url = $"{baseUrl}/api/voices";

        using var resp = await http.GetAsync(url);
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        if (!resp.IsSuccessStatusCode)
        {
            ChatGui.Print($"[NpcVoiceMaster] /api/voices failed {(int)resp.StatusCode}: {SafeUtf8(bytes)}");
            return new List<string>();
        }

        try
        {
            using var doc = JsonDocument.Parse(bytes);
            if (!doc.RootElement.TryGetProperty("voices", out var v) || v.ValueKind != JsonValueKind.Array)
                return new List<string>();

            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    list.Add(s.Trim());
            }

            return list;
        }
        catch
        {
            ChatGui.Print("[NpcVoiceMaster] /api/voices returned invalid JSON.");
            return new List<string>();
        }
    }

    // Keep your existing voice logic (simple fallback here; your project can expand it)
    public (string voice, string bucketUsed, string reason) ResolveVoiceForNpc(string npcKey, string forcedBucket = "")
    {
        var fallback = string.IsNullOrWhiteSpace(Configuration.AllTalkVoice) ? "Mia.wav" : Configuration.AllTalkVoice.Trim();
        return (fallback, "", "FALLBACK-DEFAULT");
    }

    public async Task<byte[]?> SpeakNpcLineAsync(string npcKey, string text, string forcedBucket = "")
    {
        var baseUrl = (Configuration.AllTalkBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var npc = (npcKey ?? "").Trim();
        var line = (text ?? "");

        var (voice, bucketUsed, reason) = ResolveVoiceForNpc(npc, forcedBucket);

        if (!string.IsNullOrWhiteSpace(bucketUsed))
            ChatGui.Print($"[NpcVoiceMaster] VOICE RESOLVE npc='{npc}' bucket='{bucketUsed}' -> '{voice}' ({reason})");
        else
            ChatGui.Print($"[NpcVoiceMaster] VOICE RESOLVE npc='{npc}' -> '{voice}' ({reason})");

        var wav = await AllTalkPreviewGenerateAndDownloadAsync(baseUrl, voice, line);
        return wav;
    }

    private async Task<byte[]?> AllTalkPreviewGenerateAndDownloadAsync(string baseUrl, string voiceFile, string text)
    {
        var cleanBase = (baseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(cleanBase))
            return null;

        var overridePath = (Configuration.AllTalkTtsPathOverride ?? "").Trim();
        string url;

        if (Uri.TryCreate(overridePath, UriKind.Absolute, out var abs))
            url = abs.ToString();
        else
            url = cleanBase + (string.IsNullOrWhiteSpace(overridePath) ? "/api/tts-generate" : (overridePath.StartsWith("/") ? overridePath : "/" + overridePath));

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("text_input", text ?? ""),
            new KeyValuePair<string, string>("text_filtering", "standard"),
            new KeyValuePair<string, string>("character_voice_gen", voiceFile ?? ""),
            new KeyValuePair<string, string>("narrator_enabled", "false"),
            new KeyValuePair<string, string>("language", string.IsNullOrWhiteSpace(Configuration.AllTalkLanguage) ? "en" : Configuration.AllTalkLanguage.Trim()),
            new KeyValuePair<string, string>("output_file_name", "npcvoicemaster"),
            new KeyValuePair<string, string>("output_file_timestamp", "true"),
            new KeyValuePair<string, string>("autoplay", "false"),
        });

        using var genResp = await http.PostAsync(url, form);
        var genBody = await genResp.Content.ReadAsByteArrayAsync();

        if (!genResp.IsSuccessStatusCode)
        {
            ChatGui.Print($"[NpcVoiceMaster] AllTalk TTS failed {(int)genResp.StatusCode}: {SafeUtf8(genBody)}");
            return null;
        }

        var json = SafeUtf8(genBody);

        string status = "";
        string outputUrl = "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var st)) status = st.GetString() ?? "";
            if (root.TryGetProperty("output_cache_url", out var cacheEl)) outputUrl = cacheEl.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(outputUrl) && root.TryGetProperty("output_file_url", out var fileEl)) outputUrl = fileEl.GetString() ?? "";
        }
        catch
        {
            ChatGui.Print("[NpcVoiceMaster] AllTalk returned invalid JSON.");
            ChatGui.Print($"[NpcVoiceMaster] Body: {json}");
            return null;
        }

        if (!string.Equals(status, "generate-success", StringComparison.OrdinalIgnoreCase))
        {
            ChatGui.Print($"[NpcVoiceMaster] AllTalk status: {status}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputUrl))
        {
            ChatGui.Print("[NpcVoiceMaster] AllTalk returned no output url.");
            return null;
        }

        var audioUrl = outputUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? outputUrl
            : cleanBase + (outputUrl.StartsWith("/") ? outputUrl : "/" + outputUrl);

        audioUrl += audioUrl.Contains("?") ? "&t=" : "?t=";
        audioUrl += DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        using var audioResp = await http.GetAsync(audioUrl);
        var wavBytes = await audioResp.Content.ReadAsByteArrayAsync();

        if (!audioResp.IsSuccessStatusCode)
        {
            ChatGui.Print($"[NpcVoiceMaster] AllTalk audio GET failed {(int)audioResp.StatusCode}: {SafeUtf8(wavBytes)}");
            return null;
        }

        return wavBytes;
    }

    private static string SafeUtf8(byte[] bytes)
    {
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return "<non-utf8 body>"; }
    }
}
