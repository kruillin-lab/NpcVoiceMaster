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
        // Bump when we change on-disk structure.
        public int Version { get; set; } = 4;

        public bool Enabled { get; set; } = true;

        // Debug overlay toggle
        public bool DebugOverlayEnabled { get; set; } = false;

        // AllTalk
        public string AllTalkBaseUrl { get; set; } = "http://10.0.0.80:7851";

        // Optional override for cache root folder. If blank, plugin uses <PluginConfigDir>\cache
        public string CacheFolderOverride { get; set; } = "";

        // ----------------------------
        // Tag-based system (new)
        // ----------------------------

        // Pop a suggested tag editor when we see a new NPC name (Talk).
        public bool EnableNewNpcPopup { get; set; } = true;

        // NPC names we will never prompt for again.
        public List<string> IgnoredNpcPopup { get; set; } = new();

        // If an NPC has no tags at all, we can inject a harmless default tag (helps scoring stay consistent).
        public string DefaultNpcTag { get; set; } = "default";

        // UI vocab lists (user-curated over time)
        public List<string> KnownNpcTags { get; set; } = new();
        public List<string> KnownTones { get; set; } = new();
        public List<string> KnownAccents { get; set; } = new();

        // Profiles
        public Dictionary<string, NpcProfile> NpcProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, VoiceProfile> VoiceProfiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // ----------------------------
        // Legacy (kept for backward compatibility + migration)
        // ----------------------------

        // Default bucket for random assignment (legacy)
        public string DefaultBucket { get; set; } = "male";

        // Sticky NPC -> chosen voice (random assignment gets stored here)
        public Dictionary<string, string> NpcAssignedVoices { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        // Bucket definitions (legacy)
        public List<VoiceBucket> VoiceBuckets { get; set; } = new();

        // Exact per-NPC voice overrides (beats everything)
        public List<NpcExactVoiceOverride> NpcExactVoiceOverrides { get; set; } = new();

        // NPC -> bucket override (legacy; migrated into RequiredVoiceTags)
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

            // Tag system defaults
            IgnoredNpcPopup ??= new List<string>();
            KnownNpcTags ??= new List<string>();
            KnownTones ??= new List<string>();
            KnownAccents ??= new List<string>();
            if (string.IsNullOrWhiteSpace(DefaultNpcTag))
                DefaultNpcTag = "default";

            NpcProfiles ??= new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase);
            VoiceProfiles ??= new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);

            // Ensure case-insensitive dictionaries even after JSON roundtrip
            NpcAssignedVoices = new Dictionary<string, string>(NpcAssignedVoices, StringComparer.OrdinalIgnoreCase);
            NpcBucketOverrides = new Dictionary<string, string>(NpcBucketOverrides, StringComparer.OrdinalIgnoreCase);
            NpcProfiles = new Dictionary<string, NpcProfile>(NpcProfiles, StringComparer.OrdinalIgnoreCase);
            VoiceProfiles = new Dictionary<string, VoiceProfile>(VoiceProfiles, StringComparer.OrdinalIgnoreCase);

            MigrateAndClean();
        }

        // Static lock to serialize configuration saves. Multiple concurrent Save() calls can
        // corrupt the underlying SQLite transaction in Dalamud's ReliableFileStorage, leading
        // to "cannot rollback - no transaction is active" errors. Serialize all
        // plugin config writes to avoid conflicting transactions.
        private static readonly object _saveLock = new object();

        public void Save()
        {
            if (_pi == null)
                return;
            lock (_saveLock)
            {
                _pi.SavePluginConfig(this);
            }
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
                    merged[b.Name] = new VoiceBucket
                    {
                        Name = b.Name,
                        Voices = new List<string>(b.Voices)
                    };
                }
                else
                {
                    existing.Voices.AddRange(b.Voices);
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

            // ----------------------------
            // NEW: migrate legacy buckets/overrides into tag profiles (once)
            // ----------------------------
            MigrateLegacyBucketsToTagProfiles();

            // Clean profile dictionaries (trim keys + normalize tag lists)
            CleanProfiles();

            // Clean exact overrides
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

            // Normalize vocab lists
            KnownNpcTags = TagUtil.NormDistinct(KnownNpcTags);
            KnownAccents = TagUtil.NormDistinct(KnownAccents);
            KnownTones = TagUtil.NormDistinct(KnownTones);

            // Normalize ignored list
            IgnoredNpcPopup = IgnoredNpcPopup
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Save();
        }

        private void MigrateLegacyBucketsToTagProfiles()
        {
            // Only do a "big" migration once.
            if (Version >= 4 && (VoiceProfiles?.Count ?? 0) > 0)
                return;

            // Make sure containers exist
            NpcProfiles ??= new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase);
            VoiceProfiles ??= new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            KnownNpcTags ??= new List<string>();

            // Buckets -> voice tags
            if (VoiceBuckets != null)
            {
                foreach (var b in VoiceBuckets)
                {
                    var bucketTag = TagUtil.Norm(b?.Name);
                    if (string.IsNullOrWhiteSpace(bucketTag))
                        continue;

                    TagUtil.AddTag(KnownNpcTags, bucketTag);

                    if (b?.Voices == null) continue;
                    foreach (var v in b.Voices)
                    {
                        var voice = (v ?? "").Trim();
                        if (string.IsNullOrWhiteSpace(voice))
                            continue;

                        if (!VoiceProfiles.TryGetValue(voice, out var vp) || vp == null)
                            vp = new VoiceProfile();

                        vp.Tags ??= new List<string>();
                        TagUtil.AddTag(vp.Tags, bucketTag);
                        vp.Enabled = true;

                        VoiceProfiles[voice] = vp;
                    }
                }
            }

            // Bucket overrides -> NPC required voice-tags
            if (NpcBucketOverrides != null && NpcBucketOverrides.Count > 0)
            {
                foreach (var kv in NpcBucketOverrides)
                {
                    var npc = (kv.Key ?? "").Trim();
                    var bucketTag = TagUtil.Norm(kv.Value);
                    if (string.IsNullOrWhiteSpace(npc) || string.IsNullOrWhiteSpace(bucketTag))
                        continue;

                    if (!NpcProfiles.TryGetValue(npc, out var np) || np == null)
                        np = new NpcProfile();

                    np.RequiredVoiceTags ??= new List<string>();
                    TagUtil.AddTag(np.RequiredVoiceTags, bucketTag);

                    NpcProfiles[npc] = np;
                }

                // Once migrated, legacy bucket overrides are dead weight.
                NpcBucketOverrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            // Make sure we have some sensible baseline vocab
            foreach (var t in new[] { "default", "male", "woman", "boy", "girl", "loporrit", "machine", "monsters", "big monster", "little monster" })
                TagUtil.AddTag(KnownNpcTags, t);

            Version = 4;
        }

        private void CleanProfiles()
        {
            // NPC profiles
            var npcClean = new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in NpcProfiles)
            {
                var key = (kv.Key ?? "").Trim();
                if (string.IsNullOrWhiteSpace(key) || kv.Value == null)
                    continue;

                var np = kv.Value;
                np.RequiredVoiceTags ??= new List<string>();
                np.PreferredVoiceTags ??= new List<string>();
                np.NpcTags ??= new List<string>();

                np.RequiredVoiceTags = TagUtil.NormDistinct(np.RequiredVoiceTags);
                np.PreferredVoiceTags = TagUtil.NormDistinct(np.PreferredVoiceTags);
                np.NpcTags = TagUtil.NormDistinct(np.NpcTags);

                npcClean[key] = np;
            }
            NpcProfiles = npcClean;

            // Voice profiles
            var voiceClean = new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in VoiceProfiles)
            {
                var key = (kv.Key ?? "").Trim();
                if (string.IsNullOrWhiteSpace(key) || kv.Value == null)
                    continue;

                var vp = kv.Value;
                vp.Tags ??= new List<string>();
                vp.Tags = TagUtil.NormDistinct(vp.Tags);

                // Trim whitespace in tone/accent
                vp.Tone = string.IsNullOrWhiteSpace(vp.Tone) ? null : vp.Tone!.Trim();
                vp.Accent = string.IsNullOrWhiteSpace(vp.Accent) ? null : vp.Accent!.Trim();

                voiceClean[key] = vp;
            }
            VoiceProfiles = voiceClean;
        }
    }
}
