using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;

namespace NPCVoiceMaster
{
    public sealed class ConfigWindow : Window, IDisposable
    {
        private readonly Plugin _plugin;

        private bool _enabledEdit;
        private bool _debugOverlayEdit;

        private string _allTalkBaseUrlEdit = "";
        private string _cacheFolderEdit = "";

        private string _status = "";

        // Tag UI state
        private string _voiceSearch = "";
        private string _npcSearch = "";


        // Per-combo filter text (for multi-select tag dropdowns)
        private readonly Dictionary<string, string> _tagFilter = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _stringFilter = new(StringComparer.OrdinalIgnoreCase);
        private bool _fetchClearFirst = false;

        private string _newNpcName = "";
        private string _newNpcRequiredTags = "";
        private string _newNpcPreferredTags = "";

        public ConfigWindow(Plugin plugin)
            : base("NPC Voice Master##NpcVoiceMasterConfig", ImGuiWindowFlags.None)
        {
            _plugin = plugin;

            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(700, 520),
                MaximumSize = new Vector2(1200, 900)
            };

            RespectCloseHotkey = true;

            _enabledEdit = _plugin.Configuration.Enabled;
            _debugOverlayEdit = _plugin.Configuration.DebugOverlayEnabled;

            _allTalkBaseUrlEdit = _plugin.Configuration.AllTalkBaseUrl ?? "";
            _cacheFolderEdit = _plugin.Configuration.CacheFolderOverride ?? "";

            EnsureCollections();
        }

        public void Dispose()
        {
        }

        public override void Draw()
        {
            EnsureCollections();

            if (ImGui.BeginTabBar("##nvm_tabs"))
            {
                if (ImGui.BeginTabItem("General"))
                {
                    DrawTab_General();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("AllTalk"))
                {
                    DrawTab_AllTalk();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Cache"))
                {
                    DrawTab_Cache();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Tag NPCs"))
                {
                    DrawTab_TagNpcs();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Tag Voices"))
                {
                    DrawTab_TagVoices();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }

            if (!string.IsNullOrWhiteSpace(_status))
            {
                ImGui.Separator();
                ImGui.TextColored(new Vector4(1f, 1f, 0.7f, 1f), _status);
            }
        }

        private void DrawTab_General()
        {
            if (ImGui.Checkbox("Enabled", ref _enabledEdit))
            {
                _plugin.Configuration.Enabled = _enabledEdit;
                _plugin.Configuration.Save();
                _status = "Saved Enabled.";
            }

            if (ImGui.Checkbox("Debug overlay", ref _debugOverlayEdit))
            {
                _plugin.SetDebugOverlayOpen(_debugOverlayEdit);
                _status = _debugOverlayEdit ? "Debug overlay enabled." : "Debug overlay disabled.";
            }

            ImGui.Separator();

            ImGui.TextUnformatted("Quick commands (targets):");
            ImGui.BulletText("/bucketmale  -> adds required voice-tag 'male' to the current target");
            ImGui.BulletText("/bucketlady  -> adds required voice-tag 'woman' to the current target");
            ImGui.BulletText("/bucketway   -> adds required voice-tag 'loporrit' to the current target");
            ImGui.BulletText("/bucketbot   -> adds required voice-tag 'machine' to the current target");
            ImGui.BulletText("/bucketmon   -> adds required voice-tag 'monsters' to the current target");

            ImGui.Separator();

            if (ImGui.Button("Open Repo"))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/kruillin-lab/NpcVoiceMaster",
                        UseShellExecute = true
                    });
                }
                catch
                {
                    _status = "Failed to open browser.";
                }
            }
        }

        private void DrawTab_AllTalk()
        {
            ImGui.TextUnformatted("AllTalk Base URL (example: http://10.0.0.80:7851)");
            ImGui.SetNextItemWidth(520);
            if (ImGui.InputText("##allt_base", ref _allTalkBaseUrlEdit, 512))
            {
                // live edit only
            }

            if (ImGui.Button("Save AllTalk URL"))
            {
                _plugin.Configuration.AllTalkBaseUrl = (_allTalkBaseUrlEdit ?? "").Trim();
                _plugin.Configuration.Save();
                _status = "Saved AllTalk URL.";
            }

            ImGui.Separator();

            ImGui.TextUnformatted("Voice sync");
            ImGui.Checkbox("Clear voice profiles first", ref _fetchClearFirst);

            if (ImGui.Button("Fetch voices from AllTalk"))
            {
                _status = "Fetching voices...";
                _ = FetchVoicesAsync();
            }

            ImGui.SameLine();
            if (ImGui.Button("Auto-tag all voices"))
            {
                AutoTagAllVoices();
            }

            ImGui.Separator();
            ImGui.TextUnformatted("Notes:");
            ImGui.BulletText("Buckets are removed. Everything is tag-based.");
            ImGui.BulletText("Voice tags come from filenames + your edits.");
        }

        private async System.Threading.Tasks.Task FetchVoicesAsync()
        {
            try
            {
                var voices = await _plugin.FetchAllTalkVoicesAsync();

                if (_fetchClearFirst)
                {
                    _plugin.Configuration.VoiceProfiles.Clear();
                }

                foreach (var raw in voices)
                {
                    var v = _plugin.NormalizeVoiceName(raw);
                    if (string.IsNullOrWhiteSpace(v)) continue;

                    if (!_plugin.Configuration.VoiceProfiles.TryGetValue(v, out var vp) || vp == null)
                    {
                        vp = new VoiceProfile();
                        _plugin.Configuration.VoiceProfiles[v] = vp;
                    }

                    // Conservative auto-tagging
                    var suggested = AutoTagger.SuggestVoiceTagsFromFilename(v);
                    vp.Tags ??= new List<string>();
                    foreach (var t in suggested)
                        TagUtil.AddTag(vp.Tags, t);

                    if (vp.Tags.Count == 0)
                        TagUtil.AddTag(vp.Tags, "default");

                    NormalizeTagsInPlace(vp.Tags);
                }

                _plugin.Configuration.Save();
                _status = $"Fetched {voices.Count} voices.";
            }
            catch (Exception ex)
            {
                _status = $"Fetch failed: {ex.Message}";
            }
        }

        private void DrawTab_Cache()
        {
            ImGui.TextUnformatted("Cache Folder Override (blank = default plugin cache folder)");
            ImGui.SetNextItemWidth(520);
            ImGui.InputText("##cache_override", ref _cacheFolderEdit, 1024);

            if (ImGui.Button("Save Cache Folder"))
            {
                _plugin.Configuration.CacheFolderOverride = (_cacheFolderEdit ?? "").Trim();
                _plugin.Configuration.Save();
                _status = "Saved cache folder override.";
            }

            ImGui.Separator();

            if (ImGui.Button("Open Cache Folder"))
            {
                try
                {
                    _plugin.EnsureCacheRootExists();
                    Process.Start("explorer.exe", _plugin.ResolvedCacheFolder);
                }
                catch (Exception ex)
                {
                    _status = $"Failed to open cache folder: {ex.Message}";
                }
            }

        }

        private void DrawTab_TagVoices()
        {
            ImGui.TextUnformatted("Voice profiles (tags + tone + accent).");
            ImGui.TextUnformatted("Matching uses NPC required/preferred voice-tags against these voice tags.");
            ImGui.Separator();

            ImGui.SetNextItemWidth(300);
            ImGui.InputTextWithHint("##voice_search", "Search voices...", ref _voiceSearch, 256);

            ImGui.SameLine();
            if (ImGui.Button("Auto-tag filtered"))
            {
                AutoTagFilteredVoices();
            }

            ImGui.Separator();

            var voices = _plugin.Configuration.VoiceProfiles
                .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Key)
                .ToList();

            if (!string.IsNullOrWhiteSpace(_voiceSearch))
                voices = voices.Where(v => v.Contains(_voiceSearch, StringComparison.OrdinalIgnoreCase)).ToList();

            var allTags = GetAllKnownTags();

            ImGui.BeginChild("##voices_scroll", new Vector2(0, 0), true);

            foreach (var voice in voices)
            {
                if (!_plugin.Configuration.VoiceProfiles.TryGetValue(voice, out var vp) || vp == null)
                    continue;

                var header = $"{voice}##voice_{voice}";
                if (!ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
                    continue;

                var enabled = vp.Enabled;
                if (ImGui.Checkbox($"Enabled##vp_en_{voice}", ref enabled))
                {
                    vp.Enabled = enabled;
                    _plugin.Configuration.Save();
                    _status = $"Saved voice: {voice}";
                }


var reserved = vp.Reserved;
if (ImGui.Checkbox($"Reserved (exclude from fallback)##vp_res_{voice}", ref reserved))
{
    vp.Reserved = reserved;
    if (reserved)
    {
        vp.Tags ??= new List<string>();
        TagUtil.RemoveTag(vp.Tags, "default");
    }
    _plugin.Configuration.Save();
    _status = $"Updated reserved for voice: {voice}";
}

ImGui.SameLine();
if (ImGui.Button($"Reserve (remove default)##vp_resbtn_{voice}"))
{
    vp.Reserved = true;
    vp.Tags ??= new List<string>();
    TagUtil.RemoveTag(vp.Tags, "default");
    _plugin.Configuration.Save();
    _status = $"Reserved voice: {voice}";
}

                // Accent (dropdown)
                var accent = vp.Accent ?? "";
                var accentOptions = BuildStringOptions(DefaultAccents, _plugin.Configuration, includeNpc:true, includeVoices:true, currentValue: accent, isAccent:true);
                ImGui.SetNextItemWidth(260);
                if (DrawStringCombo("Accent", $"vp_acc_{voice}", ref accent, accentOptions, 260f))
                    vp.Accent = string.IsNullOrWhiteSpace(accent) ? null : accent;

                // Tone (dropdown)
                var tone = vp.Tone ?? "";
                var toneOptions = BuildStringOptions(DefaultTones, _plugin.Configuration, includeNpc:true, includeVoices:true, currentValue: tone, isAccent:false);
                ImGui.SetNextItemWidth(260);
                if (DrawStringCombo("Tone", $"vp_tone_{voice}", ref tone, toneOptions, 260f))
                    vp.Tone = string.IsNullOrWhiteSpace(tone) ? null : tone;
                // Tags (multi-select dropdown from existing tags)
                vp.Tags ??= new List<string>();
                var tags = vp.Tags;
                if (DrawTagMultiSelectCombo("Tags", $"vp_tags_{voice}", tags, allTags, 520f))
                {
                    NormalizeTagsInPlace(vp.Tags);
                }
                if (ImGui.Button($"Save##vp_save_{voice}"))
                {
                    NormalizeTagsInPlace(vp.Tags);
                    vp.Accent = (vp.Accent ?? "").Trim();
                    vp.Tone = (vp.Tone ?? "").Trim();
                    _plugin.Configuration.Save();
                    _status = $"Saved voice: {voice}";
                }

                ImGui.SameLine();
                if (ImGui.Button($"Auto-tag##vp_aut_{voice}"))
                {
                    var suggested = AutoTagger.SuggestVoiceTagsFromFilename(voice);
                    vp.Tags ??= new List<string>();
                    foreach (var t in suggested) TagUtil.AddTag(vp.Tags, t);
                    if (vp.Tags.Count == 0) TagUtil.AddTag(vp.Tags, "default");
                    NormalizeTagsInPlace(vp.Tags);
                    _plugin.Configuration.Save();
                    _status = $"Auto-tagged: {voice}";
                }

                ImGui.Separator();
            }

            ImGui.EndChild();
        }

        private void DrawTab_TagNpcs()
        {
            ImGui.TextUnformatted("NPC profiles (required/preferred voice-tags + tone + accent).");
            ImGui.Separator();

            ImGui.SetNextItemWidth(300);
            ImGui.InputTextWithHint("##npc_search", "Search NPCs...", ref _npcSearch, 256);


            ImGui.Spacing();
            ImGui.TextUnformatted("Current target tools");
            if (ImGui.Button("Clear current target NPC (start fresh)"))
            {
                _plugin.ClearCurrentTargetNpcCompletely();
            }
            ImGui.Separator();

                        ImGui.Separator();

            var allTags = GetAllKnownTags();

            // Add new NPC profile
            ImGui.TextUnformatted("Add / edit NPC profile");
            ImGui.SetNextItemWidth(260);
            ImGui.InputTextWithHint("##new_npc_name", "NPC Name (exact)", ref _newNpcName, 128);            // Required / Preferred voice-tags (multi-select dropdown from existing tags)
            var newReq = ParseCsvTags(_newNpcRequiredTags);
            if (DrawTagMultiSelectCombo("Required voice-tags", "new_npc_req", newReq, allTags, 520f))
            {
                _newNpcRequiredTags = string.Join(", ", TagUtil.NormDistinct(newReq));
            }

            var newPref = ParseCsvTags(_newNpcPreferredTags);
            if (DrawTagMultiSelectCombo("Preferred voice-tags", "new_npc_pref", newPref, allTags, 520f))
            {
                _newNpcPreferredTags = string.Join(", ", TagUtil.NormDistinct(newPref));
            }
            if (ImGui.Button("Save NPC Profile"))
            {
                var name = (_newNpcName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    _status = "NPC name required.";
                }
                else
                {
                    if (!_plugin.Configuration.NpcProfiles.TryGetValue(name, out var np) || np == null)
                    {
                        np = new NpcProfile();
                        _plugin.Configuration.NpcProfiles[name] = np;
                    }

                    np.RequiredVoiceTags = ParseCsvTags(_newNpcRequiredTags);
                    np.PreferredVoiceTags = ParseCsvTags(_newNpcPreferredTags);

                    _plugin.Configuration.NpcAssignedVoices.Remove(name);

                    _plugin.Configuration.Save();
                    _status = $"Saved NPC profile: {name}";
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Suggest tags from name"))
            {
                var name = (_newNpcName ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    var suggested = AutoTagger.SuggestNpcVoiceTagsFromName(name);
                    _newNpcPreferredTags = string.Join(", ", suggested);
                    _status = "Suggested preferred tags filled.";
                }
            }

            ImGui.Separator();

            // Build list of NPCs we know about
            var npcSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var k in _plugin.Configuration.NpcProfiles.Keys)
                npcSet.Add(k);

            foreach (var k in _plugin.Configuration.NpcAssignedVoices.Keys)
                npcSet.Add(k);

            foreach (var o in _plugin.Configuration.NpcExactVoiceOverrides.Where(x => x != null))
                if (!string.IsNullOrWhiteSpace(o.NpcKey))
                    npcSet.Add(o.NpcKey);

            var npcs = npcSet.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrWhiteSpace(_npcSearch))
                npcs = npcs.Where(n => n.Contains(_npcSearch, StringComparison.OrdinalIgnoreCase)).ToList();

            ImGui.BeginChild("##npcs_scroll", new Vector2(0, 0), true);

            foreach (var npc in npcs)
            {
                _plugin.Configuration.NpcProfiles.TryGetValue(npc, out var np);
                np ??= new NpcProfile();

                var assigned = _plugin.Configuration.NpcAssignedVoices.TryGetValue(npc, out var a) ? a : "";

                var header = $"{npc}  {(string.IsNullOrWhiteSpace(assigned) ? "" : $"-> {assigned}")}##npc_{npc}";
                if (!ImGui.CollapsingHeader(header, ImGuiTreeNodeFlags.DefaultOpen))
                    continue;                // Required voice-tags (multi-select dropdown from existing tags)
                var reqTags = np.RequiredVoiceTags ?? new List<string>();
                if (DrawTagMultiSelectCombo("Required voice-tags", $"np_req_{npc}", reqTags, allTags, 520f))
                {
                    np.RequiredVoiceTags = TagUtil.NormDistinct(reqTags);
                }

                // Preferred voice-tags (multi-select dropdown from existing tags)
                var prefTags = np.PreferredVoiceTags ?? new List<string>();
                if (DrawTagMultiSelectCombo("Preferred voice-tags", $"np_pref_{npc}", prefTags, allTags, 520f))
                {
                    np.PreferredVoiceTags = TagUtil.NormDistinct(prefTags);
                }
                // Accent / Tone (dropdowns)
                var accent = np.Accent ?? "";
                var accentOptions = BuildStringOptions(DefaultAccents, _plugin.Configuration, includeNpc:true, includeVoices:true, currentValue: accent, isAccent:true);
                ImGui.SetNextItemWidth(260);
                if (DrawStringCombo("Accent", $"np_acc_{npc}", ref accent, accentOptions, 260f))
                    np.Accent = string.IsNullOrWhiteSpace(accent) ? null : accent;

                var tone = np.Tone ?? "";
                var toneOptions = BuildStringOptions(DefaultTones, _plugin.Configuration, includeNpc:true, includeVoices:true, currentValue: tone, isAccent:false);
                ImGui.SetNextItemWidth(260);
                if (DrawStringCombo("Tone", $"np_tone_{npc}", ref tone, toneOptions, 260f))
                    np.Tone = string.IsNullOrWhiteSpace(tone) ? null : tone;


// Voice override (optional): choose from voices that match this NPC's current tags.
DrawVoiceOverrideForNpcRow(npc, np);

                if (ImGui.Button($"Save##np_save_{npc}"))
                {
                    if (!_plugin.Configuration.NpcProfiles.ContainsKey(npc))
                        _plugin.Configuration.NpcProfiles[npc] = np;

                    np.RequiredVoiceTags = TagUtil.NormDistinct(np.RequiredVoiceTags);
                    np.PreferredVoiceTags = TagUtil.NormDistinct(np.PreferredVoiceTags);
                    np.Accent = (np.Accent ?? "").Trim();
                    np.Tone = (np.Tone ?? "").Trim();

                    // Clear sticky assignment so changes apply immediately.
                    _plugin.Configuration.NpcAssignedVoices.Remove(npc);

                    _plugin.Configuration.Save();
                    _status = $"Saved NPC: {npc}";
                }

                ImGui.SameLine();
                if (ImGui.Button($"Suggest##np_sug_{npc}"))
                {
                    var suggested = AutoTagger.SuggestNpcVoiceTagsFromName(npc);
                    np.PreferredVoiceTags = TagUtil.NormDistinct(suggested);
                    _plugin.Configuration.Save();
                    _status = $"Suggested tags for: {npc}";
                }

                ImGui.SameLine();
                if (ImGui.Button($"Clear assigned voice##np_clear_{npc}"))
                {
                    _plugin.Configuration.NpcAssignedVoices.Remove(npc);
                    _plugin.Configuration.Save();
                    _status = $"Cleared assigned voice: {npc}";
                }

                ImGui.Separator();
            }

            ImGui.EndChild();
        }

        private void EnsureCollections()
        {
            _plugin.Configuration.NpcProfiles ??= new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase);
            _plugin.Configuration.VoiceProfiles ??= new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);
            _plugin.Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _plugin.Configuration.NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();
        }

        private static List<string> ParseCsvTags(string csv)
        {
            csv ??= "";
            var parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return TagUtil.NormDistinct(parts);
        }

        
        private List<string> GetAllKnownTags()
        {
            // "Existing tags" = anything already used in voice profiles or NPC profiles, plus reserved defaults.
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Predefined tag seeds (so dropdowns never "forget" useful tags)
            foreach (var t in DefaultTagSeeds)
            {
                var n = TagUtil.Norm(t);
                if (!string.IsNullOrWhiteSpace(n)) set.Add(n);
            }

            // Voice tags
            if (_plugin.Configuration.VoiceProfiles != null)
            {
                foreach (var vp in _plugin.Configuration.VoiceProfiles.Values)
                {
                    if (vp?.Tags == null) continue;
                    foreach (var t in vp.Tags)
                    {
                        var n = TagUtil.Norm(t);
                        if (!string.IsNullOrWhiteSpace(n)) set.Add(n);
                    }
                }
            }

            // NPC required/preferred tags
            if (_plugin.Configuration.NpcProfiles != null)
            {
                foreach (var np in _plugin.Configuration.NpcProfiles.Values)
                {
                    if (np == null) continue;

                    if (np.RequiredVoiceTags != null)
                        foreach (var t in np.RequiredVoiceTags)
                        {
                            var n = TagUtil.Norm(t);
                            if (!string.IsNullOrWhiteSpace(n)) set.Add(n);
                        }

                    if (np.PreferredVoiceTags != null)
                        foreach (var t in np.PreferredVoiceTags)
                        {
                            var n = TagUtil.Norm(t);
                            if (!string.IsNullOrWhiteSpace(n)) set.Add(n);
                        }
                }
            }

            return set.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList();
        }

        
        private static void NormalizeTagsInPlace(List<string> tags)
        {
            if (tags == null) return;
            var norm = TagUtil.NormDistinct(tags);
            tags.Clear();
            tags.AddRange(norm);
        }



        // Predefined tag seeds. These ensure important tags stay available in dropdowns even if you haven't used them yet.
        // Keep these normalized (lowercase, underscores) because TagUtil.Norm() will normalize user input the same way.
        private static readonly string[] DefaultTagSeeds = new[]
        {
            // Former buckets / common
            "male", "woman", "boy", "girl", "loporrit", "machine", "monsters", "default",

            // Monster sizing / general categories
            "big_monster", "little_monster", "humanoid", "beast", "dragon", "voidsent",

            // Playable races + tribes/clans (common NPC identifiers too)
            "hyur", "midlander", "highlander",
            "elezen", "wildwood", "duskwight",
            "lalafell", "plainsfolk", "dunesfolk",
            "miqote", "seeker_of_the_sun", "keeper_of_the_moon",
            "roegadyn", "sea_wolf", "hellsguard",
            "au_ra", "raen", "xaela",
            "viera", "rava", "veena",
            "hrothgar", "helions", "the_lost",

            // Common NPC-only identities (optional but handy)
            "garlean", "padjal",
        };

        // Predefined tone/accent packs (dropdown options). These are just seeds; the dropdown also includes any values
        // already used in your NPC/Voice profiles.
        private static readonly string[] DefaultAccents = new[]
        {
            "", "us", "uk", "irish", "scottish", "welsh", "australian", "new_zealand", "canadian",
            "south_african", "indian", "jamaican"
        };

        private static readonly string[] DefaultTones = new[]
        {
            "", "neutral", "warm", "cold", "cheerful", "serious", "calm", "sleepy", "whispery",
            "gruff", "old", "young", "posh", "rough", "robotic"
        };

        private List<string> BuildStringOptions(IEnumerable<string> defaults, Configuration cfg, bool includeNpc, bool includeVoices, string currentValue, bool isAccent)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in defaults)
                if (!string.IsNullOrWhiteSpace(d)) set.Add(d.Trim());

            if (includeVoices && cfg.VoiceProfiles != null)
            {
                foreach (var vp in cfg.VoiceProfiles.Values)
                {
                    var v = isAccent ? vp.Accent : vp.Tone;
                    if (!string.IsNullOrWhiteSpace(v)) set.Add(v.Trim());
                }
            }

            if (includeNpc && cfg.NpcProfiles != null)
            {
                foreach (var np in cfg.NpcProfiles.Values)
                {
                    var v = isAccent ? np.Accent : np.Tone;
                    if (!string.IsNullOrWhiteSpace(v)) set.Add(v.Trim());
                }
            }

            // Ensure current value is present (even if it's custom)
            if (!string.IsNullOrWhiteSpace(currentValue))
                set.Add(currentValue.Trim());

            var list = set.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            list.Insert(0, ""); // (None)
            return list;
        }

        private bool DrawStringCombo(string label, string id, ref string value, List<string> options, float width)
        {
            // Keep filters per-combo
            if (!_stringFilter.TryGetValue(id, out var filter))
                filter = "";

            var preview = string.IsNullOrWhiteSpace(value) ? "(None)" : value;
            var changed = false;

            // Constrain combo + popup width so it doesn't stretch across the whole screen
            if (width <= 0) width = 260f;
            ImGui.SetNextItemWidth(width);
            ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width, 420));

            if (ImGui.BeginCombo($"{label}##{id}", preview))
            {
                ImGui.SetNextItemWidth(-1);
                if (ImGui.InputText($"Filter##{id}_flt", ref filter, 64))
                    _stringFilter[id] = filter;

                ImGui.Separator();

                // (None)
                var noneSelected = string.IsNullOrWhiteSpace(value);
                if (ImGui.Selectable($"(None)##{id}_none", noneSelected))
                {
                    value = "";
                    changed = true;
                }

                foreach (var opt in options)
                {
                    if (string.IsNullOrWhiteSpace(opt))
                        continue;

                    if (!string.IsNullOrWhiteSpace(filter) &&
                        opt.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var selected = string.Equals(value, opt, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable($"{opt}##{id}_{opt}", selected))
                    {
                        value = opt;
                        changed = true;
                    }
                }

                ImGui.EndCombo();
            }

            return changed;
        }

bool DrawTagMultiSelectCombo(string label, string id, List<string>? selectedTags, List<string> allTags, float width = 520f)
        {
            selectedTags ??= new List<string>();
            NormalizeTagsInPlace(selectedTags);

            var preview = selectedTags.Count == 0 ? "<none>" : string.Join(", ", selectedTags);

            ImGui.SetNextItemWidth(width);

            var changed = false;
            if (ImGui.BeginCombo($"{label}##{id}", preview))
            {
                // filter box (per combo)
                if (!_tagFilter.TryGetValue(id, out var filter))
                    filter = "";

                ImGui.SetNextItemWidth(width - 60);
                ImGui.InputTextWithHint($"##tag_filter_{id}", "Filter tags...", ref filter, 128);
                _tagFilter[id] = filter;

                ImGui.SameLine();
                if (ImGui.Button($"Clear##tag_filter_clear_{id}"))
                {
                    filter = "";
                    _tagFilter[id] = filter;
                }

                ImGui.Separator();

                var view = allTags;
                if (!string.IsNullOrWhiteSpace(filter))
                    view = allTags.Where(t => t.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

                // Selected tags first (makes it easy to review)
                foreach (var tag in view)
                {
                    var isSelected = selectedTags.Any(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));

                    // Use Selectable with DontClosePopups so the combo stays open while you click multiple tags.
                    // We render a simple [x]/[ ] indicator instead of a Checkbox, because Checkbox can close the combo popup in some bindings.
                    var prefix = isSelected ? "[x] " : "[ ] ";
                    var rowLabel = prefix + tag;

                    if (ImGui.Selectable($"{rowLabel}##{id}::{tag}", false, ImGuiSelectableFlags.DontClosePopups))
                    {
                        changed = true;
                        if (!isSelected)
                            TagUtil.AddTag(selectedTags, tag);
                        else
                            selectedTags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                        NormalizeTagsInPlace(selectedTags);
                    }
                }

                ImGui.EndCombo();
            }

            if (changed)
            {
                // Normalize in-place so selections persist.
                var norm = TagUtil.NormDistinct(selectedTags);
                selectedTags.Clear();
                selectedTags.AddRange(norm);
            }

            // NOTE: caller owns assigning back to the model.
            return changed;
        }


// ------------------------------------------------------------
// Voice override UI (filtered by current tags)
// ------------------------------------------------------------

private void DrawVoiceOverrideForNpcRow(string npcName, NpcProfile np)
{
    npcName = (npcName ?? "").Trim();
    if (string.IsNullOrWhiteSpace(npcName))
        return;

    np ??= new NpcProfile();

    var candidates = BuildVoiceCandidatesForNpc(np, out var strictMatches);
    var currentOverride = GetExactOverrideVoice(npcName);

    ImGui.TextUnformatted("Voice override (optional)");
    var preview = string.IsNullOrWhiteSpace(currentOverride) ? "(Auto - use resolver)" : currentOverride;

    ImGui.SetNextItemWidth(420f);
    if (ImGui.BeginCombo($"Voice (matches tags)##np_voice_{npcName}", preview))
    {
        if (ImGui.Selectable($"(Auto - use resolver)##np_voice_auto_{npcName}", string.IsNullOrWhiteSpace(currentOverride)))
        {
            SetExactOverrideVoice(npcName, "");
            currentOverride = "";
        }

        foreach (var v in candidates)
        {
            var selected = string.Equals(currentOverride, v, StringComparison.OrdinalIgnoreCase);
            var vid = MakeSafeId(v);
            if (ImGui.Selectable($"{v}##np_voice_{npcName}_{vid}", selected))
            {
                SetExactOverrideVoice(npcName, v);
                currentOverride = v;
            }
        }

        ImGui.EndCombo();
    }

    ImGui.SameLine();
    if (ImGui.Button($"Clear override##np_voice_clear_{npcName}"))
    {
        SetExactOverrideVoice(npcName, "");
    }

    if (candidates.Count == 0)
    {
        ImGui.TextDisabled("No enabled voices found.");
    }
    else if (!strictMatches)
    {
        ImGui.TextDisabled($"No voices match REQUIRED tags; showing all enabled voices ({candidates.Count}).");
    }
    else
    {
        ImGui.TextDisabled($"Matching voices: {candidates.Count}");
    }
}

private List<string> BuildVoiceCandidatesForNpc(NpcProfile np, out bool strictMatches)
{
    strictMatches = true;

    var req = TagUtil.NormDistinct(np.RequiredVoiceTags ?? new List<string>());
    var pref = TagUtil.NormDistinct(np.PreferredVoiceTags ?? new List<string>());

    var reqSet = new HashSet<string>(req, StringComparer.OrdinalIgnoreCase);
    var prefSet = new HashSet<string>(pref, StringComparer.OrdinalIgnoreCase);

    var scored = new List<(string Voice, int Score)>();

    if (_plugin.Configuration.VoiceProfiles == null || _plugin.Configuration.VoiceProfiles.Count == 0)
        return new List<string>();

    foreach (var kv in _plugin.Configuration.VoiceProfiles)
    {
        var voice = kv.Key;
        var vp = kv.Value;
        if (vp == null) continue;
        if (!vp.Enabled) continue;

        var vtags = TagUtil.NormDistinct(vp.Tags ?? new List<string>());
        var vset = new HashSet<string>(vtags, StringComparer.OrdinalIgnoreCase);

        bool ok = true;
        foreach (var t in reqSet)
        {
            if (!vset.Contains(t))
            {
                ok = false;
                break;
            }
        }

        if (!ok) continue;

        int score = 0;

        foreach (var t in prefSet)
            if (vset.Contains(t))
                score += 10;

        if (!string.IsNullOrWhiteSpace(np.Accent) &&
            !string.IsNullOrWhiteSpace(vp.Accent) &&
            string.Equals(np.Accent.Trim(), vp.Accent.Trim(), StringComparison.OrdinalIgnoreCase))
            score += 2;

        if (!string.IsNullOrWhiteSpace(np.Tone) &&
            !string.IsNullOrWhiteSpace(vp.Tone) &&
            string.Equals(np.Tone.Trim(), vp.Tone.Trim(), StringComparison.OrdinalIgnoreCase))
            score += 1;

        scored.Add((voice, score));
    }

    if (scored.Count == 0)
    {
        // If required tags are too strict, fall back to all enabled voices so you can still pick something.
        strictMatches = false;

        foreach (var kv in _plugin.Configuration.VoiceProfiles)
        {
            var voice = kv.Key;
            var vp = kv.Value;
            if (vp == null) continue;
            if (!vp.Enabled) continue;
            scored.Add((voice, 0));
        }
    }

    return scored
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Voice, StringComparer.OrdinalIgnoreCase)
        .Select(x => x.Voice)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

private string GetExactOverrideVoice(string npcName)
{
    npcName = (npcName ?? "").Trim();
    if (string.IsNullOrWhiteSpace(npcName))
        return "";

    var list = _plugin.Configuration.NpcExactVoiceOverrides;
    if (list == null) return "";

    foreach (var o in list)
    {
        if (o == null) continue;
        if (!o.Enabled) continue;

        var key = (o.NpcKey ?? "").Trim();
        if (string.Equals(key, npcName, StringComparison.OrdinalIgnoreCase))
            return (o.Voice ?? "").Trim();
    }

    return "";
}

private void SetExactOverrideVoice(string npcName, string voice)
{
    npcName = (npcName ?? "").Trim();
    if (string.IsNullOrWhiteSpace(npcName))
        return;

    voice = _plugin.NormalizeVoiceName((voice ?? "").Trim());

    _plugin.Configuration.NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();
    _plugin.Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Remove old entry (we'll re-add if needed)
    _plugin.Configuration.NpcExactVoiceOverrides.RemoveAll(o =>
        o != null && string.Equals((o.NpcKey ?? "").Trim(), npcName, StringComparison.OrdinalIgnoreCase));

    if (string.IsNullOrWhiteSpace(voice))
    {
        // Clear override + clear sticky assignment to re-resolve
        _plugin.Configuration.NpcAssignedVoices.Remove(npcName);
        _plugin.Configuration.Save();
        _status = $"Cleared voice override: {npcName}";
        return;
    }

    _plugin.Configuration.NpcExactVoiceOverrides.Add(new NpcExactVoiceOverride
    {
        NpcKey = npcName,
        Voice = voice,
        Enabled = true
    });

    // Keep sticky assignment aligned so previews update immediately.
    _plugin.Configuration.NpcAssignedVoices[npcName] = voice;

    _plugin.Configuration.Save();
    _status = $"Set voice override: {npcName} -> {voice}";
}

private static string MakeSafeId(string s)
{
    s ??= "";
    var sb = new System.Text.StringBuilder(s.Length);
    foreach (var ch in s)
    {
        if (char.IsLetterOrDigit(ch))
            sb.Append(ch);
        else
            sb.Append('_');
    }
    return sb.ToString();
}

private void AutoTagAllVoices()
        {
            foreach (var kv in _plugin.Configuration.VoiceProfiles)
            {
                var voice = kv.Key;
                var vp = kv.Value;
                if (vp == null) continue;

                var suggested = AutoTagger.SuggestVoiceTagsFromFilename(voice);
                vp.Tags ??= new List<string>();
                foreach (var t in suggested) TagUtil.AddTag(vp.Tags, t);
                if (vp.Tags.Count == 0) TagUtil.AddTag(vp.Tags, "default");
                NormalizeTagsInPlace(vp.Tags);
            }

            _plugin.Configuration.Save();
            _status = "Auto-tagged all voices.";
        }

        private void AutoTagFilteredVoices()
        {
            var voices = _plugin.Configuration.VoiceProfiles.Keys.ToList();
            if (!string.IsNullOrWhiteSpace(_voiceSearch))
                voices = voices.Where(v => v.Contains(_voiceSearch, StringComparison.OrdinalIgnoreCase)).ToList();

            foreach (var voice in voices)
            {
                if (!_plugin.Configuration.VoiceProfiles.TryGetValue(voice, out var vp) || vp == null)
                    continue;

                var suggested = AutoTagger.SuggestVoiceTagsFromFilename(voice);
                vp.Tags ??= new List<string>();
                foreach (var t in suggested) TagUtil.AddTag(vp.Tags, t);
                if (vp.Tags.Count == 0) TagUtil.AddTag(vp.Tags, "default");
                NormalizeTagsInPlace(vp.Tags);
            }

            _plugin.Configuration.Save();
            _status = "Auto-tagged filtered voices.";
        }
    }
}
