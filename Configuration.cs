// File: ConfigWindow.cs
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using ImGuiNET; // This is the fix
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace NpcVoiceMaster
{
    public class ConfigWindow : Window
    {
        private readonly Plugin _plugin;
        private string _status = "";
        private DateTime _statusTime = DateTime.MinValue;

        // Voice list
        private List<string> _voices = new();
        private bool _loadingVoices;
        private string _voiceFilter = "";

        // Bucket UI add (single)
        private string _bucketToAddVoice = "male";
        private int _voiceToAddIndex = -1;

        // Bulk add UI
        private string _bulkVoicesText = "";
        private bool _bulkValidateAgainstVoiceList = true;

        // Keyword rule add
        private string _newKeyword = "";
        private string _newKeywordBucket = "monster";

        // Exact NPC overrides add
        private string _exactNpcKey = "";
        private string _exactBucket = "male";
        private int _exactVoiceIndex = -1;

        // Assignment tools
        private string _resetNpcKey = "";

        // Per-bucket collapse state
        private readonly Dictionary<string, bool> _bucketOpen = new(StringComparer.OrdinalIgnoreCase);

        public ConfigWindow(Plugin plugin) : base("NPC Voice Master Settings")
        {
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(700, 760) };
            _plugin = plugin;
            _ = RefreshVoicesAsync();
        }

        public override void Draw()
        {
            DrawStatusBanner();

            ImGui.TextWrapped("NPC Voice Master: buckets + random assignment + manual overrides + cache.");
            ImGui.Separator();

            if (ImGui.BeginTabBar("##NpcVoiceMasterTabs", ImGuiTabBarFlags.Reorderable))
            {
                if (ImGui.BeginTabItem("AllTalk"))
                {
                    DrawAllTalkSection();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Buckets"))
                {
                    DrawBucketsSection();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Rules"))
                {
                    DrawKeywordRulesSection();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Overrides"))
                {
                    DrawExactOverridesSection();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Cache"))
                {
                    DrawCacheSection();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Tools"))
                {
                    DrawToolsSection();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        private void DrawStatusBanner()
        {
            if (!string.IsNullOrWhiteSpace(_status) && (DateTime.UtcNow - _statusTime).TotalSeconds < 8)
            {
                ImGui.TextWrapped(_status);
                ImGui.Separator();
            }
        }

        private void DrawAllTalkSection()
        {
            ImGui.TextWrapped("AllTalk Connection");
            ImGui.Spacing();

            var url = _plugin.Configuration.AllTalkBaseUrl ?? "";
            if (ImGui.InputText("AllTalk Base URL", ref url, 240))
            {
                _plugin.Configuration.AllTalkBaseUrl = url;
                _plugin.Configuration.Save();
            }

            ImGui.Spacing();

            if (_loadingVoices)
            {
                ImGui.BeginDisabled(true);
                ImGui.Button("Refreshing voices...");
                ImGui.EndDisabled();
            }
            else
            {
                if (ImGui.Button("Refresh Voice List"))
                    _ = RefreshVoicesAsync();
            }

            ImGui.SameLine();
            ImGui.TextDisabled("GET /api/voices");

            ImGui.Spacing();
            ImGui.InputText("Voice Filter", ref _voiceFilter, 200);

            var filtered = FilterVoices(_voices, _voiceFilter);
            var current = (_plugin.Configuration.AllTalkVoice ?? "").Trim();
            var preview = string.IsNullOrWhiteSpace(current) ? "<none>" : current;

            if (ImGui.BeginCombo("Default Voice (fallback)", preview))
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    bool sel = string.Equals(filtered[i], current, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(filtered[i], sel))
                    {
                        _plugin.Configuration.AllTalkVoice = filtered[i];
                        _plugin.Configuration.Save();
                        SetStatus($"Default voice set: {filtered[i]}");
                    }
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            ImGui.TextDisabled("Used if no override/bucket voice can be resolved.");
        }

        private void DrawBucketsSection()
        {
            _plugin.Configuration.VoiceBuckets ??= new List<VoiceBucket>();
            // EnsureDefaultBucketsExist() removed or handled inside Plugin/Config if needed, 
            // but we'll assume config is loaded. 

            ImGui.TextWrapped("Voice Buckets");
            ImGui.TextDisabled("Add voices to each bucket. Each NPC gets a random voice ONCE, then keeps it.");
            ImGui.Spacing();

            var bucketNames = GetBucketNames();
            if (bucketNames.Count == 0) bucketNames.Add("male");

            if (ImGui.BeginCombo("Bucket", _bucketToAddVoice))
            {
                foreach (var bn in bucketNames)
                {
                    bool sel = string.Equals(bn, _bucketToAddVoice, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(bn, sel)) _bucketToAddVoice = bn;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            var filtered = FilterVoices(_voices, _voiceFilter);
            string voicePreview = (filtered.Count > 0 && _voiceToAddIndex >= 0 && _voiceToAddIndex < filtered.Count)
                ? filtered[_voiceToAddIndex] : "<pick voice>";

            if (ImGui.BeginCombo("Voice (single add)", voicePreview))
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    bool sel = (i == _voiceToAddIndex);
                    if (ImGui.Selectable(filtered[i], sel)) _voiceToAddIndex = i;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("Add Voice To Bucket"))
            {
                var b = GetBucket(_bucketToAddVoice);
                if (b != null && filtered.Count > 0 && _voiceToAddIndex >= 0 && _voiceToAddIndex < filtered.Count)
                {
                    var v = filtered[_voiceToAddIndex];
                    b.Voices ??= new List<string>();
                    if (!b.Voices.Contains(v))
                    {
                        b.Voices.Add(v);
                        _plugin.Configuration.Save();
                        SetStatus($"Added '{v}' to bucket '{b.Name}'.");
                    }
                }
            }

            ImGui.Spacing();
            ImGui.Separator();

            ImGui.TextWrapped("Bulk Add Voices");
            ImGui.Checkbox("Validate names", ref _bulkValidateAgainstVoiceList);
            ImGui.InputTextMultiline("##bulkvoices", ref _bulkVoicesText, 8000, new Vector2(-1, 120));

            if (ImGui.Button("Bulk ADD"))
                BulkApplyToBucket(_bucketToAddVoice, _bulkVoicesText, false, _bulkValidateAgainstVoiceList);
            ImGui.SameLine();
            if (ImGui.Button("Bulk REPLACE"))
                BulkApplyToBucket(_bucketToAddVoice, _bulkVoicesText, true, _bulkValidateAgainstVoiceList);

            ImGui.Spacing();
            ImGui.Separator();

            foreach (var b in _plugin.Configuration.VoiceBuckets)
            {
                if (b == null) continue;
                var name = (b.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                b.Voices ??= new List<string>();
                if (!_bucketOpen.ContainsKey(name)) _bucketOpen[name] = false;

                ImGui.SetNextItemOpen(_bucketOpen[name], ImGuiCond.Always);
                bool open = ImGui.CollapsingHeader($"Bucket: {name} ({b.Voices.Count})##{name}");
                _bucketOpen[name] = open;

                if (!open) continue;

                ImGui.Indent();
                if (ImGui.SmallButton($"Clear##{name}"))
                {
                    b.Voices.Clear();
                    _plugin.Configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton($"Copy##{name}"))
                {
                    ImGui.SetClipboardText(string.Join(Environment.NewLine, b.Voices));
                }

                for (int i = 0; i < b.Voices.Count; i++)
                {
                    ImGui.BulletText(b.Voices[i]);
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Del##{name}{i}"))
                    {
                        b.Voices.RemoveAt(i);
                        _plugin.Configuration.Save();
                        break;
                    }
                }
                ImGui.Unindent();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.InputText("NPC Key to reset", ref _resetNpcKey, 220);
            if (ImGui.Button("Reset Assigned Voice"))
            {
                // Simple logic to remove assignments
                _plugin.Configuration.NpcAssignedVoices?.RemoveAll(x => x.NpcKey == _resetNpcKey);
                _plugin.Configuration.Save();
                SetStatus("Reset.");
            }
        }

        private void DrawKeywordRulesSection()
        {
            _plugin.Configuration.BucketKeywordRules ??= new List<BucketKeywordRule>();

            ImGui.InputText("New Keyword", ref _newKeyword, 120);
            ImGui.SameLine();

            if (ImGui.BeginCombo("Bucket", _newKeywordBucket))
            {
                foreach (var bn in GetBucketNames())
                {
                    if (ImGui.Selectable(bn, bn == _newKeywordBucket)) _newKeywordBucket = bn;
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("Add Rule"))
            {
                if (!string.IsNullOrWhiteSpace(_newKeyword))
                {
                    _plugin.Configuration.BucketKeywordRules.Add(new BucketKeywordRule { Keyword = _newKeyword, BucketName = _newKeywordBucket });
                    _plugin.Configuration.Save();
                }
            }

            ImGui.Separator();

            for (int i = 0; i < _plugin.Configuration.BucketKeywordRules.Count; i++)
            {
                var r = _plugin.Configuration.BucketKeywordRules[i];
                var kw = r.Keyword;
                if (ImGui.InputText($"Kw##{i}", ref kw, 100)) { r.Keyword = kw; _plugin.Configuration.Save(); }
                ImGui.SameLine();
                ImGui.Text($"-> {r.BucketName}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Del##kw{i}"))
                {
                    _plugin.Configuration.BucketKeywordRules.RemoveAt(i);
                    _plugin.Configuration.Save();
                    break;
                }
            }
        }

        private void DrawExactOverridesSection()
        {
            _plugin.Configuration.NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();

            ImGui.Text("Exact Overrides");
            ImGui.InputText("NPC Key", ref _exactNpcKey, 200);

            var filtered = FilterVoices(_voices, _voiceFilter);
            var preview = (filtered.Count > 0 && _exactVoiceIndex >= 0 && _exactVoiceIndex < filtered.Count) ? filtered[_exactVoiceIndex] : "";

            if (ImGui.BeginCombo("Voice", preview))
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    if (ImGui.Selectable(filtered[i], i == _exactVoiceIndex)) _exactVoiceIndex = i;
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("Save Exact Override"))
            {
                if (!string.IsNullOrWhiteSpace(_exactNpcKey) && _exactVoiceIndex >= 0)
                {
                    // Simplified upsert
                    var list = _plugin.Configuration.NpcExactVoiceOverrides;
                    var exist = list.FirstOrDefault(x => x.NpcKey == _exactNpcKey);
                    if (exist != null) exist.Voice = filtered[_exactVoiceIndex];
                    else list.Add(new NpcExactVoiceOverride { NpcKey = _exactNpcKey, Voice = filtered[_exactVoiceIndex] });
                    _plugin.Configuration.Save();
                }
            }
        }

        private void DrawCacheSection()
        {
            var en = _plugin.Configuration.EnableCache;
            if (ImGui.Checkbox("Enable Cache", ref en)) { _plugin.Configuration.EnableCache = en; _plugin.Configuration.Save(); }

            var ov = _plugin.Configuration.CacheFolderOverride ?? "";
            if (ImGui.InputText("Cache Folder", ref ov, 300)) { _plugin.Configuration.CacheFolderOverride = ov; _plugin.Configuration.Save(); }

            ImGui.Text($"Effective: {_plugin.Configuration.GetEffectiveCacheFolder()}");
            if (ImGui.Button("Clear Cache"))
            {
                try
                {
                    var path = _plugin.Configuration.GetEffectiveCacheFolder();
                    if (Directory.Exists(path))
                    {
                        foreach (var f in Directory.GetFiles(path, "*.wav")) File.Delete(f);
                        SetStatus("Cache cleared.");
                    }
                }
                catch { }
            }
        }

        private void DrawToolsSection()
        {
            if (ImGui.Button("Test Audio")) { _plugin.RunVoiceTestFromUI(); SetStatus("Test sent."); }
        }

        private async Task RefreshVoicesAsync()
        {
            if (_loadingVoices) return;
            _loadingVoices = true;
            try
            {
                var v = await _plugin.FetchAllTalkVoicesAsync();
                _voices = v ?? new List<string>();
            }
            finally { _loadingVoices = false; }
        }

        private void BulkApplyToBucket(string bucketName, string text, bool replace, bool validate)
        {
            var b = GetBucket(bucketName);
            if (b == null) return;

            var lines = text.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();

            if (replace) b.Voices.Clear();
            foreach (var l in lines)
            {
                if (validate && !_voices.Contains(l)) continue;
                if (!b.Voices.Contains(l)) b.Voices.Add(l);
            }
            _plugin.Configuration.Save();
            SetStatus("Bulk applied.");
        }

        private VoiceBucket? GetBucket(string name)
            => _plugin.Configuration.VoiceBuckets?.FirstOrDefault(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase));

        private List<string> GetBucketNames()
            => _plugin.Configuration.VoiceBuckets?.Select(b => b.Name).ToList() ?? new List<string>();

        private static List<string> FilterVoices(List<string> voices, string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) return voices;
            return voices.Where(v => v.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void SetStatus(string s) { _status = s; _statusTime = DateTime.UtcNow; }
    }
}