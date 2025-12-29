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

        // IMPORTANT:
        // AllTalk accepts ONLY these: auto, ar, zh-cn, zh, cs, nl, en, fr, de, hu, hi, it, ja, ko, pl, pt, ru, es, tr
        // We normalize any user/saved values (e.g. "English", "en-US") into a valid code.
        public string AllTalkLanguage { get; set; } = "en";

        // --- NEW: Buckets + mapping ---
        public List<VoiceBucket> VoiceBuckets { get; set; } = new();

        // Keyword-based auto bucket rules (first match wins)
        public List<BucketKeywordRule> BucketKeywordRules { get; set; } = new();

        // Manual override tables
        public List<NpcContainsVoiceRule> NpcContainsVoiceRules { get; set; } = new();
        public List<NpcExactBucketOverride> NpcExactBucketOverrides { get; set; } = new();
        public List<NpcExactVoiceOverride> NpcExactVoiceOverrides { get; set; } = new();
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
            AllTalkLanguage ??= "en";

            // Normalize language every load so old saved values like "English" stop breaking AllTalk.
            AllTalkLanguage = NormalizeAllTalkLanguage(AllTalkLanguage);

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

            // Seed keyword rules if missing (editable in UI)
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

        private static string NormalizeAllTalkLanguage(string? raw)
        {
            var s = (raw ?? "").Trim().ToLowerInvariant();

            // Allowed by AllTalk:
            // auto, ar, zh-cn, zh, cs, nl, en, fr, de, hu, hi, it, ja, ko, pl, pt, ru, es, tr

            if (string.IsNullOrWhiteSpace(s))
                return "en";

            // Common human inputs / locale inputs -> valid codes
            if (s is "english" or "en-us" or "en_us" or "en-gb" or "en_gb" or "en-uk" or "en_uk" or "en-ca" or "en_ca" or "en-au" or "en_au" or "eng")
                return "en";
            if (s is "auto" or "detect")
                return "auto";
            if (s is "japanese" or "ja-jp" or "ja_jp" or "jp")
                return "ja";
            if (s is "korean" or "ko-kr" or "ko_kr" or "kr")
                return "ko";
            if (s is "chinese" or "zh-hans" or "zh_cn" or "zh-cn")
                return "zh-cn";
            if (s is "zh-tw" or "zh_tw" or "zh-hant" or "zh_hant")
                return "zh";
            if (s is "spanish" or "es-es" or "es_es" or "es-mx" or "es_mx")
                return "es";
            if (s is "portuguese" or "pt-br" or "pt_br" or "pt-pt" or "pt_pt")
                return "pt";
            if (s is "french" or "fr-fr" or "fr_fr" or "fr-ca" or "fr_ca")
                return "fr";
            if (s is "german" or "de-de" or "de_de")
                return "de";
            if (s is "russian" or "ru-ru" or "ru_ru")
                return "ru";
            if (s is "italian" or "it-it" or "it_it")
                return "it";
            if (s is "hindi" or "hi-in" or "hi_in")
                return "hi";
            if (s is "arabic" or "ar-sa" or "ar_sa")
                return "ar";
            if (s is "turkish" or "tr-tr" or "tr_tr")
                return "tr";
            if (s is "dutch" or "nl-nl" or "nl_nl")
                return "nl";
            if (s is "czech" or "cs-cz" or "cs_cz")
                return "cs";
            if (s is "hungarian" or "hu-hu" or "hu_hu")
                return "hu";
            if (s is "polish" or "pl-pl" or "pl_pl")
                return "pl";

            // If they already typed a valid code, pass it through
            return s switch
            {
                "auto" or "ar" or "zh-cn" or "zh" or "cs" or "nl" or "en" or "fr" or "de" or "hu" or "hi" or "it" or "ja" or "ko" or "pl" or "pt" or "ru" or "es" or "tr"
                    => s,
                _ => "en"
            };
        }
    }

    [Serializable]
    public class VoiceBucket
    {
        public string Name { get; set; } = "";
        public List<string> Voices { get; set; } = new();
    }

    [Serializable]
    public class BucketKeywordRule
    {
        public string Keyword { get; set; } = "";
        public string BucketName { get; set; } = "";
    }

    [Serializable]
    public class NpcContainsVoiceRule
    {
        public string Match { get; set; } = "";
        public string Voice { get; set; } = "";
    }

    [Serializable]
    public class NpcExactBucketOverride
    {
        public string NpcKey { get; set; } = "";
        public string BucketName { get; set; } = "";
    }

    [Serializable]
    public class NpcExactVoiceOverride
    {
        public string NpcKey { get; set; } = "";
        public string Voice { get; set; } = "";
    }

    [Serializable]
    public class NpcAssignedVoice
    {
        public string NpcKey { get; set; } = "";
        public string BucketName { get; set; } = "";
        public string Voice { get; set; } = "";
    }
}
