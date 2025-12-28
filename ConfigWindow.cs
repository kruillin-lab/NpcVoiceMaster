// File: ConfigWindow.cs
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
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

        // Bucket UI add
        private string _bucketToAddVoice = "male";
        private int _voiceToAddIndex = -1;

        // Keyword rule add
        private string _newKeyword = "";
        private string _newKeywordBucket = "monster";

        // Exact NPC overrides add
        private string _exactNpcKey = "";
        private string _exactBucket = "male";
        private int _exactVoiceIndex = -1;

        // Assignment tools
        private string _resetNpcKey = "";

        public ConfigWindow(Plugin plugin) : base("NPC Voice Master Settings")
        {
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(640, 720) };
            _plugin = plugin;

            _ = RefreshVoicesAsync();
        }

        public override void Draw()
        {
            if (!string.IsNullOrWhiteSpace(_status) && (DateTime.UtcNow - _statusTime).TotalSeconds < 8)
            {
                ImGui.TextWrapped(_status);
                ImGui.Separator();
            }

            ImGui.TextWrapped("Buckets = automatic random voices per NPC type, remembered per NPC unless you override manually.");
            ImGui.Separator();

            DrawAllTalkSection();
            ImGui.Separator();

            DrawBucketsSection();
            ImGui.Separator();

            DrawKeywordRulesSection();
            ImGui.Separator();

            DrawExactOverridesSection();
            ImGui.Separator();

            DrawCacheSection();
            ImGui.Separator();

            if (ImGui.Button("Test Audio (/voicetest)"))
            {
                _plugin.RunVoiceTestFromUI();
                SetStatus("Sent /voicetest (watch chat + listen).");
            }

            ImGui.SameLine();
            if (ImGui.Button("Print Fingerprint (/voicefinger)"))
                SetStatus("Type /voicefinger in chat to confirm loaded DLL.");

            ImGui.Spacing();
            ImGui.TextWrapped("Tip: test buckets in chat:");
            ImGui.TextWrapped("  /voicetest npc=Cid type=male Hello");
            ImGui.TextWrapped("  /voicetest npc=Livingway type=loporrit Hello");
        }

        private void DrawAllTalkSection()
        {
            ImGui.TextWrapped("AllTalk Connection");
            ImGui.Spacing();

            var url = _plugin.Configuration.AllTalkBaseUrl ?? "";
            if (ImGui.InputText("AllTalk Base URL", ref url, 220))
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

            // Default fallback voice
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

            ImGui.TextDisabled("Used if no manual override and bucket has no voices.");
        }

        private void DrawBucketsSection()
        {
            _plugin.Configuration.VoiceBuckets ??= new List<VoiceBucket>();
            EnsureDefaultBucketsExist();

            ImGui.TextWrapped("Voice Buckets");
            ImGui.TextDisabled("Add voices to each bucket. NPCs will get a random voice from their bucket once, then keep it.");

            ImGui.Spacing();

            // Add voice to bucket controls
            var bucketNames = _plugin.Configuration.VoiceBuckets.Select(b => (b?.Name ?? "").Trim()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bucketNames.Sort(StringComparer.OrdinalIgnoreCase);

            if (bucketNames.Count == 0)
                bucketNames.Add("male");

            // Bucket dropdown
            if (ImGui.BeginCombo("Bucket to add voice to", _bucketToAddVoice))
            {
                foreach (var bn in bucketNames)
                {
                    bool sel = string.Equals(bn, _bucketToAddVoice, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(bn, sel))
                        _bucketToAddVoice = bn;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            // Voice dropdown
            var filtered = FilterVoices(_voices, _voiceFilter);
            string voicePreview = (filtered.Count > 0 && _voiceToAddIndex >= 0 && _voiceToAddIndex < filtered.Count)
                ? filtered[_voiceToAddIndex]
                : "<pick voice>";

            if (ImGui.BeginCombo("Voice to add", voicePreview))
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    bool sel = (i == _voiceToAddIndex);
                    if (ImGui.Selectable(filtered[i], sel))
                        _voiceToAddIndex = i;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("Add Voice To Bucket"))
            {
                var b = GetBucket(_bucketToAddVoice);
                if (b == null)
                {
                    SetStatus("Bucket not found.");
                }
                else if (filtered.Count == 0 || _voiceToAddIndex < 0 || _voiceToAddIndex >= filtered.Count)
                {
                    SetStatus("Pick a voice first.");
                }
                else
                {
                    var v = filtered[_voiceToAddIndex];
                    b.Voices ??= new List<string>();
                    if (!b.Voices.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                    {
                        b.Voices.Add(v);
                        _plugin.Configuration.Save();
                        SetStatus($"Added '{v}' to bucket '{b.Name}'.");
                    }
                    else
                    {
                        SetStatus($"'{v}' already in bucket '{b.Name}'.");
                    }
                }
            }

            ImGui.Separator();

            // Show each bucket contents
            foreach (var b in _plugin.Configuration.VoiceBuckets)
            {
                if (b == null) continue;

                var name = (b.Name ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) continue;

                ImGui.TextWrapped($"Bucket: {name}  (voices: {(b.Voices?.Count ?? 0)})");

                b.Voices ??= new List<string>();
                for (int i = 0; i < b.Voices.Count; i++)
                {
                    ImGui.BulletText(b.Voices[i] ?? "");

                    ImGui.SameLine();
                    if (ImGui.SmallButton($"Remove##{name}_{i}"))
                    {
                        b.Voices.RemoveAt(i);
                        _plugin.Configuration.Save();
                        SetStatus($"Removed voice from bucket '{name}'.");
                        break;
                    }
                }

                ImGui.Spacing();
                ImGui.Separator();
            }

            // Reset assignment tool
            ImGui.TextWrapped("Reset a single NPC's assigned voice (forces re-roll next time):");
            ImGui.InputText("NPC Key to reset", ref _resetNpcKey, 200);
            if (ImGui.Button("Reset Assigned Voice For NPC"))
            {
                var npc = (_resetNpcKey ?? "").Trim();
                if (string.IsNullOrWhiteSpace(npc))
                {
                    SetStatus("Enter an NPC key/name.");
                }
                else
                {
                    int removed = 0;
                    _plugin.Configuration.NpcAssignedVoices ??= new List<NpcAssignedVoice>();
                    for (int i = _plugin.Configuration.NpcAssignedVoices.Count - 1; i >= 0; i--)
                    {
                        var a = _plugin.Configuration.NpcAssignedVoices[i];
                        if (a == null) continue;
                        if (string.Equals((a.NpcKey ?? "").Trim(), npc, StringComparison.OrdinalIgnoreCase))
                        {
                            _plugin.Configuration.NpcAssignedVoices.RemoveAt(i);
                            removed++;
                        }
                    }
                    _plugin.Configuration.Save();
                    SetStatus($"Removed {removed} assigned voice record(s) for '{npc}'.");
                }
            }
        }

        private void DrawKeywordRulesSection()
        {
            _plugin.Configuration.BucketKeywordRules ??= new List<BucketKeywordRule>();
            EnsureDefaultBucketsExist();

            ImGui.TextWrapped("Automatic Bucket Classification (keyword → bucket)");
            ImGui.TextDisabled("First match wins. This is how NPCs get a bucket automatically.");

            ImGui.Spacing();

            ImGui.InputText("New Keyword", ref _newKeyword, 120);

            ImGui.SameLine();
            if (ImGui.BeginCombo("Bucket", _newKeywordBucket))
            {
                foreach (var bn in GetBucketNames())
                {
                    bool sel = string.Equals(bn, _newKeywordBucket, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(bn, sel))
                        _newKeywordBucket = bn;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("Add Keyword Rule"))
            {
                var k = (_newKeyword ?? "").Trim();
                if (string.IsNullOrWhiteSpace(k))
                {
                    SetStatus("Keyword is empty.");
                }
                else
                {
                    _plugin.Configuration.BucketKeywordRules.Add(new BucketKeywordRule { Keyword = k, BucketName = _newKeywordBucket });
                    _plugin.Configuration.Save();
                    SetStatus($"Added keyword rule: '{k}' -> '{_newKeywordBucket}'");
                }
            }

            ImGui.Separator();

            for (int i = 0; i < _plugin.Configuration.BucketKeywordRules.Count; i++)
            {
                var r = _plugin.Configuration.BucketKeywordRules[i];
                if (r == null) continue;

                var kw = r.Keyword ?? "";
                if (ImGui.InputText($"Keyword##kw{i}", ref kw, 120))
                {
                    r.Keyword = kw;
                    _plugin.Configuration.Save();
                }

                ImGui.SameLine();

                var bucket = (r.BucketName ?? "").Trim();
                if (ImGui.BeginCombo($"Bucket##b{i}", string.IsNullOrWhiteSpace(bucket) ? "<none>" : bucket))
                {
                    foreach (var bn in GetBucketNames())
                    {
                        bool sel = string.Equals(bn, bucket, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(bn, sel))
                        {
                            r.BucketName = bn;
                            _plugin.Configuration.Save();
                        }
                        if (sel) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##kwdel{i}"))
                {
                    _plugin.Configuration.BucketKeywordRules.RemoveAt(i);
                    _plugin.Configuration.Save();
                    SetStatus("Deleted keyword rule.");
                    break;
                }
            }
        }

        private void DrawExactOverridesSection()
        {
            EnsureDefaultBucketsExist();

            _plugin.Configuration.NpcExactBucketOverrides ??= new List<NpcExactBucketOverride>();
            _plugin.Configuration.NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();
            _plugin.Configuration.NpcContainsVoiceRules ??= new List<NpcContainsVoiceRule>();

            ImGui.TextWrapped("Manual Overrides (strongest wins)");
            ImGui.TextDisabled("Order of precedence: exact voice > contains voice > forced bucket > keyword bucket > fallback");

            ImGui.Separator();

            ImGui.TextWrapped("Add Exact NPC → Bucket Override");
            ImGui.InputText("Exact NPC Key (bucket)", ref _exactNpcKey, 180);

            ImGui.SameLine();
            if (ImGui.BeginCombo("Bucket (exact override)", _exactBucket))
            {
                foreach (var bn in GetBucketNames())
                {
                    bool sel = string.Equals(bn, _exactBucket, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(bn, sel))
                        _exactBucket = bn;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("Save Exact Bucket Override"))
            {
                var npc = (_exactNpcKey ?? "").Trim();
                if (string.IsNullOrWhiteSpace(npc))
                {
                    SetStatus("Enter an exact NPC key/name.");
                }
                else
                {
                    UpsertExactBucketOverride(npc, _exactBucket);
                    _plugin.Configuration.Save();
                    SetStatus($"Exact bucket override: '{npc}' -> '{_exactBucket}'");
                }
            }

            ImGui.Separator();

            ImGui.TextWrapped("Add Exact NPC → Voice Override");
            var npcVoiceKey = _exactNpcKey; // reuse same input text field
            // (we intentionally reuse the same text box to keep UI simple)

            var filtered = FilterVoices(_voices, _voiceFilter);
            string voicePreview = (filtered.Count > 0 && _exactVoiceIndex >= 0 && _exactVoiceIndex < filtered.Count)
                ? filtered[_exactVoiceIndex]
                : "<pick voice>";

            if (ImGui.BeginCombo("Voice (exact override)", voicePreview))
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    bool sel = (i == _exactVoiceIndex);
                    if (ImGui.Selectable(filtered[i], sel))
                        _exactVoiceIndex = i;
                    if (sel) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("Save Exact Voice Override"))
            {
                var npc = (npcVoiceKey ?? "").Trim();
                if (string.IsNullOrWhiteSpace(npc))
                {
                    SetStatus("Enter an exact NPC key/name (use the same 'Exact NPC Key' field above).");
                }
                else if (filtered.Count == 0 || _exactVoiceIndex < 0 || _exactVoiceIndex >= filtered.Count)
                {
                    SetStatus("Pick a voice first.");
                }
                else
                {
                    var v = filtered[_exactVoiceIndex];
                    UpsertExactVoiceOverride(npc, v);
                    _plugin.Configuration.Save();
                    SetStatus($"Exact voice override: '{npc}' -> '{v}'");
                }
            }

            ImGui.Separator();

            ImGui.TextWrapped("Contains NPC → Voice Rules (your old style)");
            ImGui.TextDisabled("First match wins. These override buckets.");

            for (int i = 0; i < _plugin.Configuration.NpcContainsVoiceRules.Count; i++)
            {
                var r = _plugin.Configuration.NpcContainsVoiceRules[i];
                if (r == null) continue;

                var match = r.Match ?? "";
                if (ImGui.InputText($"Match##m{i}", ref match, 120))
                {
                    r.Match = match;
                    _plugin.Configuration.Save();
                }

                ImGui.SameLine();
                var v = (r.Voice ?? "").Trim();
                if (ImGui.BeginCombo($"Voice##cv{i}", string.IsNullOrWhiteSpace(v) ? "<none>" : v))
                {
                    for (int j = 0; j < filtered.Count; j++)
                    {
                        bool sel = string.Equals(filtered[j], v, StringComparison.OrdinalIgnoreCase);
                        if (ImGui.Selectable(filtered[j], sel))
                        {
                            r.Voice = filtered[j];
                            _plugin.Configuration.Save();
                        }
                        if (sel) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }

                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##cvdel{i}"))
                {
                    _plugin.Configuration.NpcContainsVoiceRules.RemoveAt(i);
                    _plugin.Configuration.Save();
                    SetStatus("Deleted contains voice rule.");
                    break;
                }
            }

            ImGui.Separator();

            ImGui.TextWrapped("Exact Overrides List (bucket + voice)");

            // Exact bucket overrides list
            ImGui.TextWrapped("Exact NPC → Bucket:");
            for (int i = 0; i < _plugin.Configuration.NpcExactBucketOverrides.Count; i++)
            {
                var r = _plugin.Configuration.NpcExactBucketOverrides[i];
                if (r == null) continue;

                ImGui.BulletText($"{r.NpcKey} -> {r.BucketName}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##eb{i}"))
                {
                    _plugin.Configuration.NpcExactBucketOverrides.RemoveAt(i);
                    _plugin.Configuration.Save();
                    SetStatus("Deleted exact bucket override.");
                    break;
                }
            }

            ImGui.Spacing();

            // Exact voice overrides list
            ImGui.TextWrapped("Exact NPC → Voice:");
            for (int i = 0; i < _plugin.Configuration.NpcExactVoiceOverrides.Count; i++)
            {
                var r = _plugin.Configuration.NpcExactVoiceOverrides[i];
                if (r == null) continue;

                ImGui.BulletText($"{r.NpcKey} -> {r.Voice}");
                ImGui.SameLine();
                if (ImGui.SmallButton($"Delete##ev{i}"))
                {
                    _plugin.Configuration.NpcExactVoiceOverrides.RemoveAt(i);
                    _plugin.Configuration.Save();
                    SetStatus("Deleted exact voice override.");
                    break;
                }
            }
        }

        private void DrawCacheSection()
        {
            ImGui.TextWrapped("Local Audio Cache (WAV on disk)");

            var enableCache = _plugin.Configuration.EnableCache;
            if (ImGui.Checkbox("Enable Cache", ref enableCache))
            {
                _plugin.Configuration.EnableCache = enableCache;
                _plugin.Configuration.Save();
            }

            var cacheOverride = _plugin.Configuration.CacheFolderOverride ?? "";
            if (ImGui.InputText("Cache Folder Override (optional)", ref cacheOverride, 260))
            {
                _plugin.Configuration.CacheFolderOverride = cacheOverride;
                _plugin.Configuration.Save();
            }

            var effective = _plugin.Configuration.GetEffectiveCacheFolder();
            ImGui.TextWrapped($"Effective cache folder: {effective}");

            if (ImGui.Button("Clear Cached WAV Files"))
            {
                try
                {
                    var folder = effective ?? "";
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        SetStatus("Cache folder is empty/invalid.");
                    }
                    else
                    {
                        Directory.CreateDirectory(folder);
                        int deleted = 0;
                        foreach (var file in Directory.GetFiles(folder, "*.wav"))
                        {
                            try { File.Delete(file); deleted++; } catch { }
                        }
                        SetStatus($"Cache cleared. Deleted {deleted} .wav file(s).");
                    }
                }
                catch (Exception ex)
                {
                    SetStatus($"Cache clear failed: {ex.Message}");
                }
            }
        }

        private async Task RefreshVoicesAsync()
        {
            if (_loadingVoices) return;
            _loadingVoices = true;
            SetStatus("Refreshing voice list...");

            try
            {
                var voices = await _plugin.FetchAllTalkVoicesAsync();
                _voices = voices ?? new List<string>();
                SetStatus($"Voice list loaded ({_voices.Count}).");
            }
            catch (Exception ex)
            {
                SetStatus($"Voice refresh failed: {ex.Message}");
            }
            finally
            {
                _loadingVoices = false;
            }
        }

        private static List<string> FilterVoices(List<string> voices, string filter)
        {
            voices ??= new List<string>();
            var f = (filter ?? "").Trim();
            if (string.IsNullOrWhiteSpace(f))
                return voices;

            return voices.Where(v => v != null && v.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        private void EnsureDefaultBucketsExist()
        {
            _plugin.Configuration.VoiceBuckets ??= new List<VoiceBucket>();
            var need = new[] { "male", "female", "boy", "girl", "loporrit", "machine", "monster" };

            foreach (var n in need)
            {
                if (!_plugin.Configuration.VoiceBuckets.Any(b => string.Equals((b?.Name ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase)))
                    _plugin.Configuration.VoiceBuckets.Add(new VoiceBucket { Name = n, Voices = new List<string>() });
            }
        }

        private VoiceBucket? GetBucket(string name)
        {
            var n = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(n)) return null;

            _plugin.Configuration.VoiceBuckets ??= new List<VoiceBucket>();
            return _plugin.Configuration.VoiceBuckets.FirstOrDefault(b => string.Equals((b?.Name ?? "").Trim(), n, StringComparison.OrdinalIgnoreCase));
        }

        private List<string> GetBucketNames()
        {
            _plugin.Configuration.VoiceBuckets ??= new List<VoiceBucket>();
            var names = _plugin.Configuration.VoiceBuckets.Select(b => (b?.Name ?? "").Trim()).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        private void UpsertExactBucketOverride(string npcKey, string bucket)
        {
            _plugin.Configuration.NpcExactBucketOverrides ??= new List<NpcExactBucketOverride>();

            for (int i = 0; i < _plugin.Configuration.NpcExactBucketOverrides.Count; i++)
            {
                var r = _plugin.Configuration.NpcExactBucketOverrides[i];
                if (r == null) continue;

                if (string.Equals((r.NpcKey ?? "").Trim(), npcKey, StringComparison.OrdinalIgnoreCase))
                {
                    r.BucketName = bucket;
                    _plugin.Configuration.NpcExactBucketOverrides[i] = r;
                    return;
                }
            }

            _plugin.Configuration.NpcExactBucketOverrides.Add(new NpcExactBucketOverride { NpcKey = npcKey, BucketName = bucket });
        }

        private void UpsertExactVoiceOverride(string npcKey, string voice)
        {
            _plugin.Configuration.NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();

            for (int i = 0; i < _plugin.Configuration.NpcExactVoiceOverrides.Count; i++)
            {
                var r = _plugin.Configuration.NpcExactVoiceOverrides[i];
                if (r == null) continue;

                if (string.Equals((r.NpcKey ?? "").Trim(), npcKey, StringComparison.OrdinalIgnoreCase))
                {
                    r.Voice = voice;
                    _plugin.Configuration.NpcExactVoiceOverrides[i] = r;
                    return;
                }
            }

            _plugin.Configuration.NpcExactVoiceOverrides.Add(new NpcExactVoiceOverride { NpcKey = npcKey, Voice = voice });
        }

        private void SetStatus(string msg)
        {
            _status = msg;
            _statusTime = DateTime.UtcNow;
        }
    }
}
