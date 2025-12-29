using System;
using System.Collections.Generic;

namespace NPCVoiceMaster
{
    [Serializable]
    public class VoiceBucket
    {
        public string Name { get; set; } = "Default";
        public List<string> Voices { get; set; } = new();
    }
}