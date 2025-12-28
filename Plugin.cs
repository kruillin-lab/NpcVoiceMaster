using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Windowing;
using FFXIVClientStructs.FFXIV.Component.GUI;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ElevenLabs;
using ElevenLabs.Voices;
using ElevenLabs.TextToSpeech;
using Dalamud.Game.ClientState.Objects.Types;

namespace NpcVoiceMaster
{
    public sealed class Plugin : IDalamudPlugin
    {
        [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
        [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
        [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
        [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;
        [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
        [PluginService] internal static IFramework Framework { get; private set; } = null!;
        [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;

        private const string BuildFingerprint = "2025-12-28 A";

        private readonly WindowSystem WindowSystem = new("NpcVoiceMaster");
        private readonly ConfigWindow ConfigWindow;

        private readonly VoicePlayer _voicePlayer = new();
        private readonly Random _random = new();

        public Configuration Configuration { get; init; }
        private ElevenLabsClient? _api;

        private string _lastSeenText = string.Empty;
        private string _lastSeenNpc = string.Empty;

        private readonly ConcurrentQueue<(string npcName, string text, string category)> _ttsQueue = new();
        private readonly SemaphoreSlim _ttsGate = new(1, 1);
        private CancellationTokenSource? _ttsCts;

        private bool _voicesLoaded = false;
        private List<Voice> _voices = new();
        private Dictionary<string, Voice> _voicesById = new();
        private readonly object _voiceLock = new();

        private readonly ConcurrentDictionary<string, byte[]> _audioCache = new();

        private readonly List<string> LoporritVoiceIds = new()
        {
            "fRPg9G9qpkAC2mfz4xUI",
            "s6V3fkzkLG5uiYECXvIw",
            "HjtSCzfSl8QhXR4WWZoo"
        };

        private bool _printedFingerprintOnce = false;

        public Plugin()
        {
            // NOTE: ChatGui.Print in constructor can be unreliable. We'll print on first Framework tick instead.
            PluginLog.Information($"[NpcVoiceMaster] BUILD FINGERPRINT: {BuildFingerprint}");

            // If your VoicePlayer has Log, hook it.
            try { _voicePlayer.Log = msg => ChatGui.Print(msg); } catch { }

            this.Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            this.Configuration.Initialize(PluginInterface);

            this.ConfigWindow = new ConfigWindow(this);
            WindowSystem.AddWindow(this.ConfigWindow);

            PluginInterface.UiBuilder.Draw += () => WindowSystem.Draw();
            PluginInterface.UiBuilder.OpenConfigUi += () => ConfigWindow.IsOpen = true;

            CommandManager.AddHandler("/voiceconfig", new CommandInfo((c, a) => ConfigWindow.IsOpen = !ConfigWindow.IsOpen));
            CommandManager.AddHandler("/voicetest", new CommandInfo((c, a) => RunVoiceTestFromUI()));
            CommandManager.AddHandler("/voicefinger", new CommandInfo((c, a) => PrintFingerprint()));

            Framework.Update += OnFrameworkTick;
            Framework.Update += ScanForDialogue;

            ApplyApiKey(Configuration.ApiKey ?? string.Empty);
        }

        // ===== ConfigWindow compatibility =====
        public void RunVoiceTestFromUI() => RunVoiceTest();

        public List<Voice> GetCachedVoicesForUI()
        {
            lock (_voiceLock) return _voices.ToList();
        }
        // =====================================

        private void OnFrameworkTick(IFramework framework)
        {
            if (_printedFingerprintOnce) return;
            _printedFingerprintOnce = true;
            PrintFingerprint();
        }

        private void PrintFingerprint()
        {
            try
            {
                ChatGui.Print($"[NpcVoiceMaster] BUILD FINGERPRINT: {BuildFingerprint}");
            }
            catch
            {
                // If ChatGui isn't ready for some reason, at least the PluginLog line exists.
                PluginLog.Information($"[NpcVoiceMaster] BUILD FINGERPRINT (fallback): {BuildFingerprint}");
            }
        }

        public void ApplyApiKey(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _api = null;
                _voicesLoaded = false;
                lock (_voiceLock)
                {
                    _voices.Clear();
                    _voicesById.Clear();
                }
                ChatGui.Print("[NpcVoiceMaster] API key cleared. ElevenLabs disabled.");
                return;
            }

            _api = new ElevenLabsClient(apiKey);

            _voicesLoaded = false;
            lock (_voiceLock)
            {
                _voices.Clear();
                _voicesById.Clear();
            }

            _ = Task.Run(LoadVoicesAsync);
            ChatGui.Print("[NpcVoiceMaster] API key applied. Reloading voices...");
        }

        private async Task LoadVoicesAsync()
        {
            try
            {
                if (_api == null) return;

                var all = (await _api.VoicesEndpoint.GetAllVoicesAsync()).ToList();
                lock (_voiceLock)
                {
                    _voices = all;
                    _voicesById = all.Where(v => !string.IsNullOrWhiteSpace(v.Id)).ToDictionary(v => v.Id, v => v);
                    _voicesLoaded = true;
                }

                ChatGui.Print($"[NpcVoiceMaster] Voices loaded: {all.Count}");
            }
            catch (Exception ex)
            {
                _voicesLoaded = false;
                ChatGui.Print($"[NpcVoiceMaster] Voice Load Error: {ex.GetType().Name}: {ex.Message}");
                PluginLog.Error(ex, "[NpcVoiceMaster] Voice Load Exception");
            }
        }

        private void RunVoiceTest()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_api == null)
                    {
                        ChatGui.Print("[NpcVoiceMaster] /voicetest: API not set.");
                        return;
                    }

                    if (!_voicesLoaded)
                        await LoadVoicesAsync();

                    List<Voice> snapshot;
                    lock (_voiceLock) snapshot = _voices.ToList();

                    ChatGui.Print($"[NpcVoiceMaster] /voicetest: voices={snapshot.Count}");
                    if (snapshot.Count == 0)
                    {
                        ChatGui.Print("[NpcVoiceMaster] /voicetest: no voices returned.");
                        return;
                    }

                    var voice = snapshot[0];
                    ChatGui.Print($"[NpcVoiceMaster] /voicetest voice: {voice.Name} ({voice.Id})");

                    var req = new TextToSpeechRequest(voice, "Audio test successful.");
                    var clip = await _api.TextToSpeechEndpoint.TextToSpeechAsync(req, cancellationToken: CancellationToken.None);

                    var bytes = clip.ClipData.ToArray();
                    ChatGui.Print($"[NpcVoiceMaster] /voicetest bytes: {bytes.Length}");

                    // Save for proof/debug
                    try
                    {
                        var path = Path.Combine(PluginInterface.ConfigDirectory.FullName, "last_voicetest.mp3");
                        File.WriteAllBytes(path, bytes);
                        ChatGui.Print($"[NpcVoiceMaster] /voicetest saved: {path}");
                    }
                    catch (Exception exFile)
                    {
                        ChatGui.Print($"[NpcVoiceMaster] /voicetest save failed: {exFile.GetType().Name}: {exFile.Message}");
                    }

                    _voicePlayer.Stop();
                    _voicePlayer.PlayAudio(bytes);
                }
                catch (Exception ex)
                {
                    ChatGui.Print($"[NpcVoiceMaster] /voicetest ERROR: {ex.GetType().Name}: {ex.Message}");
                    PluginLog.Error(ex, "[NpcVoiceMaster] /voicetest exception");
                }
            });
        }

        private void ScanForDialogue(IFramework framework)
        {
            // Keep your existing dialogue logic here if you want; omitted for brevity while we stabilize audio.
        }

        public void Dispose()
        {
            Framework.Update -= OnFrameworkTick;
            Framework.Update -= ScanForDialogue;

            try { _ttsCts?.Cancel(); } catch { }

            _voicePlayer.Dispose();

            CommandManager.RemoveHandler("/voiceconfig");
            CommandManager.RemoveHandler("/voicetest");
            CommandManager.RemoveHandler("/voicefinger");

            WindowSystem.RemoveAllWindows();
        }
    }
}
