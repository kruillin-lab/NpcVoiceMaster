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
        public int Version { get; set; } = 3;

        public bool Enabled { get; set; } = true;

        // Debug overlay toggle
        public bool DebugOverlayEnabled { get; set; } = false;

        // AllTalk
        public string AllTalkBaseUrl { get; set; } = "http://10.0.0.80:7851";

        // Optional override for cache root folder. If blank, plugin uses <PluginConfigDir>\cache
        public string CacheFolderOverride { get; set; } = "";

        // Default bucket for random assignment
        public string DefaultBucket { get; set; } = "male";

        // Sticky NPC -> chosen voice (random assignment gets stored here)
        public Dictionary<string, string> NpcAssignedVoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Bucket definitions
        public List<VoiceBucket> VoiceBuckets { get; set; } = new();

        // Exact per-NPC voice overrides (beats everything)
        public List<NpcExactVoiceOverride> NpcExactVoiceOverrides { get; set; } = new();

        // NPC -> bucket override (used if no exact voice override)
        public Dictionary<string, string> NpcBucketOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        [NonSerialized]
        private IDalamudPluginInterface? _pi;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            _pi = pluginInterface;

            VoiceBuckets ??= new List<VoiceBucket>();
            NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();
            NpcBucketOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            MigrateAndClean();
        }

        public void Save()
        {
            _pi!.SavePluginConfig(this);
        }

        private void MigrateAndClean()
        {
            // Ensure canonical buckets exist and migrate "female" -> "woman"
            var canonical = new List<string> { "male", "woman", "boy", "girl", "loporrit", "machine", "monsters" };

            foreach (var b in VoiceBuckets)
            {
                b.Name = (b.Name ?? "").Trim();
                if (b.Name.Equals("female", StringComparison.OrdinalIgnoreCase))
                    b.Name = "woman";

                b.Voices ??= new List<string>();
                b.Voices = b.Voices
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            var merged = new Dictionary<string, VoiceBucket>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in VoiceBuckets)
            {
                if (string.IsNullOrWhiteSpace(b.Name)) continue;

                if (!merged.TryGetValue(b.Name, out var existing))
                {
                    existing = new VoiceBucket { Name = b.Name, Voices = new List<string>() };
                    merged[b.Name] = existing;
                }

                foreach (var v in b.Voices)
                {
                    if (!existing.Voices.Contains(v, StringComparer.OrdinalIgnoreCase))
                        existing.Voices.Add(v);
                }
            }

            foreach (var name in canonical)
            {
                if (!merged.ContainsKey(name))
                    merged[name] = new VoiceBucket { Name = name, Voices = new List<string>() };
            }

            var rebuilt = new List<VoiceBucket>();
            foreach (var name in canonical)
            {
                var b = merged[name];
                b.Name = name;
                b.Voices = b.Voices
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                rebuilt.Add(b);
            }

            var custom = merged.Keys
                .Where(k => !canonical.Contains(k, StringComparer.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

            foreach (var name in custom)
            {
                var b = merged[name];
                b.Voices = b.Voices
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                rebuilt.Add(b);
            }

            VoiceBuckets = rebuilt;

            if (string.IsNullOrWhiteSpace(DefaultBucket) ||
                !VoiceBuckets.Any(b => b.Name.Equals(DefaultBucket, StringComparison.OrdinalIgnoreCase)))
            {
                DefaultBucket = "male";
            }

            var cleaned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in NpcBucketOverrides)
            {
                var k = (kv.Key ?? "").Trim();
                var v = (kv.Value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v))
                    continue;
                cleaned[k] = v;
            }
            NpcBucketOverrides = cleaned;

            foreach (var o in NpcExactVoiceOverrides)
            {
                o.NpcKey = (o.NpcKey ?? "").Trim();
                o.Voice = (o.Voice ?? "").Trim();
            }

            NpcExactVoiceOverrides = NpcExactVoiceOverrides
                .Where(o => !string.IsNullOrWhiteSpace(o.NpcKey))
                .GroupBy(o => o.NpcKey, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            Save();
        }
    }
}
