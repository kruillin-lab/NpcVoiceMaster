using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NPCVoiceMaster
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "NPC Voice Master";

        private const string Cmd1 = "/npcvoice";
        private const string Cmd2 = "/nvm";

        private readonly IDalamudPluginInterface _pi;
        private readonly ICommandManager _commands;

        public Configuration Configuration { get; private set; }

        public WindowSystem WindowSystem { get; }
        private ConfigWindow ConfigWindow { get; }

        private readonly HttpClient _httpClient = new HttpClient();
        private readonly VoicePlayer _player = new VoicePlayer();
        private readonly Random _rng = new Random();

        // Split on anything not letter/number (so "woman_Alice.wav" => ["woman","alice"])
        private static readonly Regex TokenSplit = new Regex(@"[^a-zA-Z0-9]+", RegexOptions.Compiled);

        public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager)
        {
            _pi = pluginInterface;
            _commands = commandManager;

            _pi.Create<Svc>();

            Configuration = LoadConfigurationSafe();

            WindowSystem = new WindowSystem("NpcVoiceMaster");
            ConfigWindow = new ConfigWindow(this);
            WindowSystem.AddWindow(ConfigWindow);

            _pi.UiBuilder.Draw += DrawUI;
            _pi.UiBuilder.OpenConfigUi += DrawConfigUI;

            _commands.AddHandler(Cmd1, new CommandInfo(OnCommand) { HelpMessage = "Open NPC Voice Master config." });
            _commands.AddHandler(Cmd2, new CommandInfo(OnCommand) { HelpMessage = "Open NPC Voice Master config." });

            Svc.Chat.ChatMessage += OnChatMessage;

            Svc.Log.Information($"[NpcVoiceMaster] Loaded. Commands: {Cmd1}, {Cmd2}");
            Svc.Chat.Print($"[NpcVoiceMaster] Loaded. Type {Cmd2} to open config.");
        }

        public void Dispose()
        {
            Svc.Chat.ChatMessage -= OnChatMessage;

            _commands.RemoveHandler(Cmd1);
            _commands.RemoveHandler(Cmd2);

            _pi.UiBuilder.Draw -= DrawUI;
            _pi.UiBuilder.OpenConfigUi -= DrawConfigUI;

            WindowSystem.RemoveAllWindows();
            ConfigWindow.Dispose();

            _player.Dispose();
            _httpClient.Dispose();
        }

        private Configuration LoadConfigurationSafe()
        {
            try
            {
                var cfg = _pi.GetPluginConfig() as Configuration;
                if (cfg == null)
                {
                    cfg = new Configuration();
                    cfg.Initialize(_pi);
                    cfg.Save();
                    return cfg;
                }

                cfg.Initialize(_pi);
                return cfg;
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[NpcVoiceMaster] Config failed to load (bad/old JSON). Resetting config to defaults.");

                var cfg = new Configuration();
                cfg.Initialize(_pi);
                cfg.Save();

                try { Svc.Chat.Print("[NpcVoiceMaster] Your saved config was incompatible and was reset to defaults."); }
                catch { }

                return cfg;
            }
        }

        private void OnCommand(string command, string args)
        {
            Svc.Chat.Print($"[NpcVoiceMaster] Command received: {command}");
            ConfigWindow.IsOpen = !ConfigWindow.IsOpen;
            Svc.Log.Information($"[NpcVoiceMaster] Command fired: {command}. ConfigWindow.IsOpen={ConfigWindow.IsOpen}");
        }

        private void DrawUI() => WindowSystem.Draw();
        private void DrawConfigUI() => ConfigWindow.IsOpen = true;

        // =========================
        //  NPC Detection + TTS
        // =========================

        private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            if (!Configuration.Enabled)
                return;

            var npcName = (sender.TextValue ?? "").Trim();
            var text = (message.TextValue ?? "").Trim();

            if (Configuration.DebugLogCandidateChat)
            {
                if (!string.IsNullOrWhiteSpace(text))
                    Svc.Log.Debug($"[NpcVoiceMaster] CHAT type={(ushort)type} sender='{npcName}' msg='{text}'");
            }

            var isNpcDialogue =
                type == XivChatType.NPCDialogue ||
                type == XivChatType.NPCDialogueAnnouncements;

            if (!isNpcDialogue)
                return;

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (string.IsNullOrWhiteSpace(npcName))
                npcName = "Unknown";

            Svc.Log.Debug($"[NpcVoiceMaster] NPC Dialogue detected: '{npcName}' -> '{text}'");

            _ = Task.Run(async () =>
            {
                try
                {
                    var voice = ResolveVoiceForNpc(npcName);
                    if (string.IsNullOrWhiteSpace(voice))
                    {
                        Svc.Log.Debug($"[NpcVoiceMaster] No voice available for '{npcName}'. Add voices to your default bucket.");
                        return;
                    }

                    var audio = await GenerateTtsAndDownloadAsync(text, voice);
                    if (audio == null || audio.Length == 0)
                    {
                        Svc.Log.Debug("[NpcVoiceMaster] AllTalk returned no audio.");
                        return;
                    }

                    _player.PlayAudio(audio);
                }
                catch (Exception ex)
                {
                    Svc.Log.Error(ex, "[NpcVoiceMaster] Failed to generate/play TTS.");
                }
            });
        }

        private string ResolveVoiceForNpc(string npcName)
        {
            var ov = Configuration.NpcExactVoiceOverrides.FirstOrDefault(x =>
                x.Enabled &&
                string.Equals(x.NpcKey, npcName, StringComparison.OrdinalIgnoreCase));

            if (ov != null && !string.IsNullOrWhiteSpace(ov.Voice))
                return ov.Voice;

            if (Configuration.NpcAssignedVoices.TryGetValue(npcName, out var assigned) && !string.IsNullOrWhiteSpace(assigned))
                return assigned;

            var bucket = Configuration.VoiceBuckets.FirstOrDefault(b =>
                string.Equals(b.Name, Configuration.DefaultBucket, StringComparison.OrdinalIgnoreCase));

            if (bucket == null || bucket.Voices.Count == 0)
                return "";

            var pick = bucket.Voices[_rng.Next(bucket.Voices.Count)];
            Configuration.NpcAssignedVoices[npcName] = pick;
            Configuration.Save();

            return pick;
        }

        private static string NormalizeBaseUrl(string url)
        {
            url = (url ?? "").Trim();
            while (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);
            return url;
        }

        public async Task<List<string>> FetchAllTalkVoicesAsync()
        {
            var baseUrl = NormalizeBaseUrl(Configuration.AllTalkBaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return new List<string>();

            try
            {
                var json = await _httpClient.GetStringAsync($"{baseUrl}/api/voices");
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("voices", out var voicesEl) && voicesEl.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var v in voicesEl.EnumerateArray())
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            list.Add(s);
                    }

                    // De-dupe voices list case-insensitively
                    return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                }

                return new List<string>();
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[NpcVoiceMaster] Failed to fetch voices from AllTalk.");
                return new List<string>();
            }
        }

        private async Task<byte[]?> GenerateTtsAndDownloadAsync(string text, string voice)
        {
            var baseUrl = NormalizeBaseUrl(Configuration.AllTalkBaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            var form = new Dictionary<string, string>
            {
                ["text_input"] = text,
                ["text_filtering"] = "standard",
                ["character_voice_gen"] = voice,
                ["narrator_enabled"] = "false",
                ["text_not_inside"] = "character",
                ["language"] = "auto",
                ["output_file_name"] = "npcvoicemaster",
                ["output_file_timestamp"] = "true",
                ["autoplay"] = "false"
            };

            using var resp = await _httpClient.PostAsync(
                $"{baseUrl}/api/tts-generate",
                new FormUrlEncodedContent(form));

            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var status = doc.RootElement.TryGetProperty("status", out var sEl) ? sEl.GetString() : null;
            if (!string.Equals(status, "generate-success", StringComparison.OrdinalIgnoreCase))
                return null;

            string? relUrl = null;
            if (doc.RootElement.TryGetProperty("output_cache_url", out var cacheEl))
                relUrl = cacheEl.GetString();

            if (string.IsNullOrWhiteSpace(relUrl) && doc.RootElement.TryGetProperty("output_file_url", out var fileEl))
                relUrl = fileEl.GetString();

            if (string.IsNullOrWhiteSpace(relUrl))
                return null;

            return await _httpClient.GetByteArrayAsync($"{baseUrl}{relUrl}");
        }

        // =========================
        //  Auto-bucket by filename
        // =========================

        public int AutoBucketVoicesFromNames(List<string> voiceNames, bool clearBucketsFirst)
        {
            if (voiceNames == null || voiceNames.Count == 0)
                return 0;

            if (Configuration.VoiceBuckets == null || Configuration.VoiceBuckets.Count == 0)
                return 0;

            // Optional: clear all existing bucket voice lists first
            if (clearBucketsFirst)
            {
                foreach (var b in Configuration.VoiceBuckets)
                    b.Voices.Clear();
            }

            // Ensure bucket voice lists are de-duped before we add more
            foreach (var b in Configuration.VoiceBuckets)
            {
                b.Voices = b.Voices
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            int added = 0;

            foreach (var raw in voiceNames.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var bucket = InferBucketFromVoiceName(raw);
                if (string.IsNullOrWhiteSpace(bucket))
                    continue;

                var b = Configuration.VoiceBuckets.FirstOrDefault(x =>
                    string.Equals(x.Name, bucket, StringComparison.OrdinalIgnoreCase));

                if (b == null)
                    continue;

                if (!b.Voices.Contains(raw, StringComparer.OrdinalIgnoreCase))
                {
                    b.Voices.Add(raw);
                    added++;
                }
            }

            Configuration.Save();
            return added;
        }

        private string InferBucketFromVoiceName(string voiceName)
        {
            var n = Path.GetFileNameWithoutExtension(voiceName) ?? voiceName;
            n = n.ToLowerInvariant();

            // Tokenize the name
            var tokens = TokenSplit.Split(n)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // We use WOMAN now.
            // Also accept legacy "female" so you don't have to rename everything immediately.
            if (tokens.Contains("woman") || tokens.Contains("female"))
                return "woman";

            if (tokens.Contains("male"))
                return "male";

            if (tokens.Contains("boy"))
                return "boy";

            if (tokens.Contains("girl"))
                return "girl";

            if (tokens.Contains("loporrit") || tokens.Contains("lopo"))
                return "loporrit";

            if (tokens.Contains("machine") || tokens.Contains("robot") || tokens.Contains("mech"))
                return "machine";

            if (tokens.Contains("monsters") || tokens.Contains("monster") || tokens.Contains("beast"))
                return "monsters";

            return "";
        }
    }
}
