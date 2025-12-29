using System;

namespace NPCVoiceMaster // Make sure this matches your project
{
    [Serializable]
    public class NpcExactVoiceOverride
    {
        public string NpcKey { get; set; } = string.Empty;
        public string Voice { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }
}