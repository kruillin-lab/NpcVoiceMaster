using Dalamud.Game.Command;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
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

    private readonly WindowSystem windowSystem = new("NpcVoiceMaster");
    private readonly VoicePlayer voicePlayer = new();
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public Configuration Configuration { get; }

    private string lastSpeaker = "";
    private string lastText = "";
    private long lastTicks;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        CommandManager.AddHandler("/voicetest",
            new CommandInfo((_, args) => RunVoiceTest(args))
            { HelpMessage = "Test TTS" });

        ChatGui.ChatMessage += OnChatMessage;
    }

    public void Dispose()
    {
        ChatGui.ChatMessage -= OnChatMessage;
        CommandManager.RemoveHandler("/voicetest");
        http.Dispose();
        voicePlayer.Stop();
    }

    // ============================================================
    // CHAT → NPC VOICE
    // ============================================================

    private void OnChatMessage(
        XivChatType type,
        int timestamp,
        ref SeString sender,
        ref SeString message,
        ref bool isHandled)
    {
        if (type != XivChatType.NPCDialogue &&
            type != XivChatType.NPCDialogueAnnouncements)
            return;

        var speaker = sender.TextValue.Trim();
        var text = message.TextValue.Trim();

        if (string.IsNullOrWhiteSpace(text))
            return;

        var now = Environment.TickCount64;
        if (speaker == lastSpeaker && text == lastText && now - lastTicks < 3000)
            return;

        lastSpeaker = speaker;
        lastText = text;
        lastTicks = now;

        _ = SpeakAndPlayAsync(speaker, text);
    }

    // ============================================================
    // COMMANDS
    // ============================================================

    private void RunVoiceTest(string args)
    {
        ChatGui.Print("[NpcVoiceMaster] /voicetest running…");
        _ = SpeakAndPlayAsync("TestSpeaker", args);
    }

    public void RunVoiceTestFromUI()
    {
        RunVoiceTest("Hello from UI");
    }

    // ============================================================
    // CORE SPEECH PIPELINE (MINIMAL + WORKING)
    // ============================================================

    private async Task SpeakAndPlayAsync(string speaker, string text)
    {
        var wav = await SpeakNpcLineAsync(speaker, text);
        if (wav == null || wav.Length < 256)
        {
            ChatGui.Print("[NpcVoiceMaster] No audio returned.");
            return;
        }

        voicePlayer.PlayAudio(wav);
    }

    public async Task<byte[]?> SpeakNpcLineAsync(string npcKey, string text, string forcedBucket = "")
    {
        var baseUrl = (Configuration.AllTalkBaseUrl ?? "").Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            ChatGui.Print("[NpcVoiceMaster] AllTalkBaseUrl is empty.");
            return null;
        }

        var voice = Configuration.AllTalkVoice;
        if (string.IsNullOrWhiteSpace(voice))
            voice = "Mia.wav";

        return await GenerateViaAllTalk(baseUrl, voice, text);
    }

    private async Task<byte[]?> GenerateViaAllTalk(string baseUrl, string voiceFile, string text)
    {
        using var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string,string>("voice", voiceFile),
            new KeyValuePair<string,string>("text", text),
        });

        using var resp = await http.PostAsync($"{baseUrl}/api/previewvoice/", form);
        if (!resp.IsSuccessStatusCode)
            return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("output_file_url", out var p))
            return null;

        var path = p.GetString();
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var audioUrl = path.StartsWith("http") ? path : $"{baseUrl}{path}";
        return await http.GetByteArrayAsync(audioUrl);
    }

    // ============================================================
    // PLACEHOLDER METHODS (REQUIRED BY OTHER FILES)
    // ============================================================

    public (string voice, string bucket, string reason) ResolveVoiceForNpc(string npc, string forcedBucket)
        => (Configuration.AllTalkVoice ?? "Mia.wav", "", "DEFAULT");

    public async Task<List<string>> FetchAllTalkVoicesAsync()
        => new() { "Mia.wav" };
}
