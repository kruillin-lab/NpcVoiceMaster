// File: Plugin.cs
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace NpcVoiceMaster;

/// <summary>
/// NpcVoiceMaster (AllTalk-only)
/// - No ElevenLabs code or dependencies
/// - Uses AllTalk (your server on 7851) for /voicetest
/// - Adds /voicefinger so you can prove the correct DLL is loaded
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private const string BuildFingerprint = "NpcVoiceMaster | AllTalk-only | 2025-12-28";

    private readonly WindowSystem windowSystem = new("NpcVoiceMaster");
    private readonly ConfigWindow configWindow;
    private readonly VoicePlayer voicePlayer = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public Configuration Configuration { get; }

    private bool printedOnce;

    public Plugin()
    {
        PluginLog.Information($"[NpcVoiceMaster] BUILD FINGERPRINT: {BuildFingerprint}");

        try { voicePlayer.Log = msg => ChatGui.Print(msg); } catch { /* ignore */ }

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

        CommandManager.AddHandler("/voicetest", new CommandInfo((_, _) => RunVoiceTestFromUI())
        {
            HelpMessage = "Run a quick AllTalk TTS playback test."
        });

        CommandManager.AddHandler("/voicefinger", new CommandInfo((_, _) => PrintFingerprintAndLoadedMarker())
        {
            HelpMessage = "Print build fingerprint + loaded timestamp."
        });

        Framework.Update += OnFrameworkTick;
    }

    private void DrawUI() => windowSystem.Draw();

    // Called by the UI
    public void RunVoiceTestFromUI() => RunVoiceTest();

    private void OnFrameworkTick(IFramework _)
    {
        if (printedOnce) return;
        printedOnce = true;

        // This is your “lie detector”: if you reload the plugin and don’t see this,
        // Dalamud is not loading the DLL you just built.
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

    private void RunVoiceTest()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Configuration.AllTalkBaseUrl))
                {
                    ChatGui.Print("[NpcVoiceMaster] AllTalk Base URL is empty. Open /voiceconfig and set it.");
                    return;
                }

                ChatGui.Print($"[NpcVoiceMaster] /voicetest: Trying AllTalk at {Configuration.AllTalkBaseUrl} ...");

                var bytes = await TryAllTalkVoiceTestAsync();
                if (bytes is not { Length: > 0 })
                {
                    ChatGui.Print("[NpcVoiceMaster] /voicetest: AllTalk returned no audio. Check your endpoint path / payload.");
                    return;
                }

                ChatGui.Print($"[NpcVoiceMaster] /voicetest (AllTalk) bytes: {bytes.Length}");
                SaveLastVoiceTest(bytes);

                voicePlayer.Stop();
                voicePlayer.PlayAudio(bytes);
            }
            catch (Exception ex)
            {
                ChatGui.Print($"[NpcVoiceMaster] /voicetest ERROR: {ex.GetType().Name}: {ex.Message}");
                PluginLog.Error(ex, "[NpcVoiceMaster] /voicetest exception");
            }
        });
    }

    private void SaveLastVoiceTest(byte[] bytes)
    {
        try
        {
            // AllTalk very commonly returns WAV. If it returns MP3, we still save as .wav,
            // but VoicePlayer will detect format when playing.
            var path = Path.Combine(PluginInterface.ConfigDirectory.FullName, "last_voicetest_audio.bin");
            File.WriteAllBytes(path, bytes);
            ChatGui.Print($"[NpcVoiceMaster] /voicetest saved: {path}");
        }
        catch (Exception exFile)
        {
            ChatGui.Print($"[NpcVoiceMaster] /voicetest save failed: {exFile.GetType().Name}: {exFile.Message}");
        }
    }

    private async Task<byte[]?> TryAllTalkVoiceTestAsync()
    {
        var baseUrl = (Configuration.AllTalkBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return null;

        // If your AllTalk endpoint path differs, set it in the UI:
        // "AllTalk TTS Path Override"
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(Configuration.AllTalkTtsPathOverride))
            candidates.Add(Configuration.AllTalkTtsPathOverride.Trim());

        // Common guesses (override if needed)
        candidates.Add("/api/tts");
        candidates.Add("/tts");
        candidates.Add("/generate");
        candidates.Add("/api/generate");

        foreach (var path in candidates)
        {
            var url = baseUrl + NormalizePath(path);

            try
            {
                // Generic payload. If your server needs different keys, you’ll see a 400 in the plugin log.
                var payload = new
                {
                    text = "Audio test successful from AllTalk (port 7851).",
                    voice = Configuration.AllTalkVoiceName,
                    language = Configuration.AllTalkLanguage,
                    streaming = false
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = JsonContent.Create(payload)
                };

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
                if (!resp.IsSuccessStatusCode)
                {
                    var body = await SafeReadTextAsync(resp);
                    PluginLog.Warning($"[NpcVoiceMaster] AllTalk POST {url} -> {(int)resp.StatusCode}. Body: {Truncate(body, 400)}");
                    continue;
                }

                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";

                // If the response is raw audio bytes (best case)
                if (contentType.Contains("audio", StringComparison.OrdinalIgnoreCase) ||
                    contentType.Contains("octet-stream", StringComparison.OrdinalIgnoreCase))
                {
                    return await resp.Content.ReadAsByteArrayAsync();
                }

                // If JSON, try to extract base64 audio
                var jsonText = await resp.Content.ReadAsStringAsync();
                var maybeBytes = TryExtractAudioBytesFromJson(jsonText);
                if (maybeBytes is { Length: > 0 })
                    return maybeBytes;

                PluginLog.Warning($"[NpcVoiceMaster] AllTalk response from {url} not recognized. ContentType={contentType}. Body: {Truncate(jsonText, 400)}");
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, $"[NpcVoiceMaster] AllTalk call failed: {url}");
            }
        }

        return null;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        return path.StartsWith("/") ? path : "/" + path;
    }

    private static async Task<string> SafeReadTextAsync(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync(); }
        catch { return ""; }
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        if (s.Length <= max) return s;
        return s.Substring(0, max) + "...";
    }

    private static byte[]? TryExtractAudioBytesFromJson(string jsonText)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;

            if (root.TryGetProperty("audio_base64", out var b64El) && b64El.ValueKind == JsonValueKind.String)
            {
                var b64 = b64El.GetString();
                if (!string.IsNullOrWhiteSpace(b64))
                    return Convert.FromBase64String(b64);
            }

            if (root.TryGetProperty("audio", out var audioEl) && audioEl.ValueKind == JsonValueKind.String)
            {
                var maybeB64 = audioEl.GetString();
                if (!string.IsNullOrWhiteSpace(maybeB64))
                {
                    try { return Convert.FromBase64String(maybeB64); } catch { }
                }
            }
        }
        catch
        {
            // ignore parse errors
        }

        return null;
    }

    public void Dispose()
    {
        Framework.Update -= OnFrameworkTick;

        PluginInterface.UiBuilder.Draw -= DrawUI;
        // Note: OpenConfigUi uses a lambda; removing it cleanly requires storing the delegate.
        // It’s harmless in practice because Plugin is disposed once.
        // PluginInterface.UiBuilder.OpenConfigUi -= () => configWindow.IsOpen = true;

        CommandManager.RemoveHandler("/voiceconfig");
        CommandManager.RemoveHandler("/voicetest");
        CommandManager.RemoveHandler("/voicefinger");

        try { voicePlayer.Dispose(); } catch { }
        try { http.Dispose(); } catch { }

        windowSystem.RemoveAllWindows();
    }
}
