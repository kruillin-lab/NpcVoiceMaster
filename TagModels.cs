using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace NPCVoiceMaster
{
    /// <summary>
    /// NPC-side metadata used for tag-based voice resolution.
    /// Suggestions only; user can override via UI.
    /// </summary>
    [Serializable]
    public sealed class NpcProfile
    {
        public List<string> RequiredVoiceTags { get; set; } = new();
        public List<string> PreferredVoiceTags { get; set; } = new();

        // Separate domain: identity/context tags (optional).
        public List<string> NpcTags { get; set; } = new();

        public string? Tone { get; set; } = null;
        public string? Accent { get; set; } = null;
    }

    /// <summary>
    /// Voice-side metadata used for tag matching + filtering.
    /// </summary>
    [Serializable]
    public sealed class VoiceProfile
    {
        public bool Enabled { get; set; } = true;
        public bool Reserved { get; set; } = false;

        public List<string> Tags { get; set; } = new();

        public string? Tone { get; set; } = null;
        public string? Accent { get; set; } = null;
    }

    internal static class TagUtil
    {
        public static string Norm(string? s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();

            // Collapse whitespace
            s = Regex.Replace(s, @"\s+", " ");

            // Friendly canonicalizations
            if (s == "female") return "woman";
            if (s == "women") return "woman";
            if (s == "males") return "male";
            if (s == "boys") return "boy";
            if (s == "girls") return "girl";

            // Common misspellings
            if (s == "loporit") return "loporrit";
            if (s == "loporits") return "loporrit";

            return s;
        }

        public static HashSet<string> ToSet(IEnumerable<string>? tags)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tags == null) return set;
            foreach (var t in tags)
            {
                var n = Norm(t);
                if (!string.IsNullOrWhiteSpace(n))
                    set.Add(n);
            }
            return set;
        }

        public static List<string> NormDistinct(IEnumerable<string>? tags)
        {
            return ToSet(tags).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public static bool Contains(IEnumerable<string>? tags, string? tag)
        {
            var n = Norm(tag);
            if (string.IsNullOrWhiteSpace(n) || tags == null) return false;
            foreach (var t in tags)
            {
                if (string.Equals(Norm(t), n, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public static void AddTag(List<string>? list, string? tag)
        {
            if (list == null) return;
            var n = Norm(tag);
            if (string.IsNullOrWhiteSpace(n)) return;
            if (!Contains(list, n))
                list.Add(n);
        }

        public static void RemoveTag(List<string>? list, string? tag)
        {
            if (list == null) return;
            var n = Norm(tag);
            if (string.IsNullOrWhiteSpace(n)) return;

            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (string.Equals(Norm(list[i]), n, StringComparison.OrdinalIgnoreCase))
                    list.RemoveAt(i);
            }
        }
    }

}
