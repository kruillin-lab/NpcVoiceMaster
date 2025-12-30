using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace NPCVoiceMaster
{
    internal static class AutoTagger
    {
        // Conservative suggestions only (UI should ask before persisting if desired).
        public static List<string> SuggestVoiceTagsFromFilename(string voiceFile)
        {
            var tags = new List<string>();
            var name = Path.GetFileNameWithoutExtension(voiceFile) ?? voiceFile;
            var tokens = Tokenize(name);

            // Core identity buckets as tags
            foreach (var t in tokens)
            {
                switch (t)
                {
                    case "male":
                    case "man":
                        TagUtil.AddTag(tags, "male");
                        break;
                    case "female":
                    case "woman":
                        TagUtil.AddTag(tags, "woman");
                        break;
                    case "boy":
                    case "kid":
                        TagUtil.AddTag(tags, "boy");
                        break;
                    case "girl":
                        TagUtil.AddTag(tags, "girl");
                        break;
                    case "loporrit":
                        TagUtil.AddTag(tags, "loporrit");
                        break;
                    case "machine":
                    case "robot":
                    case "android":
                    case "unit":
                    case "synth":
                        TagUtil.AddTag(tags, "machine");
                        break;
                    case "monster":
                    case "monsters":
                    case "beast":
                    case "creature":
                        TagUtil.AddTag(tags, "monsters");
                        break;
                    case "default":
                        TagUtil.AddTag(tags, "default");
                        break;
                }
            }

            // Accent-ish tokens
            var accentMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["uk"]="british", ["brit"]="british", ["british"]="british", ["eng"]="british", ["english"]="british",
                ["us"]="american", ["usa"]="american", ["american"]="american",
                ["aus"]="australian", ["aussie"]="australian", ["australian"]="australian",
                ["india"]="indian", ["indian"]="indian",
                ["irish"]="irish", ["ireland"]="irish",
                ["scottish"]="scottish", ["scotland"]="scottish",
                ["welsh"]="welsh",
                ["canadian"]="canadian", ["canada"]="canadian",
                ["nz"]="new zealand", ["newzealand"]="new zealand",
                ["sa"]="south african", ["southafrican"]="south african",
            };

            foreach (var t in tokens)
            {
                if (accentMap.TryGetValue(t, out var a))
                    TagUtil.AddTag(tags, a);
            }

            // Tone/vibe-ish tokens (kept as tags; also can be used to seed Tone field later)
            var vibe = new[]
            {
                "calm","stern","angry","sad","sleepy","menacing","warm","cold","soft","rough","posh","gravel","deep","high",
                "cheerful","excited","bored","serious","goofy","whisper","breathy"
            };
            foreach (var t in tokens)
            {
                if (vibe.Contains(t))
                    TagUtil.AddTag(tags, t);
            }

            return TagUtil.NormDistinct(tags);
        }

        public static string? SuggestVoiceAccentFromFilename(string voiceFile)
        {
            var name = Path.GetFileNameWithoutExtension(voiceFile) ?? voiceFile;
            var tokens = Tokenize(name);

            if (tokens.Contains("british") || tokens.Contains("uk") || tokens.Contains("eng") || tokens.Contains("english"))
                return "british";
            if (tokens.Contains("american") || tokens.Contains("us") || tokens.Contains("usa"))
                return "american";
            if (tokens.Contains("australian") || tokens.Contains("aus") || tokens.Contains("aussie"))
                return "australian";
            if (tokens.Contains("indian") || tokens.Contains("india"))
                return "indian";
            if (tokens.Contains("irish") || tokens.Contains("ireland"))
                return "irish";
            if (tokens.Contains("scottish") || tokens.Contains("scotland"))
                return "scottish";
            if (tokens.Contains("welsh"))
                return "welsh";
            if (tokens.Contains("canadian") || tokens.Contains("canada"))
                return "canadian";
            if (tokens.Contains("nz") || tokens.Contains("newzealand"))
                return "new zealand";
            if (tokens.Contains("southafrican") || (tokens.Contains("south") && tokens.Contains("african")))
                return "south african";

            return null;
        }

        public static string? SuggestVoiceToneFromFilename(string voiceFile)
        {
            var name = Path.GetFileNameWithoutExtension(voiceFile) ?? voiceFile;
            var tokens = Tokenize(name);

            // Prioritized tone heuristics
            var ordered = new[]
            {
                "menacing","angry","stern","sad","sleepy","cheerful","calm","warm","cold","soft","rough","posh","gravel","deep","high","goofy"
            };

            foreach (var t in ordered)
                if (tokens.Contains(t))
                    return t;

            return null;
        }

        public static List<string> SuggestNpcVoiceTagsFromName(string npcName)
        {
            var tags = new List<string>();
            var n = (npcName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(n)) return tags;

            var tokens = Tokenize(n);

            // Title-based hints (NPC identity/context -> voice preferences)
            if (tokens.Contains("sir") || tokens.Contains("lord") || tokens.Contains("lady") || tokens.Contains("captain") || tokens.Contains("commander"))
                TagUtil.AddTag(tags, "posh");

            if (tokens.Contains("madam") || tokens.Contains("duke") || tokens.Contains("duchess") || tokens.Contains("count") || tokens.Contains("countess"))
                TagUtil.AddTag(tags, "posh");

            if (tokens.Contains("dr") || tokens.Contains("doctor") || tokens.Contains("professor"))
                TagUtil.AddTag(tags, "calm");

            if (tokens.Contains("machine") || tokens.Contains("android") || tokens.Contains("robot") || tokens.Contains("unit"))
                TagUtil.AddTag(tags, "machine");

            // Special-case: Loporrit naming quirk ("Way")
            if (n.EndsWith(" Way", StringComparison.OrdinalIgnoreCase))
                TagUtil.AddTag(tags, "loporrit");

            return TagUtil.NormDistinct(tags);
        }

        private static HashSet<string> Tokenize(string s)
        {
            // Split on non-letters/numbers, keep words lowercased.
            var parts = Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9]+")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            return new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
        }
    }
}
