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

        public bool Enabled { get; set; } = true;

        public string AllTalkBaseUrl { get; set; } = "http://localhost:7851";

        // Which bucket to use when we don't know what an NPC is yet
        public string DefaultBucket { get; set; } = "male";

        // Persisted: NPC name -> voice filename (so random assignment “sticks”)
        public Dictionary<string, string> NpcAssignedVoices { get; set; } = new();

        public List<VoiceBucket> VoiceBuckets { get; set; } = new()
        {
            new VoiceBucket { Name = "male" },
            new VoiceBucket { Name = "female" },
            new VoiceBucket { Name = "boy" },
            new VoiceBucket { Name = "girl" },
            new VoiceBucket { Name = "loporrit" },
            new VoiceBucket { Name = "machine" },
            new VoiceBucket { Name = "monsters" },
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
