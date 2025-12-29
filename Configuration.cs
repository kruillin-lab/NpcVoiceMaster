using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NPCVoiceMaster
{
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 0;

        public bool Enabled { get; set; } = true;

        public string AllTalkBaseUrl { get; set; } = "http://10.0.0.80:7851";

        public string DefaultBucket { get; set; } = "male";

        // If true, logs lots of chat messages with their numeric type
        public bool DebugLogCandidateChat { get; set; } = false;

        public Dictionary<string, string> NpcAssignedVoices { get; set; } = new();

        public List<VoiceBucket> VoiceBuckets { get; set; } = new();

        public List<NpcExactVoiceOverride> NpcExactVoiceOverrides { get; set; } = new();

        [NonSerialized]
        private IDalamudPluginInterface? _pluginInterface;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;

            // Always clean/migrate buckets on load so old configs don't create duplicates forever.
            MigrateAndCleanBuckets();
        }

        public void Save()
        {
            _pluginInterface!.SavePluginConfig(this);
        }

        private void MigrateAndCleanBuckets()
        {
            // Expected buckets (canonical names, in the order we want to show them)
            var canonicalOrder = new List<string>
            {
                "male",
                "woman",
                "boy",
                "girl",
                "loporrit",
                "machine",
                "monsters",
            };

            // If config has never been created or got nuked, start sane.
            if (VoiceBuckets == null)
                VoiceBuckets = new List<VoiceBucket>();

            // 1) Normalize names and migrate female -> woman
            foreach (var b in VoiceBuckets)
            {
                if (b == null) continue;

                b.Name = (b.Name ?? "").Trim();
                if (b.Name.Equals("female", StringComparison.OrdinalIgnoreCase))
                    b.Name = "woman";

                if (b.Voices == null)
                    b.Voices = new List<string>();
            }

            // 2) Merge duplicates by name (case-insensitive)
            var merged = new Dictionary<string, VoiceBucket>(StringComparer.OrdinalIgnoreCase);

            foreach (var b in VoiceBuckets.Where(x => x != null))
            {
                var name = (b.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!merged.TryGetValue(name, out var existing))
                {
                    merged[name] = new VoiceBucket
                    {
                        Name = name,
                        Voices = new List<string>()
                    };
                    existing = merged[name];
                }

                // Merge voices (case-insensitive de-dupe)
                foreach (var v in b.Voices.Where(v => !string.IsNullOrWhiteSpace(v)))
                {
                    if (!existing.Voices.Contains(v, StringComparer.OrdinalIgnoreCase))
                        existing.Voices.Add(v);
                }
            }

            // 3) Ensure all canonical buckets exist
            foreach (var name in canonicalOrder)
            {
                if (!merged.ContainsKey(name))
                {
                    merged[name] = new VoiceBucket
                    {
                        Name = name,
                        Voices = new List<string>()
                    };
                }
            }

            // 4) Rebuild the list in canonical order, then append any extra custom buckets at the end
            var rebuilt = new List<VoiceBucket>();

            foreach (var name in canonicalOrder)
            {
                var b = merged[name];
                b.Name = name; // enforce canonical casing
                b.Voices = b.Voices
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                rebuilt.Add(b);
            }

            // Extra buckets (user-added custom categories) keep them, but not duplicated
            var extras = merged.Keys
                .Where(k => !canonicalOrder.Contains(k, StringComparer.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

            foreach (var extraName in extras)
            {
                var b = merged[extraName];
                b.Name = extraName.Trim();
                b.Voices = b.Voices
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                rebuilt.Add(b);
            }

            VoiceBuckets = rebuilt;

            // 5) If DefaultBucket points to something that no longer exists, set it to male
            if (string.IsNullOrWhiteSpace(DefaultBucket) ||
                !VoiceBuckets.Any(b => b.Name.Equals(DefaultBucket, StringComparison.OrdinalIgnoreCase)))
            {
                DefaultBucket = "male";
            }

            // 6) Save after migration so duplicates don’t come back next reload
            Save();
        }
    }
}
