using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.IO;

namespace NpcVoiceMaster
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        public string ApiKey { get; set; } = string.Empty;
        public bool EnableCache { get; set; } = true;

        // Voice pools
        public List<string> MaleVoicePool { get; set; } = new();
        public List<string> FemaleVoicePool { get; set; } = new();
        public List<string> MaleChildVoicePool { get; set; } = new();
        public List<string> FemaleChildVoicePool { get; set; } = new();
        public List<string> MachineVoicePool { get; set; } = new();
        public List<string> MonsterVoicePool { get; set; } = new();
        public List<string> LoporritVoicePool { get; set; } = new();

        // Maps for custom assignments
        // NpcVoiceMap: direct NPC -> voiceId (strongest override)
        public Dictionary<string, string> NpcVoiceMap { get; set; } = new();

        // NpcCategoryMap: NPC -> category string ("Male", "Female", "Machine", etc.)
        public Dictionary<string, string> NpcCategoryMap { get; set; } = new();

        [NonSerialized] private IDalamudPluginInterface? _pluginInterface;
        [NonSerialized] private string _cacheFolder = string.Empty;

        // Non-serialized but safe for UI/runtime
        public string CacheFolder => _cacheFolder;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;

            _cacheFolder = Path.Combine(pluginInterface.ConfigDirectory.FullName, "VoiceCache");
            try
            {
                Directory.CreateDirectory(_cacheFolder);
            }
            catch
            {
                // If we cannot create a folder, leave it empty; UI should handle this.
                _cacheFolder = string.Empty;
            }

            // Safety: handle nulls if config was created in older versions
            MaleVoicePool ??= new();
            FemaleVoicePool ??= new();
            MaleChildVoicePool ??= new();
            FemaleChildVoicePool ??= new();
            MachineVoicePool ??= new();
            MonsterVoicePool ??= new();
            LoporritVoicePool ??= new();

            NpcVoiceMap ??= new();
            NpcCategoryMap ??= new();
        }

        public void Save()
        {
            _pluginInterface?.SavePluginConfig(this);
        }
    }
}
