// File: Plugin.cs
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.Command;
using Dalamud.Game.NativeWrapper;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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

    private const string BuildFingerprint = "NpcVoiceMaster | Talk capture (next-frame) | 2025-12-28";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static IFramework FrameworkSvc { get; private set; } = null!;

    private readonly WindowSystem windowSystem = new("NpcVoiceMaster");
    private readonly ConfigWindow configWindow;

    private readonly HttpClient http = new();
    private readonly VoicePlayer voicePlayer = new();

    // Live Talk-window capture toggle (NOT persisted; safe-mode by default)
    private volatile bool npcCaptureEnabled = false;

    // Prevent overlapping TTS requests from multiple UI events
    private readonly SemaphoreSlim ttsGate = new(1, 1);

    // Dedupe repeated lines
    private string lastSpokenKey = "";
    private long lastSpokenTick = 0;

    // Next-frame capture queue (this is the important bit)
    private volatile bool scanQueued = false;
    private string queuedAddonName = "";
    private AtkUnitBasePtr queuedAddonPtr;

    public Configuration Configuration { get; }

    // only print once
    private bool printedOnce = false;

    // Optional debug spam toggle (leave true while testing)
    private bool debug = true;

    public Plugin()
    {
        try { voicePlayer.Log = msg => ChatGui.Print(msg); } catch { }

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += () => configWindow.IsOpen = true;

        FrameworkSvc.Update -= OnFrameworkUpdate;

        // /voiceconfig toggles window, OR enables capture: /voiceconfig on | off
        CommandManager.AddHandler("/voiceconfig", new CommandInfo((_, args) =>
        {
            var a = (args ?? "").Trim();

            if (a.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                npcCaptureEnabled = true;
                ChatGui.Print("[NpcVoiceMaster] NPC capture: ON (Talk/BattleTalk live)");
                return;
            }

            if (a.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                npcCaptureEnabled = false;
                ChatGui.Print("[NpcVoiceMaster] NPC capture: OFF (safe mode)");
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
            HelpMessage = "Generate and play a test line. Usage: /voicetest [npc=Name] [type=bucket] some text..."
        });

        CommandManager.AddHandler("/voicefinger", new CommandInfo((_, _) => PrintFingerprintAndLoadedMarker())
        {
            HelpMessage = "Print build fingerprint marker to confirm the plugin loaded."
        });

        // Live capture: capture while the Talk window is open
        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "Talk", OnAddonEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "Talk", OnAddonEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "Talk", OnAddonEvent);

        AddonLifecycle.RegisterListener(AddonEvent.PostSetup, "BattleTalk", OnAddonEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, "BattleTalk", OnAddonEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, "BattleTalk", OnAddonEvent);

        ChatGui.Print($"[NpcVoiceMaster] Loaded. NPC capture is OFF (safe mode). Type /voiceconfig on");
        PrintLoadedOnce();
    }

    public void Dispose()
    {
        try { AddonLifecycle.UnregisterListener(OnAddonEvent); } catch { }
        FrameworkSvc.Update -= OnFrameworkUpdate;

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

                if (string.IsNullOrWhiteSpace(Configuration.AllTalkBaseUrl))
                {
                    ChatGui.Print("[NpcVoiceMaster] AllTalkBaseUrl is empty. Open /voiceconfig and set it.");
                    return;
                }

                var parsed = ParseTestArgs(args);
                var npcKey = parsed.npcKey;
                var forcedBucket = parsed.forcedBucket;
                var text = parsed.text;

                if (string.IsNullOrWhiteSpace(text))
                    text = "VoiceMaster test line.";

                var wav = await SpeakNpcLineAsync(npcKey, text, forcedBucket);
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

    private (string npcKey, string forcedBucket, string text) ParseTestArgs(string args)
    {
        string npc = "VOICETEST";
        string type = "";
        string text = "";

        var s = (args ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return (npc, type, text);

        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var remaining = new List<string>();

        foreach (var p in parts)
        {
            if (p.StartsWith("npc=", StringComparison.OrdinalIgnoreCase))
                npc = p.Substring(4).Trim().TrimEnd(':');
            else if (p.StartsWith("type=", StringComparison.OrdinalIgnoreCase))
                type = p.Substring(5).Trim();
            else
                remaining.Add(p);
        }

        text = string.Join(" ", remaining);
        return (npc, type, text);
    }

    // -------------------------
    // Live Talk capture (Talk/BattleTalk)
    // -------------------------

    private void OnAddonEvent(AddonEvent type, AddonArgs args)
    {
        try
        {
            if (!npcCaptureEnabled)
                return;

            var addonName = (args.AddonName ?? "").Trim();
            if (!addonName.Equals("Talk", StringComparison.OrdinalIgnoreCase) &&
                !addonName.Equals("BattleTalk", StringComparison.OrdinalIgnoreCase))
                return;

            if (debug)
                Svc.Chat.Print($"[NpcVoiceMaster] DEBUG: OnAddonEvent addon={addonName} event={type} (queue scan)");

            // Queue a scan for NEXT FRAME. This avoids the classic "text only appears after close"
            // because the addon often populates its AtkValues after lifecycle events.
            queuedAddonName = addonName;
            queuedAddonPtr = args.Addon;
            scanQueued = true;
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "[NpcVoiceMaster] OnAddonEvent failed");
        }
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        try
        {
            if (!scanQueued)
                return;

            // Consume queue (one scan per frame max)
            scanQueued = false;

            if (!npcCaptureEnabled)
                return;

            var addonName = queuedAddonName;
            var addonPtr = queuedAddonPtr;

            if (!TryExtractTalkLine(addonPtr, out var speaker, out var text))
                return;

            // Dedupe: Talk fires a lot. We only speak "new enough" lines.
            var now = Environment.TickCount64;
            var key = $"{addonName}|{speaker}|{text}";

            if (key == lastSpokenKey && (now - lastSpokenTick) < 800)
                return;

            lastSpokenKey = key;
            lastSpokenTick = now;

            _ = SpeakCapturedLineAsync(addonName, speaker, text);
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "[NpcVoiceMaster] OnFrameworkUpdate scan failed");
        }
    }

    private async Task SpeakCapturedLineAsync(string addonName, string speaker, string text)
    {
        // If we're already speaking, drop this line on the floor.
        // (If you want "queue all lines" later, we can do that—right now we want stability.)
        if (!await ttsGate.WaitAsync(0))
            return;

        try
        {
            ChatGui.Print($"[NpcVoiceMaster] DEBUG: CAPTURE type={addonName} speaker='{speaker}' text='{text}'");

            var wav = await SpeakNpcLineAsync(speaker, text);
            if (wav == null || wav.Length < 256)
            {
                ChatGui.Print("[NpcVoiceMaster] No audio returned.");
                return;
            }

            voicePlayer.PlayAudio(wav);
        }
        catch (Exception ex)
        {
            PluginLog.Error(ex, "[NpcVoiceMaster] SpeakCapturedLineAsync failed");
        }
        finally
        {
            ttsGate.Release();
        }
    }

    private static bool TryExtractTalkLine(AtkUnitBasePtr addon, out string speaker, out string text)
    {
        speaker = "";
        text = "";

        try
        {
            var strings = new List<string>(16);

            foreach (var v in addon.AtkValues)
            {
                var obj = v.GetValue();
                if (obj is not string s)
                    continue;

                s = CollapseWhitespace(s);
                if (string.IsNullOrWhiteSpace(s))
                    continue;

                strings.Add(s.Trim());
            }

            if (strings.Count == 0)
                return false;

            strings = strings
                .Distinct(StringComparer.Ordinal)
                .Where(s => !IsNoise(s))
                .ToList();

            if (strings.Count == 0)
                return false;

            var bestText = strings
                .OrderByDescending(ScoreDialogue)
                .ThenByDescending(s => s.Length)
                .FirstOrDefault() ?? "";

            var bestSpeaker = strings
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

    // -------------------------
    // Voice selection (your existing config model)
    // -------------------------

    public (string voice, string bucketUsed, string reason) ResolveVoiceForNpc(string npcKey, string forcedBucket = "")
    {
        var npc = (npcKey ?? "").Trim();
        var buckets = Configuration.VoiceBuckets ?? new List<VoiceBucket>();

        // 1) Exact voice override
        var exactVoice = FindExactVoiceOverride(npc);
        if (!string.IsNullOrWhiteSpace(exactVoice))
            return (exactVoice, "", "EXACT-VOICE");

        // 2) Contains voice override
        var containsVoice = FindContainsVoiceOverride(npc);
        if (!string.IsNullOrWhiteSpace(containsVoice))
            return (containsVoice, "", "CONTAINS-VOICE");

        // 3) Determine bucket
        var bucket = (forcedBucket ?? "").Trim();

        if (string.IsNullOrWhiteSpace(bucket))
            bucket = FindExactBucketOverride(npc);

        if (string.IsNullOrWhiteSpace(bucket))
            bucket = FindKeywordBucketRule(npc);

        if (!string.IsNullOrWhiteSpace(bucket))
        {
            var b = buckets.FirstOrDefault(x => string.Equals((x.Name ?? "").Trim(), bucket, StringComparison.OrdinalIgnoreCase));
            var voiceList = b?.Voices?.Where(v => !string.IsNullOrWhiteSpace(v)).ToList() ?? new List<string>();

            if (voiceList.Count > 0)
            {
                // 4) Reuse assignment
                var assigned = FindAssignedVoice(npc, bucket);
                if (!string.IsNullOrWhiteSpace(assigned) && voiceList.Any(v => string.Equals(v, assigned, StringComparison.OrdinalIgnoreCase)))
                    return (assigned, bucket, "ASSIGNED-REUSE");

                // 5) Random pick, persist
                var chosen = PickRandom(voiceList);
                UpsertAssignedVoice(npc, bucket, chosen);
                Configuration.Save();
                return (chosen, bucket, "ASSIGNED-NEW");
            }

            ChatGui.Print($"[NpcVoiceMaster] Bucket '{bucket}' has no voices configured. Falling back.");
        }

        // 6) Fallback
        var fallback = string.IsNullOrWhiteSpace(Configuration.AllTalkVoice) ? "Mia.wav" : Configuration.AllTalkVoice.Trim();
        return (fallback, bucket, "FALLBACK-DEFAULT");
    }

    private string FindExactVoiceOverride(string npcKey)
    {
        var list = Configuration.NpcExactVoiceOverrides ?? new List<NpcExactVoiceOverride>();
        foreach (var r in list)
        {
            if (r == null) continue;
            if (string.Equals((r.NpcKey ?? "").Trim(), npcKey, StringComparison.OrdinalIgnoreCase))
                return (r.Voice ?? "").Trim();
        }
        return "";
    }

    private string FindContainsVoiceOverride(string npcKey)
    {
        var list = Configuration.NpcContainsVoiceRules ?? new List<NpcContainsVoiceRule>();
        foreach (var r in list)
        {
            if (r == null) continue;
            var m = (r.Match ?? "").Trim();
            var v = (r.Voice ?? "").Trim();
            if (string.IsNullOrWhiteSpace(m) || string.IsNullOrWhiteSpace(v)) continue;

            if (npcKey.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0)
                return v;
        }
        return "";
    }

    private string FindExactBucketOverride(string npcKey)
    {
        var list = Configuration.NpcExactBucketOverrides ?? new List<NpcExactBucketOverride>();
        foreach (var r in list)
        {
            if (r == null) continue;
            if (string.Equals((r.NpcKey ?? "").Trim(), npcKey, StringComparison.OrdinalIgnoreCase))
                return (r.BucketName ?? "").Trim();
        }
        return "";
    }

    private string FindKeywordBucketRule(string npcKey)
    {
        var list = Configuration.BucketKeywordRules ?? new List<BucketKeywordRule>();
        foreach (var r in list)
        {
            if (r == null) continue;
            var k = (r.Keyword ?? "").Trim();
            var b = (r.BucketName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(b)) continue;

            if (npcKey.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                return b;
        }
        return "";
    }

    private string FindAssignedVoice(string npcKey, string bucket)
    {
        var list = Configuration.NpcAssignedVoices ?? new List<NpcAssignedVoice>();
        foreach (var r in list)
        {
            if (r == null) continue;
            if (string.Equals((r.NpcKey ?? "").Trim(), npcKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((r.BucketName ?? "").Trim(), bucket, StringComparison.OrdinalIgnoreCase))
            {
                return (r.Voice ?? "").Trim();
            }
        }
        return "";
    }

    private void UpsertAssignedVoice(string npcKey, string bucket, string voice)
    {
        var list = Configuration.NpcAssignedVoices ?? new List<NpcAssignedVoice>();

        var existing = list.FirstOrDefault(r =>
            r != null &&
            string.Equals((r.NpcKey ?? "").Trim(), npcKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals((r.BucketName ?? "").Trim(), bucket, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.Voice = voice;
        }
        else
        {
            list.Add(new NpcAssignedVoice
            {
                NpcKey = npcKey,
                BucketName = bucket,
                Voice = voice
            });
        }

        Configuration.NpcAssignedVoices = list;
    }

    private static string PickRandom(List<string> list)
    {
        if (list.Count == 0) return "";
        return list[Random.Shared.Next(list.Count)].Trim();
    }

    // -------------------------
    // Speak NPC line (cache + AllTalk)
    // -------------------------

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

        // Cache key includes resolved voice and output-affecting settings
        var settingsBlob =
            "npc=" + npc + "\n" +
            "text=" + line + "\n" +
            "voice=" + voice + "\n" +
            "bucket=" + (bucketUsed ?? "") + "\n" +
            "baseUrl=" + baseUrl + "\n" +
            "lang=" + (Configuration.AllTalkLanguage ?? "") + "\n" +
            "ttsPathOverride=" + (Configuration.AllTalkTtsPathOverride ?? "") + "\n" +
            "voiceNameLegacy=" + (Configuration.AllTalkVoiceName ?? "");

        if (Configuration.EnableCache)
        {
            var cached = TryLoadFromCache(settingsBlob);
            if (cached != null)
                return cached;
        }

        var wav = await AllTalkPreviewGenerateAndDownloadAsync(baseUrl, voice, line);

        if (wav != null && wav.Length > 0 && Configuration.EnableCache)
            SaveToCache(settingsBlob, wav);

        return wav;
    }

    private byte[]? TryLoadFromCache(string keyBlob)
    {
        try
        {
            var folder = Configuration.GetEffectiveCacheFolder();
            if (string.IsNullOrWhiteSpace(folder))
                return null;

            Directory.CreateDirectory(folder);

            var hash = Sha256Hex(keyBlob);
            var path = Path.Combine(folder, $"{hash}.wav");
            if (!File.Exists(path))
                return null;

            return File.ReadAllBytes(path);
        }
        catch
        {
            return null;
        }
    }

    private void SaveToCache(string keyBlob, byte[] wav)
    {
        try
        {
            var folder = Configuration.GetEffectiveCacheFolder();
            if (string.IsNullOrWhiteSpace(folder))
                return;

            Directory.CreateDirectory(folder);

            var hash = Sha256Hex(keyBlob);
            var path = Path.Combine(folder, $"{hash}.wav");
            File.WriteAllBytes(path, wav);
        }
        catch
        {
            // ignore
        }
    }

    private static string Sha256Hex(string input)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input ?? "");
        var hash = sha.ComputeHash(bytes);

        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    // -------------------------
    // Voices API (dropdown)
    // -------------------------

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

    // -------------------------
    // AllTalk: standard generate flow (/api/tts-generate) + download WAV
    // -------------------------
    private async Task<byte[]?> AllTalkPreviewGenerateAndDownloadAsync(string baseUrl, string voiceFile, string text)
    {
        var cleanBase = (baseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(cleanBase))
            return null;

        // Optional override in settings (relative like /api/tts-generate, or absolute)
        var overridePath = (Configuration.AllTalkTtsPathOverride ?? "").Trim();
        string url;

        if (Uri.TryCreate(overridePath, UriKind.Absolute, out var abs))
            url = abs.ToString();
        else
            url = cleanBase + (string.IsNullOrWhiteSpace(overridePath) ? "/api/tts-generate" : (overridePath.StartsWith("/") ? overridePath : "/" + overridePath));

        // If user points override to OpenAI-compatible endpoint, switch payload style
        if (url.Contains("/v1/audio/speech", StringComparison.OrdinalIgnoreCase))
        {
            var payload = new
            {
                model = "any_model_name",
                input = text ?? "",
                voice = (voiceFile ?? "").Replace(".wav", "", StringComparison.OrdinalIgnoreCase),
                response_format = "wav",
                speed = 1.0
            };

            using var jsonContent = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(url, jsonContent);
            var bytes = await resp.Content.ReadAsByteArrayAsync();

            if (!resp.IsSuccessStatusCode)
            {
                ChatGui.Print($"[NpcVoiceMaster] AllTalk OpenAI-style TTS failed {(int)resp.StatusCode}: {SafeUtf8(bytes)}");
                return null;
            }

            return bytes;
        }

        // Standard AllTalk endpoint uses form fields
        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("text_input", text ?? ""),
            new KeyValuePair<string, string>("text_filtering", "standard"),
            new KeyValuePair<string, string>("character_voice_gen", voiceFile ?? ""),
            new KeyValuePair<string, string>("narrator_enabled", "false"),
            new KeyValuePair<string, string>("language", string.IsNullOrWhiteSpace(Configuration.AllTalkLanguage) ? "en" : Configuration.AllTalkLanguage!.Trim()),
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

        string json = SafeUtf8(genBody);

        string status = "";
        string outputUrl = "";

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("status", out var st))
                status = st.GetString() ?? "";

            if (root.TryGetProperty("output_cache_url", out var cacheEl))
                outputUrl = cacheEl.GetString() ?? "";

            if (string.IsNullOrWhiteSpace(outputUrl) && root.TryGetProperty("output_file_url", out var fileEl))
                outputUrl = fileEl.GetString() ?? "";
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
            var err = SafeUtf8(wavBytes);
            ChatGui.Print($"[NpcVoiceMaster] AllTalk audio GET failed {(int)audioResp.StatusCode}: {err}");
            return null;
        }

        return wavBytes;
    }

    // -------------------------
    // Helpers
    // -------------------------

    private static string SafeUtf8(byte[] bytes)
    {
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return "<non-utf8 body>"; }
    }

    private void PrintLoadedOnce()
    {
        if (printedOnce) return;
        printedOnce = true;

        try
        {
            ChatGui.Print($"[NpcVoiceMaster] LOADED BUILD: {DateTime.Now:yyyy-MM-dd HH:mm:ss} | {BuildFingerprint}");
        }
        catch
        {
            PluginLog.Information($"[NpcVoiceMaster] LOADED BUILD (fallback log): {DateTime.Now:O} | {BuildFingerprint}");
        }
    }

    private void PrintFingerprintAndLoadedMarker()
    {
        try
        {
            ChatGui.Print($"[NpcVoiceMaster] BUILD FINGERPRINT: {BuildFingerprint}");
            ChatGui.Print($"[NpcVoiceMaster] LOADED MARKER NOW: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        }
        catch
        {
            PluginLog.Information($"[NpcVoiceMaster] BUILD FINGERPRINT (fallback): {BuildFingerprint}");
        }
    }
}
