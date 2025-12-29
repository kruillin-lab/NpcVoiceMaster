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

        private string _allTalkUrlEdit = "";
        private string _cacheFolderEdit = "";
        private string _status = "";

        private bool _isFetching = false;
        private List<string> _fetchedVoices = new();

        private string _newBucketName = "";
        private readonly Dictionary<string, string> _addVoiceBuffers = new(StringComparer.OrdinalIgnoreCase);

        private string _newNpcVoiceName = "";
        private string _newNpcVoiceValue = "";

        private string _newNpcBucketName = "";
        private int _newNpcBucketIdx = 0;

        public ConfigWindow(Plugin plugin)
            : base("NPC Voice Master")
        {
            _plugin = plugin;

            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(820, 580),
                MaximumSize = new Vector2(2400, 2400)
            };

            RefreshEditsFromConfig();
        }

        public void Dispose() { }

        private void RefreshEditsFromConfig()
        {
            _enabledEdit = _plugin.Configuration.Enabled;
            _debugOverlayEdit = _plugin.Configuration.DebugOverlayEnabled;

            _allTalkUrlEdit = _plugin.Configuration.AllTalkBaseUrl ?? "";
            _cacheFolderEdit = _plugin.Configuration.CacheFolderOverride ?? "";

            _plugin.Configuration.VoiceBuckets ??= new List<VoiceBucket>();
            _plugin.Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _plugin.Configuration.NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();
            _plugin.Configuration.NpcBucketOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public override void Draw()
        {
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

                if (ImGui.BeginTabItem("Buckets"))
                {
                    DrawTab_Buckets();
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem("NPC Overrides"))
                {
                    DrawTab_NpcOverrides();
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

            ImGui.Spacing();
            ImGui.TextUnformatted("Cache Folder (resolved):");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.9f, 0.7f, 1f), _plugin.ResolvedCacheFolder);

            if (ImGui.Button("Open cache folder"))
            {
                try
                {
                    _plugin.EnsureCacheRootExists();
                    Process.Start("explorer.exe", _plugin.ResolvedCacheFolder);
                    _status = "Opened cache folder.";
                }
                catch (Exception ex)
                {
                    _status = $"Failed to open folder: {ex.Message}";
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Clear ALL sticky NPC assigned voices"))
            {
                _plugin.Configuration.NpcAssignedVoices.Clear();
                _plugin.Configuration.Save();
                _status = "Cleared sticky NPC assigned voices.";
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextUnformatted("Priority order:");
            ImGui.BulletText("Exact NPC Voice Override");
            ImGui.BulletText("Sticky NPC assigned voice (random pick saved)");
            ImGui.BulletText("NPC Bucket Override (bucket choice)");
            ImGui.BulletText("Default Bucket");
        }

        private void DrawTab_AllTalk()
        {
            ImGui.TextUnformatted("AllTalk Base URL");
            ImGui.SetNextItemWidth(-1);

            if (ImGui.InputText("##alltalkurl", ref _allTalkUrlEdit, 512))
            {
                _plugin.Configuration.AllTalkBaseUrl = _allTalkUrlEdit;
                _plugin.Configuration.Save();
                _status = "Saved AllTalk URL.";
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (!_isFetching)
            {
                if (ImGui.Button("Fetch voices"))
                    _ = FetchVoicesAsync();
            }
            else
            {
                ImGui.TextUnformatted("Fetching voices...");
            }

            ImGui.SameLine();
            if (ImGui.Button("Auto-bucket fetched voices (clear first)"))
            {
                var count = _plugin.AutoBucketVoicesFromNames(_fetchedVoices, clearBucketsFirst: true);
                _status = $"Auto-bucketed {count} voices.";
            }

            ImGui.SameLine();
            if (ImGui.Button("Auto-bucket fetched voices (merge)"))
            {
                var count = _plugin.AutoBucketVoicesFromNames(_fetchedVoices, clearBucketsFirst: false);
                _status = $"Added {count} voices.";
            }

            ImGui.Spacing();
            ImGui.TextUnformatted($"Fetched voices: {_fetchedVoices.Count}");

            if (_fetchedVoices.Count > 0)
            {
                ImGui.BeginChild("##fetchedvoices", new Vector2(0, 260), true);
                foreach (var v in _fetchedVoices)
                    ImGui.BulletText(v);
                ImGui.EndChild();
            }
        }

        private void DrawTab_Cache()
        {
            ImGui.TextUnformatted("Cache Folder Override (optional)");
            ImGui.TextDisabled("Leave blank to use default plugin cache folder.");
            ImGui.SetNextItemWidth(-1);

            ImGui.InputText("##cacheoverride", ref _cacheFolderEdit, 512);

            if (ImGui.Button("Save cache override"))
            {
                _plugin.Configuration.CacheFolderOverride = (_cacheFolderEdit ?? "").Trim();
                _plugin.Configuration.Save();
                _status = "Saved cache override.";
            }

            ImGui.SameLine();
            if (ImGui.Button("Use default"))
            {
                _cacheFolderEdit = "";
                _plugin.Configuration.CacheFolderOverride = "";
                _plugin.Configuration.Save();
                _status = "Reverted to default cache folder.";
            }

            ImGui.Spacing();
            ImGui.TextUnformatted("Resolved Cache Folder:");
            ImGui.SameLine();
            ImGui.TextColored(new Vector4(0.7f, 0.9f, 0.7f, 1f), _plugin.ResolvedCacheFolder);
        }

        private void DrawTab_Buckets()
        {
            EnsureDefaultBuckets();

            var bucketNames = _plugin.Configuration.VoiceBuckets.Select(b => b.Name).ToList();
            var current = _plugin.Configuration.DefaultBucket ?? "male";
            var idx = Math.Max(0, bucketNames.FindIndex(x => string.Equals(x, current, StringComparison.OrdinalIgnoreCase)));

            ImGui.TextUnformatted("Default Bucket");
            if (ImGui.Combo("##defaultbucket", ref idx, bucketNames.ToArray(), bucketNames.Count))
            {
                _plugin.Configuration.DefaultBucket = bucketNames[idx];
                _plugin.Configuration.Save();
                _status = "Saved default bucket.";
            }

            ImGui.Separator();

            ImGui.TextUnformatted("Add Custom Bucket");
            ImGui.SetNextItemWidth(220);
            ImGui.InputText("##newbucket", ref _newBucketName, 64);
            ImGui.SameLine();

            if (ImGui.Button("Add bucket"))
            {
                var name = (_newBucketName ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(name) &&
                    !_plugin.Configuration.VoiceBuckets.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    _plugin.Configuration.VoiceBuckets.Add(new VoiceBucket { Name = name, Voices = new List<string>() });
                    _plugin.Configuration.Save();
                    _newBucketName = "";
                    _status = "Added bucket.";
                }
                else
                {
                    _status = "Bucket name invalid or already exists.";
                }
            }

            ImGui.Spacing();

            foreach (var bucket in _plugin.Configuration.VoiceBuckets.ToList())
            {
                var header = $"{bucket.Name} ({bucket.Voices.Count})";
                if (ImGui.CollapsingHeader(header))
                {
                    ImGui.PushID(bucket.Name);

                    if (!_addVoiceBuffers.TryGetValue(bucket.Name, out var buf))
                        buf = "";

                    ImGui.SetNextItemWidth(340);
                    ImGui.InputText("Add voice##addvoice", ref buf, 256);
                    _addVoiceBuffers[bucket.Name] = buf;

                    ImGui.SameLine();
                    if (ImGui.Button("Add##btn"))
                    {
                        var v = (buf ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(v) && !bucket.Voices.Contains(v, StringComparer.OrdinalIgnoreCase))
                        {
                            bucket.Voices.Add(v);
                            bucket.Voices = bucket.Voices
                                .Where(x => !string.IsNullOrWhiteSpace(x))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                                .ToList();

                            _plugin.Configuration.Save();
                            _addVoiceBuffers[bucket.Name] = "";
                            _status = $"Added voice to {bucket.Name}.";
                        }
                    }

                    ImGui.SameLine();
                    if (ImGui.Button("Clear bucket"))
                    {
                        bucket.Voices.Clear();
                        _plugin.Configuration.Save();
                        _status = $"Cleared {bucket.Name}.";
                    }

                    ImGui.Spacing();

                    if (bucket.Voices.Count == 0)
                    {
                        ImGui.TextDisabled("No voices assigned.");
                    }
                    else
                    {
                        ImGui.BeginChild("##voicelist", new Vector2(0, 240), true);
                        for (int i = 0; i < bucket.Voices.Count; i++)
                        {
                            var v = bucket.Voices[i];
                            ImGui.TextUnformatted(v);

                            ImGui.SameLine();
                            ImGui.SetCursorPosX(ImGui.GetWindowWidth() - 70);

                            if (ImGui.SmallButton($"Remove##{i}"))
                            {
                                bucket.Voices.RemoveAt(i);
                                _plugin.Configuration.Save();
                                _status = $"Removed voice from {bucket.Name}.";
                                i--;
                            }
                        }
                        ImGui.EndChild();
                    }

                    ImGui.PopID();
                }
            }
        }

        private void DrawTab_NpcOverrides()
        {
            EnsureDefaultBuckets();

            ImGui.TextUnformatted("Exact Voice Overrides (NPC -> Voice)");
            ImGui.TextDisabled("This beats buckets and random assignment.");

            ImGui.SetNextItemWidth(220);
            ImGui.InputText("NPC Name##newNpcVoiceName", ref _newNpcVoiceName, 128);
            ImGui.SameLine();

            ImGui.SetNextItemWidth(280);
            ImGui.InputText("Voice##newNpcVoiceValue", ref _newNpcVoiceValue, 256);
            ImGui.SameLine();

            if (ImGui.Button("Add/Update Voice Override"))
            {
                var npc = (_newNpcVoiceName ?? "").Trim();
                var voice = (_newNpcVoiceValue ?? "").Trim();

                if (string.IsNullOrWhiteSpace(npc) || string.IsNullOrWhiteSpace(voice))
                {
                    _status = "NPC and Voice are required.";
                }
                else
                {
                    var existing = _plugin.Configuration.NpcExactVoiceOverrides
                        .FirstOrDefault(x => string.Equals(x.NpcKey, npc, StringComparison.OrdinalIgnoreCase));

                    if (existing == null)
                    {
                        _plugin.Configuration.NpcExactVoiceOverrides.Add(new NpcExactVoiceOverride
                        {
                            Enabled = true,
                            NpcKey = npc,
                            Voice = voice
                        });
                    }
                    else
                    {
                        existing.Enabled = true;
                        existing.Voice = voice;
                    }

                    _plugin.Configuration.Save();
                    _status = $"Saved voice override for {npc}.";
                    _newNpcVoiceName = "";
                    _newNpcVoiceValue = "";
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextUnformatted("Bucket Overrides (NPC -> Bucket)");
            ImGui.TextDisabled("Used only when there is no exact voice override.");

            var bucketNames = _plugin.Configuration.VoiceBuckets.Select(b => b.Name).ToList();
            if (_newNpcBucketIdx < 0) _newNpcBucketIdx = 0;
            if (_newNpcBucketIdx >= bucketNames.Count) _newNpcBucketIdx = 0;

            ImGui.SetNextItemWidth(220);
            ImGui.InputText("NPC Name##newNpcBucketName", ref _newNpcBucketName, 128);
            ImGui.SameLine();

            ImGui.SetNextItemWidth(200);
            ImGui.Combo("Bucket##newNpcBucketValue", ref _newNpcBucketIdx, bucketNames.ToArray(), bucketNames.Count);
            ImGui.SameLine();

            if (ImGui.Button("Add/Update Bucket Override"))
            {
                var npc = (_newNpcBucketName ?? "").Trim();
                var bucket = bucketNames.Count > 0 ? bucketNames[_newNpcBucketIdx] : "";

                if (string.IsNullOrWhiteSpace(npc) || string.IsNullOrWhiteSpace(bucket))
                {
                    _status = "NPC and Bucket are required.";
                }
                else
                {
                    _plugin.Configuration.NpcBucketOverrides[npc] = bucket;
                    _plugin.Configuration.Save();
                    _status = $"Saved bucket override for {npc} -> {bucket}.";
                    _newNpcBucketName = "";
                }
            }
        }

        private async System.Threading.Tasks.Task FetchVoicesAsync()
        {
            _isFetching = true;
            _status = "";

            try
            {
                var voices = await _plugin.FetchAllTalkVoicesAsync();
                _fetchedVoices = voices ?? new List<string>();
                _status = _fetchedVoices.Count == 0 ? "No voices returned." : $"Fetched {_fetchedVoices.Count} voices.";
            }
            catch (Exception ex)
            {
                _status = $"Fetch failed: {ex.Message}";
            }
            finally
            {
                _isFetching = false;
            }
        }

        private void EnsureDefaultBuckets()
        {
            _plugin.Configuration.VoiceBuckets ??= new List<VoiceBucket>();

            void ensure(string name)
            {
                if (!_plugin.Configuration.VoiceBuckets.Any(b => string.Equals(b.Name, name, StringComparison.OrdinalIgnoreCase)))
                    _plugin.Configuration.VoiceBuckets.Add(new VoiceBucket { Name = name, Voices = new List<string>() });
            }

            ensure("male");
            ensure("woman");
            ensure("boy");
            ensure("girl");
            ensure("loporrit");
            ensure("machine");
            ensure("monsters");

            if (string.IsNullOrWhiteSpace(_plugin.Configuration.DefaultBucket))
                _plugin.Configuration.DefaultBucket = "male";

            _plugin.Configuration.NpcBucketOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _plugin.Configuration.NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();
        }
    }
}
