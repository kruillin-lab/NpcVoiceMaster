// File: Plugin.cs
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
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
using System.Threading.Tasks;

namespace NpcVoiceMaster;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string BuildFingerprint = "NpcVoiceMaster | buckets + random assign + manual override | 2025-12-28";

    private readonly WindowSystem windowSystem = new("NpcVoiceMaster");
    private readonly ConfigWindow configWindow;
    private readonly VoicePlayer voicePlayer = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public Configuration Configuration { get; }

    private bool printedOnce;

    public Plugin()
    {
        PluginLog.Information($"[NpcVoiceMaster] BUILD FINGERPRINT: {BuildFingerprint}");

        try { voicePlayer.Log = msg => ChatGui.Print(msg); } catch { }

        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        PluginInterface.UiBuilder.Draw += DrawUI;
        PluginInterface.UiBuilder.OpenConfigUi += () => configWindow.IsOpen = true;

        CommandManager.AddHandler("/voiceconfig", new CommandInfo((_, _) => configWindow.IsOpen = !configWindow.IsOpen)
        {
            HelpMessage = "Open NpcVoiceMaster settings."
        });

        CommandManager.AddHandler("/voicetest", new CommandInfo((_, args) => RunVoiceTest(args))
        {
            HelpMessage = "Test TTS. Usage: /voicetest npc=Cid type=male Hello"
        });

        CommandManager.AddHandler("/voicefinger", new CommandInfo((_, _) => PrintFingerprintAndLoadedMarker())
        {
            HelpMessage = "Print build fingerprint + loaded timestamp."
        });

        Framework.Update += OnFrameworkTick;
    }

    public void Dispose()
    {
        try { Framework.Update -= OnFrameworkTick; } catch { }
        try { CommandManager.RemoveHandler("/voiceconfig"); } catch { }
        try { CommandManager.RemoveHandler("/voicetest"); } catch { }
        try { CommandManager.RemoveHandler("/voicefinger"); } catch { }
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

    private static (string npcKey, string forcedBucket, string text) ParseTestArgs(string args)
    {
        // Supports:
        // /voicetest npc=Cid type=male Hello there
        // /voicetest Hello there
        string npc = "VOICETEST";
        string type = "";
        string text = "";

        var s = (args ?? "").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return (npc, type, text);

        // Tokenize by spaces, parse npc= and type=
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var remaining = new List<string>();

        foreach (var p in parts)
        {
            if (p.StartsWith("npc=", StringComparison.OrdinalIgnoreCase))
                npc = p.Substring(4).Trim().TrimEnd(':');
            else if (p.StartsWith("type=", StringComparison.OrdinalIgnoreCase))
                type = p.Substring(5).Trim().TrimEnd(':');
            else
                remaining.Add(p);
        }

        text = string.Join(" ", remaining).Trim();
        return (npc, type, text);
    }

    // -------------------------
    // Voice resolution pipeline
    // -------------------------

    /// <summary>
    /// Resolve voice for an NPC:
    /// 1) Exact NPC -> Voice override
    /// 2) Contains NPC -> Voice override
    /// 3) Determine bucket (forcedBucket OR exact NPC -> bucket OR keyword rules OR "" )
    /// 4) If NPC already has assigned voice for that bucket, reuse it
    /// 5) Else randomly pick from bucket, persist assignment
    /// 6) Fallback to Configuration.AllTalkVoice
    /// </summary>
    public (string voice, string bucketUsed, string reason) ResolveVoiceForNpc(string npcKey, string forcedBucket = "")
    {
        var npc = (npcKey ?? "").Trim();
        var buckets = Configuration.VoiceBuckets ?? new List<VoiceBucket>();

        // 1) Exact voice override
        var exactVoice = FindExactVoiceOverride(npc);
        if (!string.IsNullOrWhiteSpace(exactVoice))
            return (exactVoice, "", "EXACT-VOICE-OVERRIDE");

        // 2) Contains voice override
        var containsVoice = FindContainsVoiceOverride(npc);
        if (!string.IsNullOrWhiteSpace(containsVoice))
            return (containsVoice, "", "CONTAINS-VOICE-OVERRIDE");

        // 3) Determine bucket
        var bucket = (forcedBucket ?? "").Trim();
        if (string.IsNullOrWhiteSpace(bucket))
            bucket = FindExactBucketOverride(npc);

        if (string.IsNullOrWhiteSpace(bucket))
            bucket = ClassifyBucketByKeyword(npc);

        bucket = (bucket ?? "").Trim().ToLowerInvariant();

        // If bucket exists and has voices, do assigned/random
        if (!string.IsNullOrWhiteSpace(bucket))
        {
            var b = buckets.FirstOrDefault(x => string.Equals((x?.Name ?? "").Trim(), bucket, StringComparison.OrdinalIgnoreCase));
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
                return (chosen, bucket, "ASSIGNED-NEW-RANDOM");
            }
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

    private string ClassifyBucketByKeyword(string npcKey)
    {
        var rules = Configuration.BucketKeywordRules ?? new List<BucketKeywordRule>();
        foreach (var r in rules)
        {
            if (r == null) continue;
            var k = (r.Keyword ?? "").Trim();
            var b = (r.BucketName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(b)) continue;

            if (npcKey.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                return b;
        }

        // No match means "no bucket"
        return "";
    }

    private string FindAssignedVoice(string npcKey, string bucket)
    {
        var list = Configuration.NpcAssignedVoices ?? new List<NpcAssignedVoice>();
        foreach (var a in list)
        {
            if (a == null) continue;
            if (string.Equals((a.NpcKey ?? "").Trim(), npcKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((a.BucketName ?? "").Trim(), bucket, StringComparison.OrdinalIgnoreCase))
                return (a.Voice ?? "").Trim();
        }
        return "";
    }

    private void UpsertAssignedVoice(string npcKey, string bucket, string voice)
    {
        Configuration.NpcAssignedVoices ??= new List<NpcAssignedVoice>();
        var list = Configuration.NpcAssignedVoices;

        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (a == null) continue;

            if (string.Equals((a.NpcKey ?? "").Trim(), npcKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((a.BucketName ?? "").Trim(), bucket, StringComparison.OrdinalIgnoreCase))
            {
                a.Voice = voice;
                list[i] = a;
                return;
            }
        }

        list.Add(new NpcAssignedVoice { NpcKey = npcKey, BucketName = bucket, Voice = voice });
    }

    private static string PickRandom(List<string> list)
    {
        if (list == null || list.Count == 0) return "";
        int idx = RandomNumberGenerator.GetInt32(list.Count);
        return list[idx];
    }

    // -------------------------
    // Main speaking + cache
    // -------------------------

    /// <summary>
    /// Speak NPC line with disk cache.
    /// forcedBucket is optional; used for /voicetest type=...
    /// </summary>
    public async Task<byte[]?> SpeakNpcLineAsync(string npcKey, string text, string forcedBucket = "")
    {
        var baseUrl = (Configuration.AllTalkBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        var npc = (npcKey ?? "").Trim();
        var line = (text ?? "");

        var (voice, bucketUsed, reason) = ResolveVoiceForNpc(npc, forcedBucket);

        // Log resolution
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
            var folder = (Configuration.GetEffectiveCacheFolder() ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(folder))
            {
                try
                {
                    Directory.CreateDirectory(folder);

                    var hash = Sha256Hex(settingsBlob);
                    var cachePath = Path.Combine(folder, $"{hash}.wav");

                    if (File.Exists(cachePath))
                    {
                        var bytes = await File.ReadAllBytesAsync(cachePath);
                        ChatGui.Print($"[NpcVoiceMaster] CACHE HIT  ({hash}) voice={voice} npc={npc}");
                        return bytes;
                    }

                    ChatGui.Print($"[NpcVoiceMaster] CACHE MISS ({hash}) voice={voice} npc={npc}");

                    var generated = await AllTalkPreviewGenerateAndDownloadAsync(baseUrl, voice, line);
                    if (generated == null || generated.Length < 256)
                        return generated;

                    try
                    {
                        await File.WriteAllBytesAsync(cachePath, generated);
                        ChatGui.Print($"[NpcVoiceMaster] CACHE WRITE ({hash}) -> {cachePath}");
                    }
                    catch (Exception wex)
                    {
                        ChatGui.Print($"[NpcVoiceMaster] CACHE WRITE FAILED ({hash}): {wex.Message}");
                        PluginLog.Warning(wex, "[NpcVoiceMaster] cache write failed");
                    }

                    return generated;
                }
                catch (Exception cex)
                {
                    ChatGui.Print($"[NpcVoiceMaster] CACHE ERROR: {cex.Message} (falling back to live TTS)");
                    PluginLog.Warning(cex, "[NpcVoiceMaster] cache error");
                }
            }
        }

        return await AllTalkPreviewGenerateAndDownloadAsync(baseUrl, voice, line);
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
            var body = SafeUtf8(bytes);
            ChatGui.Print($"[NpcVoiceMaster] /api/voices failed {(int)resp.StatusCode}: {body}");
            return new List<string>();
        }

        var json = SafeUtf8(bytes);

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("voices", out var voicesProp) || voicesProp.ValueKind != JsonValueKind.Array)
                return new List<string>();

            var list = new List<string>();
            foreach (var v in voicesProp.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.String)
                    list.Add(v.GetString() ?? "");
            }

            list.RemoveAll(string.IsNullOrWhiteSpace);
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }
        catch
        {
            ChatGui.Print("[NpcVoiceMaster] /api/voices returned invalid JSON.");
            return new List<string>();
        }
    }

    // -------------------------
    // AllTalk: previewvoice flow
    // -------------------------

    private async Task<byte[]?> AllTalkPreviewGenerateAndDownloadAsync(string baseUrl, string voiceFile, string text)
    {
        var previewUrl = $"{baseUrl}/api/previewvoice/";

        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("voice", voiceFile),
            new KeyValuePair<string, string>("text", text),
        });

        using var resp = await http.PostAsync(previewUrl, form);
        var bodyBytes = await resp.Content.ReadAsByteArrayAsync();

        if (!resp.IsSuccessStatusCode)
        {
            var err = SafeUtf8(bodyBytes);
            ChatGui.Print($"[NpcVoiceMaster] AllTalk preview POST failed {(int)resp.StatusCode}: {err}");
            return null;
        }

        string json = SafeUtf8(bodyBytes);
        string? outputFileUrl = null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("output_file_url", out var urlProp))
                outputFileUrl = urlProp.GetString();
        }
        catch
        {
            ChatGui.Print("[NpcVoiceMaster] AllTalk preview returned non-JSON.");
            ChatGui.Print($"[NpcVoiceMaster] Body: {json}");
            return null;
        }

        if (string.IsNullOrWhiteSpace(outputFileUrl))
        {
            ChatGui.Print("[NpcVoiceMaster] AllTalk preview JSON missing output_file_url.");
            ChatGui.Print($"[NpcVoiceMaster] Body: {json}");
            return null;
        }

        var audioUrl = outputFileUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? outputFileUrl
            : baseUrl + outputFileUrl;

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

    private static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input ?? "");
        var hash = SHA256.HashData(bytes);

        var sb = new StringBuilder(hash.Length * 2);
        for (int i = 0; i < hash.Length; i++)
            sb.Append(hash[i].ToString("x2"));

        return sb.ToString();
    }

    private void OnFrameworkTick(IFramework _)
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
