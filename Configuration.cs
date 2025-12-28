// File: Configuration.cs
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
        public int Version { get; set; } = 7;

        // AllTalk
        public string AllTalkBaseUrl { get; set; } = "http://10.0.0.80:7851";
        public string AllTalkVoice { get; set; } = "Mia.wav"; // fallback if nothing else resolves

        // Cache
        public bool EnableCache { get; set; } = true;
        public string CacheFolderOverride { get; set; } = "";

        // Legacy (kept so you don't lose settings)
        public string AllTalkTtsPathOverride { get; set; } = "";
        public string AllTalkVoiceName { get; set; } = "Aerith_original.wav";
        public string AllTalkLanguage { get; set; } = "English";

        // --- NEW: Buckets + mapping ---
        public List<VoiceBucket> VoiceBuckets { get; set; } = new();

        // Keyword-based auto bucket rules:
        // If npcKey contains Keyword (case-insensitive), bucket = BucketName.
        // First match wins.
        public List<BucketKeywordRule> BucketKeywordRules { get; set; } = new();

        // Manual override tables
        // 1) Contains-style manual voice override (first match wins)
        public List<NpcContainsVoiceRule> NpcContainsVoiceRules { get; set; } = new();

        // 2) Exact NPC -> forced bucket
        public List<NpcExactBucketOverride> NpcExactBucketOverrides { get; set; } = new();

        // 3) Exact NPC -> forced voice
        public List<NpcExactVoiceOverride> NpcExactVoiceOverrides { get; set; } = new();

        // 4) Persisted auto assignments: Exact NPC -> assigned voice (picked once, reused)
        public List<NpcAssignedVoice> NpcAssignedVoices { get; set; } = new();

        [NonSerialized] private IDalamudPluginInterface? _pluginInterface;
        [NonSerialized] private string _defaultCacheFolder = string.Empty;

        public void Initialize(IDalamudPluginInterface pluginInterface)
        {
            _pluginInterface = pluginInterface;

            _defaultCacheFolder = Path.Combine(pluginInterface.ConfigDirectory.FullName, "VoiceCache");
            try { Directory.CreateDirectory(_defaultCacheFolder); } catch { _defaultCacheFolder = string.Empty; }

            AllTalkBaseUrl ??= "http://10.0.0.80:7851";
            AllTalkVoice ??= "Mia.wav";
            CacheFolderOverride ??= "";
            AllTalkTtsPathOverride ??= "";
            AllTalkVoiceName ??= "Aerith_original.wav";
            AllTalkLanguage ??= "English";

            VoiceBuckets ??= new List<VoiceBucket>();
            BucketKeywordRules ??= new List<BucketKeywordRule>();
            NpcContainsVoiceRules ??= new List<NpcContainsVoiceRule>();
            NpcExactBucketOverrides ??= new List<NpcExactBucketOverride>();
            NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();
            NpcAssignedVoices ??= new List<NpcAssignedVoice>();

            // Seed buckets if missing
            if (VoiceBuckets.Count == 0)
            {
                VoiceBuckets.Add(new VoiceBucket { Name = "male" });
                VoiceBuckets.Add(new VoiceBucket { Name = "female" });
                VoiceBuckets.Add(new VoiceBucket { Name = "boy" });
                VoiceBuckets.Add(new VoiceBucket { Name = "girl" });
                VoiceBuckets.Add(new VoiceBucket { Name = "loporrit" });
                VoiceBuckets.Add(new VoiceBucket { Name = "machine" });
                VoiceBuckets.Add(new VoiceBucket { Name = "monster" });
            }

            // Seed keyword rules if missing (you can edit in UI)
            if (BucketKeywordRules.Count == 0)
            {
                BucketKeywordRules.Add(new BucketKeywordRule { Keyword = "loporrit", BucketName = "loporrit" });
                BucketKeywordRules.Add(new BucketKeywordRule { Keyword = "way", BucketName = "loporrit" }); // optional vibe rule
                BucketKeywordRules.Add(new BucketKeywordRule { Keyword = "machine", BucketName = "machine" });
                BucketKeywordRules.Add(new BucketKeywordRule { Keyword = "robot", BucketName = "machine" });
                BucketKeywordRules.Add(new BucketKeywordRule { Keyword = "drone", BucketName = "machine" });
                BucketKeywordRules.Add(new BucketKeywordRule { Keyword = "beast", BucketName = "monster" });
                BucketKeywordRules.Add(new BucketKeywordRule { Keyword = "monster", BucketName = "monster" });
            }

            // Seed a couple manual contains rules if empty (examples)
            if (NpcContainsVoiceRules.Count == 0)
            {
                NpcContainsVoiceRules.Add(new NpcContainsVoiceRule { Match = "Cid", Voice = "Cid.wav" });
                NpcContainsVoiceRules.Add(new NpcContainsVoiceRule { Match = "Estinien", Voice = "Estinien.wav" });
            }

            // Ensure bucket voice lists aren't null
            foreach (var b in VoiceBuckets)
            {
                if (b == null) continue;
                b.Name ??= "";
                b.Voices ??= new List<string>();
            }
        }

        public string GetEffectiveCacheFolder()
        {
            var ov = (CacheFolderOverride ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(ov))
                return ov;

            return _defaultCacheFolder ?? "";
        }

        public void Save()
        {
            _pluginInterface?.SavePluginConfig(this);
        }
    }

    [Serializable]
    public class VoiceBucket
    {
        public string Name { get; set; } = "";               // e.g. "male"
        public List<string> Voices { get; set; } = new();    // list of filenames from /api/voices
    }

    [Serializable]
    public class BucketKeywordRule
    {
        public string Keyword { get; set; } = "";        // substring match in npcKey (case-insensitive)
        public string BucketName { get; set; } = "";     // bucket to use
    }

    [Serializable]
    public class NpcContainsVoiceRule
    {
        public string Match { get; set; } = "";  // substring match in npcKey
        public string Voice { get; set; } = "";  // forced voice filename
    }

    [Serializable]
    public class NpcExactBucketOverride
    {
        public string NpcKey { get; set; } = "";      // exact npc key/name
        public string BucketName { get; set; } = "";  // forced bucket name
    }

    [Serializable]
    public class NpcExactVoiceOverride
    {
        public string NpcKey { get; set; } = "";  // exact npc key/name
        public string Voice { get; set; } = "";   // forced voice filename
    }

    [Serializable]
    public class NpcAssignedVoice
    {
        public string NpcKey { get; set; } = "";     // exact npc key/name
        public string BucketName { get; set; } = ""; // bucket used when assigned
        public string Voice { get; set; } = "";      // assigned voice filename
    }
}
