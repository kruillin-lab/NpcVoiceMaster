using System;

namespace NPCVoiceMaster // Make sure this matches your project
{
    [Serializable]
    public class NpcExactVoiceOverride
    {
        public string NpcKey { get; set; } = string.Empty;

        // Back-compat alias: older code/UI may refer to this as NpcName.
        public string NpcName
        {
            get => NpcKey;
            set => NpcKey = value;
        }
        public string Voice { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
    }
}
