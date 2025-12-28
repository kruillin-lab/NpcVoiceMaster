// File: ConfigWindow.cs
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;
using System.IO;
using System.Numerics;

namespace NpcVoiceMaster
{
    public class ConfigWindow : Window
    {
        private readonly Plugin _plugin;

        private string _status = "";
        private DateTime _statusTime = DateTime.MinValue;

        public ConfigWindow(Plugin plugin) : base("NPC Voice Master Settings")
        {
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 420) };
            _plugin = plugin;
        }

        public override void Draw()
        {
            if (!string.IsNullOrWhiteSpace(_status) && (DateTime.UtcNow - _statusTime).TotalSeconds < 8)
            {
                ImGui.TextWrapped(_status);
                ImGui.Separator();
            }

            ImGui.TextWrapped("AllTalk-only build. This plugin does NOT use ElevenLabs.");
            ImGui.Separator();

            var url = _plugin.Configuration.AllTalkBaseUrl ?? "";
            if (ImGui.InputText("AllTalk Base URL", ref url, 200))
            {
                _plugin.Configuration.AllTalkBaseUrl = url;
                _plugin.Configuration.Save();
            }

            var path = _plugin.Configuration.AllTalkTtsPathOverride ?? "";
            if (ImGui.InputText("TTS Path Override (optional)", ref path, 200))
            {
                _plugin.Configuration.AllTalkTtsPathOverride = path;
                _plugin.Configuration.Save();
            }

            var voice = _plugin.Configuration.AllTalkVoiceName ?? "";
            if (ImGui.InputText("Voice (name/id)", ref voice, 200))
            {
                _plugin.Configuration.AllTalkVoiceName = voice;
                _plugin.Configuration.Save();
            }

            var lang = _plugin.Configuration.AllTalkLanguage ?? "";
            if (ImGui.InputText("Language", ref lang, 200))
            {
                _plugin.Configuration.AllTalkLanguage = lang;
                _plugin.Configuration.Save();
            }

            ImGui.Spacing();

            if (ImGui.Button("Test Audio (/voicetest)"))
            {
                _plugin.RunVoiceTestFromUI();
                SetStatus("Sent /voicetest (watch chat + listen).");
            }

            ImGui.SameLine();

            if (ImGui.Button("Print Fingerprint (/voicefinger)"))
            {
                SetStatus("Type /voicefinger in chat to confirm the loaded DLL.");
            }

            ImGui.Separator();

            var enableCache = _plugin.Configuration.EnableCache;
            if (ImGui.Checkbox("Enable Local Audio Cache", ref enableCache))
            {
                _plugin.Configuration.EnableCache = enableCache;
                _plugin.Configuration.Save();
            }

            var cacheFolder = _plugin.Configuration.CacheFolder ?? "";
            ImGui.TextWrapped($"Cache folder: {cacheFolder}");

            if (!string.IsNullOrWhiteSpace(cacheFolder))
            {
                if (ImGui.Button("Clear Cached Audio Files"))
                {
                    try
                    {
                        Directory.CreateDirectory(cacheFolder);
                        foreach (var file in Directory.GetFiles(cacheFolder))
                            File.Delete(file);

                        SetStatus("Cache cleared.");
                    }
                    catch (Exception ex)
                    {
                        SetStatus($"Cache clear failed: {ex.Message}");
                    }
                }
            }

            ImGui.Spacing();
            ImGui.TextWrapped("If /voicetest says 'returned no audio', open AllTalk in your browser, inspect the Network tab, and copy the POST path into 'TTS Path Override'.");
        }

        private void SetStatus(string msg)
        {
            _status = msg;
            _statusTime = DateTime.UtcNow;
        }
    }
}
