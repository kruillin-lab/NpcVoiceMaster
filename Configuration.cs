using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;

namespace NPCVoiceMaster
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;

        public string AllTalkBaseUrl { get; set; } = "http://localhost:7851";

        public List<VoiceBucket> VoiceBuckets { get; set; } = new()
        {
            new VoiceBucket { Name = "male" },
            new VoiceBucket { Name = "female" }
        };

        public List<NpcExactVoiceOverride> NpcExactVoiceOverrides { get; set; } = new();

        [NonSerialized]
        private IDalamudPluginInterface? pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            this.pluginInterface = pluginInterface;
        }

        public void Save()
        {
            this.pluginInterface!.SavePluginConfig(this);
        }
    }
}