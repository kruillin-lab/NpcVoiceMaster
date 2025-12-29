using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Game.Text.SeStringHandling;


namespace NPCVoiceMaster
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "NPC Voice Master";
        private const string CommandName = "/npcvoice";

        private IDalamudPluginInterface PluginInterface { get; init; }
        private Dalamud.Plugin.Services.ICommandManager CommandManager { get; init; }

        public Configuration Configuration { get; init; }
        public WindowSystem WindowSystem { get; init; }
        private ConfigWindow ConfigWindow { get; init; }

        private readonly HttpClient _httpClient = new HttpClient();
        private readonly VoicePlayer _player = new VoicePlayer();
        private readonly Random _rng = new Random();

        public Plugin(IDalamudPluginInterface pluginInterface, Dalamud.Plugin.Services.ICommandManager commandManager)
        {
            PluginInterface = pluginInterface;
            CommandManager = commandManager;

            // IMPORTANT: enables [PluginService] injection in Svc.cs
            pluginInterface.Create<Svc>();

            // Load Config
            Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            Configuration.Initialize(PluginInterface);

            // UI Setup
            WindowSystem = new WindowSystem("NPCVoiceMaster");
            ConfigWindow = new ConfigWindow(this);
            WindowSystem.AddWindow(ConfigWindow);

            PluginInterface.UiBuilder.Draw += DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi += DrawConfigUI;

            // Command
            CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the NPC Voice Master config."
            });

            // Wire player logging into Dalamud log
            _player.Log = msg => Svc.Log.Debug(msg);

            // Hook chat
            Svc.Chat.ChatMessage += OnChatMessage;
            Svc.Log.Information("[NPCVoiceMaster] Loaded. Listening for NPC dialogue.");
        }

        public void Dispose()
        {
            Svc.Chat.ChatMessage -= OnChatMessage;

            WindowSystem.RemoveAllWindows();
            ConfigWindow.Dispose();

            CommandManager.RemoveHandler(CommandName);
            PluginInterface.UiBuilder.Draw -= DrawUI;
            PluginInterface.UiBuilder.OpenConfigUi -= DrawConfigUI;

            _player.Dispose();
            _httpClient.Dispose();
        }

        private void OnCommand(string command, string args)
        {
            ConfigWindow.IsOpen = !ConfigWindow.IsOpen;
        }

        private void DrawUI() => WindowSystem.Draw();

        private void DrawConfigUI() => ConfigWindow.IsOpen = true;

        private void OnChatMessage(XivChatType type, int timestamp, ref SeString sender, ref SeString message, ref bool isHandled)
        {
            if (!Configuration.Enabled)
                return;

            // Only speak NPC Dialogue (and the announcement variant)
            if (type != XivChatType.NPCDialogue && type != XivChatType.NPCDialogueAnnouncements)
                return;

            var npcName = sender.TextValue?.Trim();
            var text = message.TextValue?.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return;

            // Sometimes sender is empty; still speak the line, but we’ll treat speaker as "Unknown"
            if (string.IsNullOrWhiteSpace(npcName))
                npcName = "Unknown";

            // Fire and forget (don’t block chat thread)
            _ = Task.Run(async () =>
            {
                try
                {
                    var voice = ResolveVoiceForNpc(npcName);
                    if (string.IsNullOrWhiteSpace(voice))
                    {
                        Svc.Log.Debug($"[NPCVoiceMaster] No voice available for NPC '{npcName}'.");
                        return;
                    }

                    var audioBytes = await GenerateTtsAndDownloadAsync(text, voice);
                    if (audioBytes == null || audioBytes.Length == 0)
                    {
                        Svc.Log.Debug($"[NPCVoiceMaster] TTS returned empty audio for '{npcName}'.");
                        return;
                    }

                    _player.PlayAudio(audioBytes);
                }
                catch (Exception ex)
                {
                    Svc.Log.Error(ex, "[NPCVoiceMaster] Failed to generate/play TTS.");
                }
            });
        }

        private string ResolveVoiceForNpc(string npcName)
        {
            // 1) Exact override
            var ov = Configuration.NpcExactVoiceOverrides.FirstOrDefault(x =>
                x.Enabled &&
                string.Equals(x.NpcKey, npcName, StringComparison.OrdinalIgnoreCase));

            if (ov != null && !string.IsNullOrWhiteSpace(ov.Voice))
                return ov.Voice;

            // 2) Already assigned
            if (Configuration.NpcAssignedVoices.TryGetValue(npcName, out var assigned) && !string.IsNullOrWhiteSpace(assigned))
                return assigned;

            // 3) Random from default bucket
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
            url = url.Trim();
            while (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);
            return url;
        }

        // AllTalk V2: GET /api/voices returns { status: "...", voices: ["..."] }
        public async Task<List<string>> FetchAllTalkVoicesAsync()
        {
            var baseUrl = Configuration.AllTalkBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
                return new List<string>();

            baseUrl = NormalizeBaseUrl(baseUrl);

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
                    return list;
                }

                return new List<string>();
            }
            catch (Exception ex)
            {
                Svc.Log.Warning(ex, "[NPCVoiceMaster] Failed to fetch voices from AllTalk.");
                return new List<string>();
            }
        }

        // AllTalk V2: POST /api/tts-generate (form urlencoded), response contains output_cache_url
        private async Task<byte[]?> GenerateTtsAndDownloadAsync(string text, string voice)
        {
            var baseUrl = Configuration.AllTalkBaseUrl;
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            baseUrl = NormalizeBaseUrl(baseUrl);

            // Create a stable-ish filename hint (AllTalk can still timestamp it)
            var outputName = "npcvoicemaster";

            var form = new Dictionary<string, string>
            {
                ["text_input"] = text,
                ["text_filtering"] = "standard",
                ["character_voice_gen"] = voice,
                ["narrator_enabled"] = "false",
                ["text_not_inside"] = "character",
                ["language"] = "auto",
                ["output_file_name"] = outputName,
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

            // Prefer cache URL if present
            string? relUrl = null;
            if (doc.RootElement.TryGetProperty("output_cache_url", out var cacheEl))
                relUrl = cacheEl.GetString();

            if (string.IsNullOrWhiteSpace(relUrl) && doc.RootElement.TryGetProperty("output_file_url", out var fileEl))
                relUrl = fileEl.GetString();

            if (string.IsNullOrWhiteSpace(relUrl))
                return null;

            // Response does not include host, so we add it ourselves
            var downloadUrl = $"{baseUrl}{relUrl}";
            return await _httpClient.GetByteArrayAsync(downloadUrl);
        }
    }
}
