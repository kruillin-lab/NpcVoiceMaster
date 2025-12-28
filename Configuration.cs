// File: Configuration.cs
using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.IO;

namespace NpcVoiceMaster
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 3;

        // ✅ AllTalk settings
        public string AllTalkBaseUrl { get; set; } = "http://10.0.0.80:7851";
        public string AllTalkTtsPathOverride { get; set; } = "";     // e.g. "/api/tts" if needed
        public string AllTalkVoiceName { get; set; } = "Aerith_original.wav";
        public string AllTalkLanguage { get; set; } = "English";

        // Cache
        public bool EnableCache { get; set; } = true;

        [NonSerialized] private IDalamudPluginInterface? _pluginInterface;
        [NonSerialized] private string _cacheFolder = string.Empty;

        public string CacheFolder => _cacheFolder;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;

            _cacheFolder = Path.Combine(pluginInterface.ConfigDirectory.FullName, "VoiceCache");
            try { Directory.CreateDirectory(_cacheFolder); } catch { _cacheFolder = string.Empty; }

            AllTalkBaseUrl ??= "http://10.0.0.80:7851";
            AllTalkTtsPathOverride ??= "";
            AllTalkVoiceName ??= "Aerith_original.wav";
            AllTalkLanguage ??= "English";
        }

        public void Save()
        {
            _pluginInterface?.SavePluginConfig(this);
        }
    }
}
