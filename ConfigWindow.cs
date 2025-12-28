using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using ElevenLabs;
using ElevenLabs.Voices;
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
        private List<Voice> _availableVoices = new();

        private readonly string[] _categories = { "Male", "Female", "Male Child", "Female Child", "Machine", "Monster", "Loporrit" };
        private string _newNpcName = "";
        private int _selectedCategoryIndex = 0;

        private string _status = "";
        private DateTime _statusTime = DateTime.MinValue;

        public ConfigWindow(Plugin plugin) : base("NPC Voice Master Settings")
        {
            this.SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(500, 600) };
            this._plugin = plugin;
        }

        public override void Draw()
        {
            if (!string.IsNullOrWhiteSpace(_status) && (DateTime.UtcNow - _statusTime).TotalSeconds < 8)
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
                if (ImGui.BeginTabItem("Voice Pools"))
                {
                    DrawPoolsTab();
                    ImGui.EndTabItem();
                }
                if (ImGui.BeginTabItem("NPC Assignments"))
                {
                    DrawAssignmentsTab();
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
        }

        private void DrawGeneralTab()
        {
            var apiKey = _plugin.Configuration.ApiKey ?? string.Empty;

            if (ImGui.InputText("API Key", ref apiKey, 200, ImGuiInputTextFlags.Password))
            {
                _plugin.Configuration.ApiKey = apiKey;
                _plugin.Configuration.Save();
            }

            if (ImGui.Button("Apply API Key (Reload Voices)"))
            {
                _plugin.ApplyApiKey(_plugin.Configuration.ApiKey ?? string.Empty);
                SetStatus("Applied API key and triggered voice reload.");
            }

            ImGui.SameLine();
            if (ImGui.Button("Test Audio (/voicetest)"))
            {
                _plugin.RunVoiceTestFromUI();
            }

            ImGui.Separator();

            var enableCache = _plugin.Configuration.EnableCache;
            if (ImGui.Checkbox("Enable Local Audio Cache", ref enableCache))
            {
                _plugin.Configuration.EnableCache = enableCache;
                _plugin.Configuration.Save();
            }

            ImGui.Separator();

            // Cache folder safety
            var cacheFolder = _plugin.Configuration.CacheFolder ?? string.Empty;
            if (string.IsNullOrWhiteSpace(cacheFolder))
            {
                ImGui.TextWrapped("CacheFolder is empty in config. Set it in Configuration.cs.");
                return;
            }

            try
            {
                Directory.CreateDirectory(cacheFolder);
            }
            catch (Exception ex)
            {
                ImGui.TextWrapped($"Failed to create/access cache folder: {ex.Message}");
                return;
            }

            if (ImGui.Button("Clear All Cached Audio"))
            {
                try
                {
                    foreach (var file in Directory.GetFiles(cacheFolder))
                        File.Delete(file);

                    SetStatus("Cleared cached audio files.");
                }
                catch (Exception ex)
                {
                    SetStatus($"Failed to clear cache: {ex.Message}");
                }
            }

            int cacheCount = 0;
            try { cacheCount = Directory.GetFiles(cacheFolder).Length; } catch { }
            ImGui.Text($"Files in cache: {cacheCount}");
        }

        private void DrawPoolsTab()
        {
            if (ImGui.Button("Fetch Voices from ElevenLabs"))
                _ = RefreshVoices();

            ImGui.SameLine();
            if (ImGui.Button("Use Plugin Voice Cache"))
            {
                _availableVoices = _plugin.GetCachedVoicesForUI();
                SetStatus($"Loaded {_availableVoices.Count} cached voices from plugin.");
            }

            ImGui.Separator();

            DrawPoolSection("Male", _plugin.Configuration.MaleVoicePool);
            DrawPoolSection("Female", _plugin.Configuration.FemaleVoicePool);
            DrawPoolSection("Male Child", _plugin.Configuration.MaleChildVoicePool);
            DrawPoolSection("Female Child", _plugin.Configuration.FemaleChildVoicePool);
            DrawPoolSection("Machine", _plugin.Configuration.MachineVoicePool);
            DrawPoolSection("Monster", _plugin.Configuration.MonsterVoicePool);
            DrawPoolSection("Loporrit", _plugin.Configuration.LoporritVoicePool);
        }

        private void DrawAssignmentsTab()
        {
            ImGui.InputText("NPC Name", ref _newNpcName, 64);

            if (ImGui.BeginCombo("Category", _categories[_selectedCategoryIndex]))
            {
                for (int i = 0; i < _categories.Length; i++)
                    if (ImGui.Selectable(_categories[i], i == _selectedCategoryIndex))
                        _selectedCategoryIndex = i;

                ImGui.EndCombo();
            }

            if (ImGui.Button("Add/Update NPC"))
            {
                if (!string.IsNullOrWhiteSpace(_newNpcName))
                {
                    _plugin.Configuration.NpcCategoryMap[_newNpcName] = _categories[_selectedCategoryIndex];
                    _plugin.Configuration.Save();
                    SetStatus($"Assigned '{_newNpcName}' -> {_categories[_selectedCategoryIndex]}");
                    _newNpcName = "";
                }
            }

            ImGui.Separator();

            foreach (var entry in _plugin.Configuration.NpcCategoryMap.ToList())
            {
                ImGui.Text($"{entry.Key} -> {entry.Value}");
                ImGui.SameLine();
                if (ImGui.Button($"X##{entry.Key}"))
                {
                    _plugin.Configuration.NpcCategoryMap.Remove(entry.Key);
                    _plugin.Configuration.Save();
                    SetStatus($"Removed assignment for '{entry.Key}'.");
                }
            }
        }

        private void DrawPoolSection(string title, List<string> pool)
        {
            if (ImGui.CollapsingHeader($"{title} Pool"))
            {
                if (_availableVoices.Count == 0)
                {
                    ImGui.TextWrapped("No voices loaded into UI list. Click 'Fetch Voices' or 'Use Plugin Voice Cache'.");
                }

                if (ImGui.BeginCombo($"Add##{title}", "Add voice..."))
                {
                    foreach (var v in _availableVoices)
                    {
                        if (!pool.Contains(v.Id) && ImGui.Selectable(v.Name))
                        {
                            pool.Add(v.Id);
                            _plugin.Configuration.Save();
                            SetStatus($"Added voice '{v.Name}' to {title} pool.");
                        }
                    }
                    ImGui.EndCombo();
                }

                foreach (var id in pool.ToList())
                {
                    ImGui.BulletText(_availableVoices.FirstOrDefault(v => v.Id == id)?.Name ?? id);
                    ImGui.SameLine();
                    if (ImGui.Button($"Remove##{title}{id}"))
                    {
                        pool.Remove(id);
                        _plugin.Configuration.Save();
                        SetStatus($"Removed voice id '{id}' from {title} pool.");
                    }
                }
            }
        }

        private async Task RefreshVoices()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_plugin.Configuration.ApiKey))
                {
                    SetStatus("API key is empty. Enter it on the General tab and click Apply.");
                    return;
                }

                var api = new ElevenLabsClient(_plugin.Configuration.ApiKey);
                _availableVoices = (await api.VoicesEndpoint.GetAllVoicesAsync()).ToList();
                SetStatus($"Fetched {_availableVoices.Count} voices from ElevenLabs.");
            }
            catch (Exception ex)
            {
                SetStatus($"Fetch Voices error: {ex.Message}");
            }
        }

        private void SetStatus(string msg)
        {
            _status = msg;
            _statusTime = DateTime.UtcNow;
        }
    }
}
