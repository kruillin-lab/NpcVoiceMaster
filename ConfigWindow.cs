using Dalamud.Interface.Windowing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

// Force ImGui to be the Dalamud bindings version (no ambiguity)
using ImGui = Dalamud.Bindings.ImGui.ImGui;

namespace NPCVoiceMaster
{
    public class ConfigWindow : Window, IDisposable
    {
        private readonly Plugin _plugin;
        private string _status = "";

        // UI State
        private List<string> _voices = new();
        private bool _loadingVoices;
        private string _voiceFilter = "";
        private string _bucketToAddVoice = "male";
        private int _voiceToAddIndex = -1;
        private string _exactNpcKey = "";
        private int _exactVoiceIndex = -1;

        public ConfigWindow(Plugin plugin) : base("NPC Voice Master")
        {
            SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(650, 520) };
            _plugin = plugin;
            _ = RefreshVoicesAsync();
        }

        public void Dispose()
        {
        }

        public override void Draw()
        {
            if (!string.IsNullOrEmpty(_status))
            {
                ImGui.TextWrapped(_status);
                ImGui.Separator();
            }

            if (ImGui.BeginTabBar("Tabs"))
            {
                if (ImGui.BeginTabItem("General"))
                {
                    DrawGeneralTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Connection"))
                {
                    DrawConnectionTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Buckets"))
                {
                    DrawBucketsTab();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("Overrides"))
                {
                    DrawOverridesTab();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }

        private void DrawGeneralTab()
        {
            var enabled = _plugin.Configuration.Enabled;
            if (ImGui.Checkbox("Enable NPC Voice Master", ref enabled))
            {
                _plugin.Configuration.Enabled = enabled;
                _plugin.Configuration.Save();
            }

            ImGui.Separator();

            var buckets = _plugin.Configuration.VoiceBuckets;
            var defaultBucket = _plugin.Configuration.DefaultBucket;

            if (string.IsNullOrWhiteSpace(defaultBucket) && buckets.Count > 0)
                defaultBucket = buckets[0].Name;

            if (ImGui.BeginCombo("Default Bucket", defaultBucket))
            {
                foreach (var b in buckets)
                {
                    if (ImGui.Selectable(b.Name, b.Name == defaultBucket))
                    {
                        _plugin.Configuration.DefaultBucket = b.Name;
                        _plugin.Configuration.Save();
                    }
                }
                ImGui.EndCombo();
            }

            ImGui.Spacing();
            ImGui.TextDisabled($"Assigned NPC voices saved: {_plugin.Configuration.NpcAssignedVoices.Count}");
            if (ImGui.Button("Clear ALL assigned NPC voices (keeps Overrides)"))
            {
                _plugin.Configuration.NpcAssignedVoices.Clear();
                _plugin.Configuration.Save();
                _status = "Cleared assigned NPC voices.";
            }
        }

        private void DrawConnectionTab()
        {
            var url = _plugin.Configuration.AllTalkBaseUrl ?? "";
            if (ImGui.InputText("AllTalk URL", ref url, 200))
            {
                _plugin.Configuration.AllTalkBaseUrl = url;
                _plugin.Configuration.Save();
            }

            if (ImGui.Button("Refresh Voices")) _ = RefreshVoicesAsync();
            ImGui.SameLine();
            ImGui.TextDisabled(_loadingVoices ? "Loading..." : $"Loaded: {_voices.Count}");
        }

        private void DrawBucketsTab()
        {
            var buckets = _plugin.Configuration.VoiceBuckets;

            if (ImGui.BeginCombo("Target Bucket", _bucketToAddVoice))
            {
                foreach (var b in buckets)
                {
                    if (ImGui.Selectable(b.Name, b.Name == _bucketToAddVoice))
                        _bucketToAddVoice = b.Name;
                }
                ImGui.EndCombo();
            }

            ImGui.InputText("Filter Voices", ref _voiceFilter, 100);
            var filtered = _voices.Where(v => v.Contains(_voiceFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            var preview = (filtered.Count > 0 && _voiceToAddIndex >= 0 && _voiceToAddIndex < filtered.Count)
                ? filtered[_voiceToAddIndex]
                : "Select Voice...";

            if (ImGui.BeginCombo("##voiceSelector", preview))
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    if (ImGui.Selectable(filtered[i], i == _voiceToAddIndex))
                        _voiceToAddIndex = i;
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("Add Voice to Bucket"))
            {
                var b = buckets.FirstOrDefault(x => x.Name == _bucketToAddVoice);
                if (b != null && _voiceToAddIndex >= 0 && _voiceToAddIndex < filtered.Count)
                {
                    var v = filtered[_voiceToAddIndex];
                    if (!b.Voices.Contains(v))
                    {
                        b.Voices.Add(v);
                        _plugin.Configuration.Save();
                        _status = $"Added {v} to {_bucketToAddVoice}";
                    }
                }
            }

            ImGui.Separator();

            foreach (var b in buckets)
            {
                if (ImGui.CollapsingHeader($"{b.Name} ({b.Voices.Count} voices)"))
                {
                    if (ImGui.Button($"Clear##{b.Name}"))
                    {
                        b.Voices.Clear();
                        _plugin.Configuration.Save();
                    }

                    foreach (var v in b.Voices)
                        ImGui.BulletText(v);
                }
            }
        }

        private void DrawOverridesTab()
        {
            ImGui.Text("Force a specific voice for an NPC:");
            ImGui.InputText("NPC Name (Exact)", ref _exactNpcKey, 100);

            ImGui.InputText("Filter Voices", ref _voiceFilter, 100);
            var filtered = _voices.Where(v => v.Contains(_voiceFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            var preview = (filtered.Count > 0 && _exactVoiceIndex >= 0 && _exactVoiceIndex < filtered.Count)
                ? filtered[_exactVoiceIndex]
                : "Select Voice...";

            if (ImGui.BeginCombo("##overrideSelector", preview))
            {
                for (int i = 0; i < filtered.Count; i++)
                {
                    if (ImGui.Selectable(filtered[i], i == _exactVoiceIndex))
                        _exactVoiceIndex = i;
                }
                ImGui.EndCombo();
            }

            if (ImGui.Button("Save Override"))
            {
                if (string.IsNullOrWhiteSpace(_exactNpcKey) || _exactVoiceIndex < 0) return;

                var list = _plugin.Configuration.NpcExactVoiceOverrides;
                var exist = list.FirstOrDefault(x => x.NpcKey.Equals(_exactNpcKey, StringComparison.OrdinalIgnoreCase));
                var v = filtered[_exactVoiceIndex];

                if (exist != null) exist.Voice = v;
                else list.Add(new NpcExactVoiceOverride { NpcKey = _exactNpcKey, Voice = v });

                _plugin.Configuration.Save();
                _status = $"Override Saved: {_exactNpcKey} -> {v}";
            }

            ImGui.Separator();
            ImGui.TextDisabled($"Overrides: {_plugin.Configuration.NpcExactVoiceOverrides.Count}");
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
            finally
            {
                _loadingVoices = false;
            }
        }
    }
}
