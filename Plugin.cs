using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

using FFXIVClientStructs.FFXIV.Component.GUI;

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NPCVoiceMaster
{
    public sealed class Plugin : IDalamudPlugin
    {
        public string Name => "NPC Voice Master";

        private const string CmdOpen1 = "/npcvoice";
        private const string CmdOpen2 = "/nvm";

        private const string CmdMale = "/male";
        private const string CmdLady = "/lady";
        private const string CmdWay = "/way";
        private const string CmdBot = "/bot";
        private const string CmdMon = "/mon";

                private const string CmdTag = "/tag";
private readonly IDalamudPluginInterface _pi;
        private readonly ICommandManager _commands;

        public Configuration Configuration { get; private set; }

        public WindowSystem WindowSystem { get; }
        public ConfigWindow ConfigWindow { get; }
        public DebugOverlayWindow DebugOverlayWindow { get; }

        private readonly HttpClient _http = new();
        private readonly VoicePlayer _player = new();
        private readonly Random _rng = new();

        private readonly SemaphoreSlim _speakQueue = new(1, 1);

        // Removed unused _lastTalkKeyInternal field. Originally used to suppress repeated identical lines,
        // the plugin now allows the same line to be spoken consecutively. Keeping this field unused
        // triggers CS0414 warnings, so it has been removed.

        /// <summary>
        /// Folder name used to store cached audio generated for player-controlled dialogue. Files in this
        /// folder are subject to periodic cleanup (e.g., daily) to avoid unbounded growth. NPC dialogue
        /// caches are stored under their NPC name and are not removed automatically.
        /// </summary>
        private const string PlayerCacheFolder = "__player";

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex TokenSplit = new Regex(@"[^a-zA-Z0-9]+", RegexOptions.Compiled);

        // Debug overlay fields (public getters)
        public string LastTalkNpc { get; private set; } = "";
        public string LastTalkLine { get; private set; } = "";
        public string LastTalkKey { get; private set; } = "";
        public DateTime LastTalkAt { get; private set; } = DateTime.MinValue;

        // New NPC suggestion popup state
        private bool _newNpcPopupOpen = false;
        private string _newNpcPopupName = "";
        private NpcProfile? _newNpcPopupProfile = null;
        private bool _tagPopupManual = false;
        private string _newNpcPopupVoiceChoice = "";
        private bool _newNpcPopupVoiceChoiceInit = false;

// Voice audition preview (stop previous preview on new preview)
private readonly object _previewSync = new object();
private System.Threading.CancellationTokenSource? _previewCts = null;
private System.Threading.Tasks.Task? _previewTask = null;

        public string LastDetectedGender { get; private set; } = "unknown";
        public string LastResolvedBucket { get; private set; } = "";
        public string LastResolvedVoice { get; private set; } = "";
        public string LastResolvePath { get; private set; } = "";

        // Cache path exposure
        public string ResolvedCacheFolder => GetCacheRoot();

        public Plugin(IDalamudPluginInterface pluginInterface, ICommandManager commandManager)
        {
            _pi = pluginInterface;
            _commands = commandManager;

            _pi.Create<Svc>();

            Configuration = LoadConfigurationSafe();

            WindowSystem = new WindowSystem("NpcVoiceMaster");
            ConfigWindow = new ConfigWindow(this);
            DebugOverlayWindow = new DebugOverlayWindow(this);

            WindowSystem.AddWindow(ConfigWindow);
            WindowSystem.AddWindow(DebugOverlayWindow);

            DebugOverlayWindow.IsOpen = Configuration.DebugOverlayEnabled;

            _pi.UiBuilder.Draw += DrawUI;
            _pi.UiBuilder.OpenConfigUi += DrawConfigUI;

            _commands.AddHandler(CmdOpen1, new CommandInfo(OnOpenCommand) { HelpMessage = "Open NPC Voice Master config." });
            _commands.AddHandler(CmdOpen2, new CommandInfo(OnOpenCommand) { HelpMessage = "Open NPC Voice Master config." });

            _commands.AddHandler(CmdMale, new CommandInfo((c, a) => SetBucketForCurrentTarget("male"))
            {
                HelpMessage = "Add required voice-tag for current target NPC: male"
            });

            _commands.AddHandler(CmdLady, new CommandInfo((c, a) => SetBucketForCurrentTarget("woman"))
            {
                HelpMessage = "Add required voice-tag for current target NPC: woman"
            });

            _commands.AddHandler(CmdWay, new CommandInfo((c, a) => SetBucketForCurrentTarget("loporrit"))
            {
                HelpMessage = "Add required voice-tag for current target NPC: loporrit"
            });

            _commands.AddHandler(CmdBot, new CommandInfo((c, a) => SetBucketForCurrentTarget("machine"))
            {
                HelpMessage = "Add required voice-tag for current target NPC: machine"
            });

            _commands.AddHandler(CmdMon, new CommandInfo((c, a) => SetBucketForCurrentTarget("monsters"))
            {
                HelpMessage = "Add required voice-tag for current target NPC: monsters"
            });

                        _commands.AddHandler(CmdTag, new CommandInfo(OnTagCommand)
            {
                HelpMessage = "Open tag editor popup for your current target (suggested tags + current voice)."
            });

Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "Talk", OnTalkPostDraw);

            try { Svc.Chat.Print($"[NpcVoiceMaster] Loaded. Cache: {ResolvedCacheFolder}"); } catch { }

            // On startup, prune any cached player dialogue older than one day. This prevents stale
            // player-generated audio from accumulating over time. NPC dialogue caches remain untouched.
            try
            {
                CleanOldPlayerCache(TimeSpan.FromDays(1));
            }
            catch
            {
                // Swallow cleanup failures; they should not block plugin initialization.
            }
        }

        public void Dispose()
        {
            try { Svc.AddonLifecycle.UnregisterListener(AddonEvent.PostDraw, "Talk", OnTalkPostDraw); } catch { }

            _commands.RemoveHandler(CmdOpen1);
            _commands.RemoveHandler(CmdOpen2);

            _commands.RemoveHandler(CmdMale);
            _commands.RemoveHandler(CmdLady);
            _commands.RemoveHandler(CmdWay);
            _commands.RemoveHandler(CmdBot);
            _commands.RemoveHandler(CmdMon);

            _pi.UiBuilder.Draw -= DrawUI;
            _pi.UiBuilder.OpenConfigUi -= DrawConfigUI;

            WindowSystem.RemoveAllWindows();

            _player.Dispose();
            _http.Dispose();
            _speakQueue.Dispose();
        }

        private void OnOpenCommand(string command, string args)
        {
            ConfigWindow.IsOpen = !ConfigWindow.IsOpen;
        }

        private void OnTagCommand(string command, string args)
        {
            try
            {
                var npcName = GetCurrentTargetNpcName();
                if (string.IsNullOrWhiteSpace(npcName))
                {
                    try { Svc.Chat.Print("[NpcVoiceMaster] /tag: No target selected."); } catch { }
                    return;
                }

                npcName = npcName.Trim();

                Configuration.NpcProfiles ??= new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase);
                Configuration.KnownNpcTags ??= new List<string>();

                // Work on a copy so Cancel doesn't mutate live config.
                NpcProfile working;
                if (Configuration.NpcProfiles.TryGetValue(npcName, out var existing) && existing != null)
                {
                    working = CloneNpcProfile(existing);
                }
                else
                {
                    working = new NpcProfile();
                    working.RequiredVoiceTags ??= new List<string>();
                    working.PreferredVoiceTags ??= new List<string>();
                    working.NpcTags ??= new List<string>();

                    // Suggest tags from name/title
                    var suggested = AutoTagger.SuggestNpcVoiceTagsFromName(npcName);

                    // Core/bucket-ish tags go in required; identity-ish tags go in preferred.
                    var core = new HashSet<string>(new[]
                    {
                        "male","woman","boy","girl","loporrit","machine","monsters","default",
                        "big monster","little monster"
                    }, StringComparer.OrdinalIgnoreCase);

                    foreach (var t in suggested)
                    {
                        if (core.Contains(t))
                            TagUtil.AddTag(working.RequiredVoiceTags, t);
                        else
                            TagUtil.AddTag(working.PreferredVoiceTags, t);
                    }

                    working.RequiredVoiceTags = TagUtil.NormDistinct(working.RequiredVoiceTags);
                    working.PreferredVoiceTags = TagUtil.NormDistinct(working.PreferredVoiceTags);
                }

                _newNpcPopupName = npcName;
                _newNpcPopupProfile = working;
                _newNpcPopupOpen = true;
                _tagPopupManual = true;
                _newNpcPopupVoiceChoice = "";
                _newNpcPopupVoiceChoiceInit = false;
            }
            catch
            {
                try { Svc.Chat.Print("[NpcVoiceMaster] /tag: failed to open tag editor."); } catch { }
            }
        }

        private static NpcProfile CloneNpcProfile(NpcProfile src)
        {
            var dst = new NpcProfile();
            dst.RequiredVoiceTags = src.RequiredVoiceTags != null ? new List<string>(src.RequiredVoiceTags) : new List<string>();
            dst.PreferredVoiceTags = src.PreferredVoiceTags != null ? new List<string>(src.PreferredVoiceTags) : new List<string>();
            dst.NpcTags = src.NpcTags != null ? new List<string>(src.NpcTags) : new List<string>();
            dst.Tone = src.Tone;
            dst.Accent = src.Accent;
            return dst;
        }

        private string GetCurrentVoicePreview(string npcName)
        {
            npcName = (npcName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(npcName))
                return "(none)";

            var ov = Configuration.NpcExactVoiceOverrides?
                .FirstOrDefault(x => x != null && x.Enabled && x.NpcKey.Equals(npcName, StringComparison.OrdinalIgnoreCase));

            if (ov != null && !string.IsNullOrWhiteSpace(ov.Voice))
                return $"override: {ov.Voice.Trim()}";

            if (Configuration.NpcAssignedVoices != null && Configuration.NpcAssignedVoices.TryGetValue(npcName, out var av) && !string.IsNullOrWhiteSpace(av))
                return $"assigned: {av.Trim()}";

            if (!string.IsNullOrWhiteSpace(LastResolvedVoice))
                return $"resolved: {LastResolvedVoice}";

            return "(unknown)";
        }

public void PreviewVoice(string voiceName, string? testLine)
{
    voiceName = NormalizeVoiceName((voiceName ?? "").Trim());
    if (string.IsNullOrWhiteSpace(voiceName))
        return;

    var line = (testLine ?? "").Trim();
    if (string.IsNullOrWhiteSpace(line))
        line = "Thirty-three thieves thought they thrilled the throne throughout Thursday.";

    // Keep previews out of normal NPC caching buckets
    _ = PreviewVoiceLineQueuedAsync(voiceName, line);
}


public void StopPreviewVoice()
{
    System.Threading.CancellationTokenSource? cts;
    lock (_previewSync)
    {
        cts = _previewCts;
    }

    try
    {
        cts?.Cancel();
    }
    catch
    {
        // ignore
    }
}

private async Task PreviewVoiceLineQueuedAsync(string voiceName, string lineText)
{
    // Cancel any in-flight/playing preview so the new preview starts ASAP.
    System.Threading.CancellationTokenSource? oldCts = null;
    System.Threading.CancellationTokenSource newCts;
    lock (_previewSync)
    {
        oldCts = _previewCts;
        newCts = new System.Threading.CancellationTokenSource();
        _previewCts = newCts;
    }

    try
    {
        oldCts?.Cancel();
    }
    catch { /* ignore */ }
    finally
    {
        try { oldCts?.Dispose(); } catch { /* ignore */ }
    }

    var token = newCts.Token;

    // Serialize TTS generation with the rest of the plugin, but do NOT block on playback.
    try
    {
        await _speakQueue.WaitAsync(token).ConfigureAwait(false);

        if (token.IsCancellationRequested)
        {
            _speakQueue.Release();
            return;
        }

        // For preview lines, allow caching but tag them as player dialogue. Caches in the player folder are
        // automatically cleaned up after a retention period to prevent unbounded growth.
        var audio = await GetOrCreateCachedTtsAsync("__VoiceTest__", lineText, voiceName, useCache: true, isPlayerCache: true).ConfigureAwait(false);

        // Release the semaphore immediately so subsequent lines can queue while the audio plays.
        _speakQueue.Release();

        if (audio == null || audio.Length == 0 || token.IsCancellationRequested)
            return;

        var playTask = _player.PlayAudioAsync(audio, token);
        lock (_previewSync)
        {
            _previewTask = playTask;
        }

        // Fire-and-forget: swallow preview playback failures (don't crash plugin).
        _ = playTask.ContinueWith(t =>
        {
            try
            {
                if (t.Exception != null)
                    Svc.Log.Debug($"[NpcVoiceMaster] Preview playback ended with error: {t.Exception.GetBaseException().Message}");
            }
            catch { }
        }, System.Threading.Tasks.TaskScheduler.Default);
    }
    catch (OperationCanceledException)
    {
        // Ignore cancellations
    }
    catch (Exception ex)
    {
        // Make sure to release the semaphore if an exception occurs after acquisition.
        try { _speakQueue.Release(); } catch { }
        Svc.Log.Error(ex, "[NpcVoiceMaster] PreviewVoiceLineQueuedAsync failed.");
    }
}





        private void DrawUI()
        {
            WindowSystem.Draw();
            DrawNewNpcPopup();
        }
        private void DrawConfigUI() => ConfigWindow.IsOpen = true;

        private void MaybeQueueNewNpcPopup(string npcName)
        {
            if (!Configuration.EnableNewNpcPopup)
                return;

            npcName = (npcName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(npcName))
                return;

            // Don't spam while a popup is already pending
            if (_newNpcPopupOpen)
                return;

            // Already has a profile? Don't prompt.
            if (Configuration.NpcProfiles.ContainsKey(npcName))
                return;

            // Ignored? Don't prompt.
            if (Configuration.IgnoredNpcPopup != null && Configuration.IgnoredNpcPopup.Any(x => string.Equals(x, npcName, StringComparison.OrdinalIgnoreCase)))
                return;

            // Create profile now so the UI edits a stable object
            var profile = new NpcProfile();
            profile.RequiredVoiceTags ??= new List<string>();
            profile.PreferredVoiceTags ??= new List<string>();
            profile.NpcTags ??= new List<string>();

            // Suggest tags from name/title
            var suggested = AutoTagger.SuggestNpcVoiceTagsFromName(npcName);

            // Put “identity” style tags in preferred unless they're one of the core bucket tags
            var core = new HashSet<string>(new[] { "male","woman","boy","girl","loporrit","machine","monsters","default","big monster","little monster" }, StringComparer.OrdinalIgnoreCase);
            foreach (var t in suggested)
            {
                if (core.Contains(t))
                    TagUtil.AddTag(profile.RequiredVoiceTags, t);
                else
                    TagUtil.AddTag(profile.PreferredVoiceTags, t);
            }

            profile.RequiredVoiceTags = TagUtil.NormDistinct(profile.RequiredVoiceTags);
            profile.PreferredVoiceTags = TagUtil.NormDistinct(profile.PreferredVoiceTags);

            Configuration.NpcProfiles[npcName] = profile;
            Configuration.Save();

            _newNpcPopupName = npcName;
            _newNpcPopupProfile = profile;
            _tagPopupManual = false;
            _newNpcPopupOpen = true;
            _newNpcPopupVoiceChoice = "";
            _newNpcPopupVoiceChoiceInit = false;
        }

        private void DrawNewNpcPopup()
        {
            if (!_newNpcPopupOpen || _newNpcPopupProfile == null)
                return;

            // Minimal UI: we keep it simple and editable.
            ImGui.SetNextWindowSizeConstraints(new Vector2(480, 0), new Vector2(720, 520));
            var popupTitle = _tagPopupManual ? "Tag target (suggested)" : "New NPC detected";
            ImGui.OpenPopup(popupTitle);

            bool open = true;
            if (ImGui.BeginPopupModal(popupTitle, ref open, ImGuiWindowFlags.AlwaysAutoResize))
            {
                ImGui.TextUnformatted($"NPC: {_newNpcPopupName}");
                ImGui.TextUnformatted($"Current voice (preview): {GetCurrentVoicePreview(_newNpcPopupName)}");
                ImGui.Separator();

                ImGui.TextUnformatted(_tagPopupManual ? "Edit tags for this target, then save." : "Suggested tags are pre-filled. Edit if needed, then choose:");
                ImGui.Spacing();

                var allNpcTags = Configuration.KnownNpcTags ?? new List<string>();
                var profile = _newNpcPopupProfile;

                profile.RequiredVoiceTags ??= new List<string>();
                profile.PreferredVoiceTags ??= new List<string>();

                DrawTagMultiSelectPopup("Required voice-tags", "npc_popup_req", profile.RequiredVoiceTags, allNpcTags);
                DrawTagMultiSelectPopup("Preferred voice-tags", "npc_popup_pref", profile.PreferredVoiceTags, allNpcTags);

                ImGui.Separator();

                // Accent/Tone dropdowns using known lists
                var accents = (Configuration.KnownAccents ?? new List<string>());
                var tones = (Configuration.KnownTones ?? new List<string>());

                var acc = profile.Accent ?? "";
                if (DrawStringComboPopup("Accent", "npc_popup_acc", ref acc, accents, 260f))
                    profile.Accent = string.IsNullOrWhiteSpace(acc) ? null : acc;

                var tone = profile.Tone ?? "";
                if (DrawStringComboPopup("Tone", "npc_popup_tone", ref tone, tones, 260f))
                    profile.Tone = string.IsNullOrWhiteSpace(tone) ? null : tone;


// Voice override (optional): filtered list based on the tags above.
var voiceCandidates = GetCandidateVoicesForProfile(profile);

if (!_newNpcPopupVoiceChoiceInit)
{
    if (TryGetExactVoiceOverride(_newNpcPopupName, out var existingVoice) && !string.IsNullOrWhiteSpace(existingVoice))
        _newNpcPopupVoiceChoice = existingVoice;
    else
        _newNpcPopupVoiceChoice = "";

    _newNpcPopupVoiceChoiceInit = true;
}

DrawVoiceComboPopup("Voice override (matches tags)", "npc_popup_voice", ref _newNpcPopupVoiceChoice, voiceCandidates, 420f);

if (voiceCandidates.Count > 0)
    ImGui.TextDisabled($"Matching voices: {voiceCandidates.Count}");
else
    ImGui.TextDisabled("No enabled voices found.");

                ImGui.Spacing();
                ImGui.Separator();

                if (ImGui.Button("Yes (save)"))
                {
                    profile.RequiredVoiceTags = TagUtil.NormDistinct(profile.RequiredVoiceTags);
                    profile.PreferredVoiceTags = TagUtil.NormDistinct(profile.PreferredVoiceTags);

                    // Commit profile (manual /tag uses a working copy)
                    Configuration.NpcProfiles ??= new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase);
                    Configuration.NpcProfiles[_newNpcPopupName] = profile;

                    // Learn vocab
                    Configuration.KnownNpcTags ??= new List<string>();
                    foreach (var t in profile.RequiredVoiceTags) TagUtil.AddTag(Configuration.KnownNpcTags, t);
                    foreach (var t in profile.PreferredVoiceTags) TagUtil.AddTag(Configuration.KnownNpcTags, t);
                    if (!string.IsNullOrWhiteSpace(profile.Accent))
                    {
                        Configuration.KnownAccents ??= new List<string>();
                        TagUtil.AddTag(Configuration.KnownAccents, profile.Accent);
                    }
                    if (!string.IsNullOrWhiteSpace(profile.Tone))
                    {
                        Configuration.KnownTones ??= new List<string>();
                        TagUtil.AddTag(Configuration.KnownTones, profile.Tone);
                    }

                    Configuration.KnownNpcTags = TagUtil.NormDistinct(Configuration.KnownNpcTags);
                    Configuration.KnownAccents = TagUtil.NormDistinct(Configuration.KnownAccents ?? new List<string>());
                    Configuration.KnownTones = TagUtil.NormDistinct(Configuration.KnownTones ?? new List<string>());

                    ApplyPopupVoiceOverrideNoSave(_newNpcPopupName, _newNpcPopupVoiceChoice);
                    Configuration.Save();

                    _newNpcPopupOpen = false;
                    _tagPopupManual = false;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (!_tagPopupManual && ImGui.Button("No (don't ask again)"))
                {
                    Configuration.IgnoredNpcPopup ??= new List<string>();
                    Configuration.IgnoredNpcPopup.Add(_newNpcPopupName);
                    Configuration.IgnoredNpcPopup = Configuration.IgnoredNpcPopup.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    Configuration.Save();

                    // Remove the auto-created profile to avoid clutter
                    Configuration.NpcProfiles.Remove(_newNpcPopupName);
                    Configuration.Save();

                    _newNpcPopupOpen = false;
                    _tagPopupManual = false;
                    ImGui.CloseCurrentPopup();
                }
                ImGui.SameLine();
                if (ImGui.Button(_tagPopupManual ? "Cancel" : "Cancel (ask later)"))
                {
                    // Keep profile but don't keep popup open
                    _newNpcPopupOpen = false;
                    _tagPopupManual = false;
                    ImGui.CloseCurrentPopup();
                }

                ImGui.EndPopup();
            }

            if (!open)
            {
                _newNpcPopupOpen = false;
                _tagPopupManual = false;
            }
        }

        // --- Tiny popup-local widgets (no dependency on ConfigWindow) ---

        private void DrawTagMultiSelectPopup(string label, string id, List<string> selected, List<string> allOptions)
        {
            ImGui.TextUnformatted(label);

            ImGui.SetNextItemWidth(520f);
            if (ImGui.BeginCombo($"##{id}", selected.Count == 0 ? "(none)" : string.Join(", ", selected)))
            {
                foreach (var opt in allOptions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var norm = TagUtil.Norm(opt);
                    if (string.IsNullOrWhiteSpace(norm))
                        continue;

                    bool isSel = selected.Any(x => string.Equals(TagUtil.Norm(x), norm, StringComparison.OrdinalIgnoreCase));
                    if (ImGui.Selectable($"{opt}##{id}_{opt}", isSel, ImGuiSelectableFlags.DontClosePopups))
                    {
                        if (isSel)
                            selected.RemoveAll(x => string.Equals(TagUtil.Norm(x), norm, StringComparison.OrdinalIgnoreCase));
                        else
                            selected.Add(opt);

                        // normalize in-place
                        var n = TagUtil.NormDistinct(selected);
                        selected.Clear();
                        selected.AddRange(n);
                    }
                }

                ImGui.EndCombo();
            }
        }

        private bool DrawStringComboPopup(string label, string id, ref string value, List<string> options, float width)
        {
            ImGui.SetNextItemWidth(width);
            var preview = string.IsNullOrWhiteSpace(value) ? "(none)" : value;

            ImGui.SetNextWindowSizeConstraints(new Vector2(width, 0), new Vector2(width, 420));
            if (ImGui.BeginCombo($"{label}##{id}", preview))
            {
                if (ImGui.Selectable("(none)", string.IsNullOrWhiteSpace(value), ImGuiSelectableFlags.DontClosePopups))
                {
                    value = "";
                    ImGui.EndCombo();
                    return true;
                }

                foreach (var o in options.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.IsNullOrWhiteSpace(o)) continue;
                    bool sel = string.Equals(value, o, StringComparison.OrdinalIgnoreCase);
                    if (ImGui.Selectable(o, sel, ImGuiSelectableFlags.DontClosePopups))
                    {
                        value = o;
                        ImGui.EndCombo();
                        return true;
                    }
                }

                ImGui.EndCombo();
            }

            return false;
        }




private List<string> GetCandidateVoicesForProfile(NpcProfile profile)
{
    profile ??= new NpcProfile();

    var req = TagUtil.NormDistinct(profile.RequiredVoiceTags ?? new List<string>());
    var pref = TagUtil.NormDistinct(profile.PreferredVoiceTags ?? new List<string>());

    var reqSet = new HashSet<string>(req, StringComparer.OrdinalIgnoreCase);
    var prefSet = new HashSet<string>(pref, StringComparer.OrdinalIgnoreCase);

    var scored = new List<(string Voice, int Score)>();
    var strictMatches = true;

    if (Configuration.VoiceProfiles == null || Configuration.VoiceProfiles.Count == 0)
        return new List<string>();

    foreach (var kv in Configuration.VoiceProfiles)
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

        if (!string.IsNullOrWhiteSpace(profile.Accent) &&
            !string.IsNullOrWhiteSpace(vp.Accent) &&
            string.Equals(profile.Accent.Trim(), vp.Accent.Trim(), StringComparison.OrdinalIgnoreCase))
            score += 2;

        if (!string.IsNullOrWhiteSpace(profile.Tone) &&
            !string.IsNullOrWhiteSpace(vp.Tone) &&
            string.Equals(profile.Tone.Trim(), vp.Tone.Trim(), StringComparison.OrdinalIgnoreCase))
            score += 1;

        scored.Add((NormalizeVoiceName(voice), score));
    }

    if (scored.Count == 0)
    {
        strictMatches = false;

        foreach (var kv in Configuration.VoiceProfiles)
        {
            var voice = kv.Key;
            var vp = kv.Value;
            if (vp == null) continue;
            if (!vp.Enabled) continue;
            scored.Add((NormalizeVoiceName(voice), 0));
        }
    }

    var result = scored
        .OrderByDescending(x => x.Score)
        .ThenBy(x => x.Voice, StringComparer.OrdinalIgnoreCase)
        .Select(x => x.Voice)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    // Store note for UI if you want it later.
    // (We keep it local for now to avoid more state.)
    _ = strictMatches;

    return result;
}

private bool DrawVoiceComboPopup(string label, string id, ref string value, List<string> options, float width)
{
    ImGui.SetNextItemWidth(width);
    var preview = string.IsNullOrWhiteSpace(value) ? "(Auto - use resolver)" : value;

    if (ImGui.BeginCombo($"{label}##{id}", preview))
    {
        bool changed = false;

        if (ImGui.Selectable("(Auto - use resolver)", string.IsNullOrWhiteSpace(value), ImGuiSelectableFlags.DontClosePopups))
        {
            value = "";
            changed = true;
        }

        foreach (var o in options)
        {
            if (string.IsNullOrWhiteSpace(o)) continue;
            bool sel = string.Equals(value, o, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(o, sel, ImGuiSelectableFlags.DontClosePopups))
            {
                value = o;
                changed = true;
            }
        }

        ImGui.EndCombo();
        return changed;
    }

    return false;
}

private void ApplyPopupVoiceOverrideNoSave(string npcName, string voiceChoice)
{
    npcName = (npcName ?? "").Trim();
    if (string.IsNullOrWhiteSpace(npcName))
        return;

    voiceChoice = NormalizeVoiceName((voiceChoice ?? "").Trim());

    Configuration.NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();
    Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    // Remove old override entry
    Configuration.NpcExactVoiceOverrides.RemoveAll(o =>
        o != null && string.Equals((o.NpcKey ?? "").Trim(), npcName, StringComparison.OrdinalIgnoreCase));

    if (string.IsNullOrWhiteSpace(voiceChoice))
    {
        // Clear sticky assignment so next talk re-resolves from tags
        Configuration.NpcAssignedVoices.Remove(npcName);
        return;
    }

    Configuration.NpcExactVoiceOverrides.Add(new NpcExactVoiceOverride
    {
        NpcKey = npcName,
        Voice = voiceChoice,
        Enabled = true
    });

    // Keep sticky assignment aligned so previews update immediately.
    Configuration.NpcAssignedVoices[npcName] = voiceChoice;
}

        public void SetDebugOverlayOpen(bool open)
        {
            DebugOverlayWindow.IsOpen = open;
            Configuration.DebugOverlayEnabled = open;
            Configuration.Save();
        }

        
        // UI helpers (kept thin; core logic lives in the same code paths as the chat commands).
        public void ToggleRequiredVoiceTagForCurrentTarget(string tag)
        {
            ToggleRequiredVoiceTagForNpcName(GetCurrentTargetNpcName(), tag);
        }

        public void ClearCurrentTargetNpcCompletely()
        {
            var name = GetCurrentTargetNpcName();
            if (string.IsNullOrWhiteSpace(name))
            {
                try { Svc.Chat.Print("[NpcVoiceMaster] No target selected."); } catch { }
                return;
            }

            ClearNpcCompletely(name);
        }

        public void ToggleRequiredVoiceTagForNpcName(string? npcName, string tag)
        {
            try
            {
                npcName = (npcName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(npcName))
                {
                    try { Svc.Chat.Print("[NpcVoiceMaster] NPC name is empty."); } catch { }
                    return;
                }

                tag = TagUtil.Norm(tag);
                if (string.IsNullOrWhiteSpace(tag))
                {
                    try { Svc.Chat.Print("[NpcVoiceMaster] Tag is empty."); } catch { }
                    return;
                }

                Configuration.NpcProfiles ??= new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase);
                if (!Configuration.NpcProfiles.TryGetValue(npcName, out var profile) || profile == null)
                {
                    profile = new NpcProfile();
                    Configuration.NpcProfiles[npcName] = profile;
                }

                profile.RequiredVoiceTags ??= new List<string>();
                if (TagUtil.Contains(profile.RequiredVoiceTags, tag))
                    TagUtil.RemoveTag(profile.RequiredVoiceTags, tag);
                else
                    TagUtil.AddTag(profile.RequiredVoiceTags, tag);

                profile.RequiredVoiceTags = TagUtil.NormDistinct(profile.RequiredVoiceTags);

                // Force re-resolve next time (so you immediately see the effect).
                Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Configuration.NpcAssignedVoices.Remove(npcName);

                Configuration.Save();

                try { Svc.Chat.Print($"[NpcVoiceMaster] Required voice-tag {(TagUtil.Contains(profile.RequiredVoiceTags, tag) ? "added" : "removed")}: {tag} (NPC: {npcName})"); } catch { }
            }
            catch (Exception ex)
            {
                try { Svc.Log.Error(ex, "ToggleRequiredVoiceTagForNpcName failed"); } catch { }
            }
        }

        public void ClearNpcCompletely(string npcName)
        {
            try
            {
                npcName = (npcName ?? "").Trim();
                if (string.IsNullOrWhiteSpace(npcName))
                    return;

                Configuration.NpcProfiles?.Remove(npcName);
                Configuration.NpcAssignedVoices?.Remove(npcName);
                Configuration.NpcBucketOverrides?.Remove(npcName);

                if (Configuration.NpcExactVoiceOverrides != null)
                    Configuration.NpcExactVoiceOverrides.RemoveAll(x => string.Equals(x.NpcName?.Trim(), npcName, StringComparison.OrdinalIgnoreCase));

                Configuration.IgnoredNpcPopup?.RemoveAll(x => string.Equals(x?.Trim(), npcName, StringComparison.OrdinalIgnoreCase));

                Configuration.Save();

                try { Svc.Chat.Print($"[NpcVoiceMaster] Cleared NPC data: {npcName}"); } catch { }
            }
            catch (Exception ex)
            {
                try { Svc.Log.Error(ex, "ClearNpcCompletely failed"); } catch { }
            }
        }

        private string GetCurrentTargetNpcName()
        {
            try
            {
                var target = Svc.Targets.Target;
                if (target == null)
                    return string.Empty;

                return (target.Name.TextValue ?? "").Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

public List<string> GetAllConfiguredVoicesForUi()
{
    return GetAllConfiguredVoices();
}

        public string GetCurrentTargetNameForUi() => GetCurrentTargetNpcName();


public string PreviewVoiceForNpc(string npcName)
{
    npcName = (npcName ?? "").Trim();
    if (string.IsNullOrWhiteSpace(npcName))
        return "";

    // 1) Exact override
    if (TryGetExactVoiceOverride(npcName, out var exactVoice) && !string.IsNullOrWhiteSpace(exactVoice))
        return exactVoice;

    // 2) Sticky assignment
    if (Configuration.NpcAssignedVoices != null &&
        Configuration.NpcAssignedVoices.TryGetValue(npcName, out var assigned) &&
        !string.IsNullOrWhiteSpace(assigned))
        return assigned;

    // 3) Named voice match (no side effects)
    if (TryMatchVoiceFromNpcName(npcName, out var namedVoice, out _))
        return namedVoice;

    // 4) Tag-based resolution (no side effects)
    var byTags = ResolveVoiceByTags(npcName);
    if (!string.IsNullOrWhiteSpace(byTags))
        return byTags;

    // 5) Final fallback: non-reserved enabled voices first (never silent)
    var all = GetAllConfiguredVoices();
    if (all.Count == 0)
        return "";

    var eligible = new List<string>(all.Count);
    foreach (var v in all)
    {
        if (Configuration.VoiceProfiles != null &&
            Configuration.VoiceProfiles.TryGetValue(v, out var vp) &&
            vp != null &&
            vp.Reserved)
            continue;

        eligible.Add(v);
    }

    if (eligible.Count == 0)
        eligible = all;

    return eligible[_rng.Next(eligible.Count)];
}


        private bool TryMatchVoiceFromNpcName(string npcName, out string voice, out string reason)
        {
            voice = "";
            reason = "";

            npcName = (npcName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(npcName))
                return false;

            // Consider ALL configured voices (including reserved). This is an explicit name match.
            var all = GetAllConfiguredVoices();
            if (all.Count == 0)
                return false;

            // Normalize NPC name for matching.
            static string Norm(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                var sb = new StringBuilder(s.Length);
                foreach (var ch in s)
                {
                    if (char.IsLetterOrDigit(ch))
                        sb.Append(char.ToLowerInvariant(ch));
                    else
                        sb.Append(' ');
                }
                return sb.ToString();
            }

            var npcNorm = Norm(npcName);
            var npcTokens = npcNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            // Prefer the longest/best match (more specific voice name).
            string bestVoice = "";
            int bestScore = 0;

            foreach (var v in all)
            {
                var nv = NormalizeVoiceName(v);
                if (string.IsNullOrWhiteSpace(nv))
                    continue;

                var baseName = nv;
                try { baseName = Path.GetFileNameWithoutExtension(nv) ?? nv; } catch { }

                var voiceNorm = Norm(baseName);
                if (string.IsNullOrWhiteSpace(voiceNorm))
                    continue;

                // Fast path: substring match on normalized strings.
                if (!string.IsNullOrWhiteSpace(npcNorm) && npcNorm.Contains(voiceNorm, StringComparison.Ordinal))
                {
                    var score = voiceNorm.Length;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestVoice = nv;
                    }
                    continue;
                }

                // Token-order match: all voice tokens appear in NPC tokens in sequence.
                var voiceTokens = voiceNorm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (voiceTokens.Length == 0)
                    continue;

                int ti = 0;
                for (int ni = 0; ni < npcTokens.Length && ti < voiceTokens.Length; ni++)
                {
                    if (npcTokens[ni] == voiceTokens[ti])
                        ti++;
                }

                if (ti == voiceTokens.Length)
                {
                    var score = voiceTokens.Sum(t => t.Length);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestVoice = nv;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(bestVoice))
                return false;

            voice = bestVoice;
            reason = "npc-name-match";
            return true;
        }


private bool TryGetExactVoiceOverride(string npcName, out string voice)
{
    voice = "";
    try
    {
        if (Configuration.NpcExactVoiceOverrides == null)
            return false;

        foreach (var o in Configuration.NpcExactVoiceOverrides)
        {
            if (o == null) continue;
            if (string.Equals((o.NpcKey ?? "").Trim(), npcName, StringComparison.OrdinalIgnoreCase))
            {
                voice = (o.Voice ?? "").Trim();
                return true;
            }
        }
    }
    catch { }
    return false;
}

private void SetBucketForCurrentTarget(string bucketName)
        {
            // Legacy command name kept for convenience; buckets are gone.
            // This now adds a REQUIRED voice-tag to the current target NPC profile.
            try
            {
                var target = Svc.Targets.Target;
                if (target == null)
                {
                    Svc.Chat.Print("[NpcVoiceMaster] No target selected.");
                    return;
                }

                var npcName = (target.Name.TextValue ?? "").Trim();
                if (string.IsNullOrWhiteSpace(npcName))
                {
                    Svc.Chat.Print("[NpcVoiceMaster] Target has no name.");
                    return;
                }

                var tag = TagUtil.Norm(bucketName);
                if (string.IsNullOrWhiteSpace(tag))
                {
                    Svc.Chat.Print("[NpcVoiceMaster] Tag is empty.");
                    return;
                }

                Configuration.NpcProfiles ??= new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase);
                if (!Configuration.NpcProfiles.TryGetValue(npcName, out var profile) || profile == null)
                {
                    profile = new NpcProfile();
                    Configuration.NpcProfiles[npcName] = profile;
                }

                profile.RequiredVoiceTags ??= new List<string>();
                if (TagUtil.Contains(profile.RequiredVoiceTags, tag))
                    TagUtil.RemoveTag(profile.RequiredVoiceTags, tag);
                else
                    TagUtil.AddTag(profile.RequiredVoiceTags, tag);
                profile.RequiredVoiceTags = TagUtil.NormDistinct(profile.RequiredVoiceTags);

                // Force re-resolve next time (so you immediately see the effect).
                Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Configuration.NpcAssignedVoices.Remove(npcName);

                Configuration.Save();
                Svc.Chat.Print($"[NpcVoiceMaster] Saved required voice-tag for {npcName}: {tag}");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[NpcVoiceMaster] Failed to set NPC required voice-tag.");
                try { Svc.Chat.Print($"[NpcVoiceMaster] Failed to set NPC required voice-tag: {ex.Message}"); } catch { }
            }
        }

        public void EnsureCacheRootExists()
        {
            EnsureDirectoryExists(ResolvedCacheFolder);
        }

        private string GetCacheRoot()
        {
            var overridePath = (Configuration.CacheFolderOverride ?? "").Trim();
            if (!string.IsNullOrWhiteSpace(overridePath))
                return overridePath;

            return Path.Combine(_pi.ConfigDirectory.FullName, "cache");
        }

        private unsafe void OnTalkPostDraw(AddonEvent evt, AddonArgs args)
        {
            if (!Configuration.Enabled)
                return;

            try
            {
                if (!TryReadTalkVisibleText(out var npc, out var line))
                    return;

                npc = (npc ?? "").Trim();
                line = (line ?? "").Trim();

                LastTalkNpc = npc;
                LastTalkLine = line;
                LastTalkKey = $"{npc}||{line}";
                LastTalkAt = DateTime.Now;

                MaybeQueueNewNpcPopup(npc);

                if (string.IsNullOrWhiteSpace(line))
                    return;

                // Previously, repeated identical lines were suppressed to avoid duplicate playback.
                // This behaviour has been removed so that the same line can be spoken consecutively if desired.
                _ = SpeakTalkLineQueuedAsync(string.IsNullOrWhiteSpace(npc) ? "Unknown" : npc, line);
            }
            catch (Exception ex)
            {
                Svc.Log.Debug(ex, "[NpcVoiceMaster] OnTalkPostDraw failed.");
            }
        }

        private unsafe bool TryReadTalkVisibleText(out string npcName, out string lineText)
        {
            npcName = "";
            lineText = "";

            nint addonPtr = Svc.GameGui.GetAddonByName("Talk", 1);
            if (addonPtr == 0)
                return false;

            var addon = (AtkUnitBase*)addonPtr;
            if (addon == null)
                return false;

            var strings = ReadAllTextFromAddon(addon);
            if (strings.Count == 0)
                return false;

            var longest = strings.OrderByDescending(s => s.Length).FirstOrDefault() ?? "";
            var shortest = strings.OrderBy(s => s.Length).FirstOrDefault() ?? "";

            if (strings.Count == 1)
            {
                npcName = "";
                lineText = longest;
                return true;
            }

            if (shortest.Length <= 1)
                shortest = "";

            if (string.Equals(shortest, longest, StringComparison.OrdinalIgnoreCase))
                shortest = "";

            npcName = shortest;
            lineText = longest;
            return true;
        }

        private static unsafe List<string> ReadAllTextFromAddon(AtkUnitBase* addon)
        {
            var results = new List<string>();

            try
            {
                var mgr = addon->UldManager;
                var count = mgr.NodeListCount;
                var list = mgr.NodeList;

                for (int i = 0; i < count; i++)
                {
                    var node = list[i];
                    if (node == null)
                        continue;

                    if (node->Type != NodeType.Text)
                        continue;

                    var textNode = (AtkTextNode*)node;
                    var s = textNode->NodeText.ToString();
                    s = CleanText(s);

                    if (!string.IsNullOrWhiteSpace(s))
                        results.Add(s);
                }
            }
            catch { }

            return results.Distinct(StringComparer.Ordinal).ToList();
        }

        private static string CleanText(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            s = s.Replace("\u0002", "").Replace("\u0003", "").Trim();

            // Remove any trailing [+number] tokens (e.g., "Hello there [+1]") which are not part of the actual dialog.
            // This pattern matches a plus sign with digits inside square brackets at the end of the string.
            s = Regex.Replace(s, @"\[\+\d+\]$", "").Trim();

            // Collapse multiple whitespace characters into a single space.
            s = Regex.Replace(s, @"\s+", " ");
            return s;
        }

        private async Task SpeakTalkLineQueuedAsync(string npcName, string lineText)
        {
            // Serialize TTS generation so we don't overload AllTalk with concurrent requests.
            await _speakQueue.WaitAsync();
            byte[]? audio = null;
            try
            {
                var voice = ResolveVoiceForNpc(npcName);
                if (string.IsNullOrWhiteSpace(voice))
                    return;

                // Determine if this line originates from the local player. A line is considered player-controlled if
                // the NPC name matches the local player's character name (case-insensitive) or if the NPC name is
                // "Unknown" (used as a fallback when no name is detected). For player lines, disable caching so the
                // line can be repeated immediately without reusing the cached audio. When not a player line, caching
                // is enabled to avoid redundant TTS requests.
                var localPlayerName = Svc.Objects.LocalPlayer?.Name?.TextValue ?? "";
                var isPlayerNameMatch = !string.IsNullOrWhiteSpace(localPlayerName) &&
                               string.Equals(localPlayerName.Trim(), npcName, StringComparison.OrdinalIgnoreCase);

                // Check if this NPC has been manually tagged as a player ("pc") in its profile. A pc tag on either
                // required or preferred voice-tags indicates that the user wants this character treated as a player for
                // caching and cleanup purposes.
                bool isTaggedPlayer = false;
                if (Configuration.NpcProfiles != null &&
                    Configuration.NpcProfiles.TryGetValue(npcName, out var np) &&
                    np != null)
                {
                    var reqSet = TagUtil.ToSet(np.RequiredVoiceTags);
                    var prefSet = TagUtil.ToSet(np.PreferredVoiceTags);
                    isTaggedPlayer = reqSet.Contains("pc") || prefSet.Contains("pc");
                }

                var isPlayer = isPlayerNameMatch || isTaggedPlayer;
                var isPlayerCache = isPlayer || string.Equals(npcName, "Unknown", StringComparison.OrdinalIgnoreCase);
                var useCache = !isPlayer;
                audio = await GetOrCreateCachedTtsAsync(npcName, lineText, voice, useCache: useCache, isPlayerCache: isPlayerCache);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[NpcVoiceMaster] SpeakTalkLineQueuedAsync failed.");
            }
            finally
            {
                _speakQueue.Release();
            }

            // Play the audio outside of the semaphore to avoid blocking subsequent lines from queuing.
            if (audio != null && audio.Length > 0)
            {
                try
                {
                    _player.PlayAudio(audio);
                }
                catch (Exception ex)
                {
                    Svc.Log.Error(ex, "[NpcVoiceMaster] SpeakTalkLineQueuedAsync playback failed.");
                }
            }
        }

        private static bool EndsWithWay(string npcName)
        {
            var n = (npcName ?? "").Trim();
            return n.EndsWith("way", StringComparison.OrdinalIgnoreCase);
        }



        // ============================================================
        // Auto-bucketing (called by ConfigWindow)
        // ============================================================

        // Overload to support call sites that only pass the voice list.

        public string NormalizeVoiceName(string voice)
        {
            voice = (voice ?? "").Trim();
            if (string.IsNullOrWhiteSpace(voice))
                return "";

            // Strip path if somebody pasted one.
            try
            {
                voice = Path.GetFileName(voice) ?? voice;
            }
            catch { }

            // Add .wav if missing
            if (!voice.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                voice += ".wav";

            return voice;
        }

        public void AutoBucketVoicesFromNames(List<string> voiceNames)
            => AutoBucketVoicesFromNames(voiceNames, clearBucketsFirst: false);

        // Legacy name kept; buckets are gone. This now auto-TAGS voices from filenames.
        public void AutoBucketVoicesFromNames(List<string> voiceNames, bool clearBucketsFirst)
        {
            voiceNames ??= new List<string>();

            Configuration.VoiceProfiles ??= new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);

            if (clearBucketsFirst)
            {
                Configuration.VoiceProfiles.Clear();

                // Also clear legacy buckets so old UI/state doesn't keep re-appearing.
                Configuration.VoiceBuckets ??= new List<VoiceBucket>();
                Configuration.VoiceBuckets.Clear();
            }

            foreach (var raw in voiceNames)
            {
                var voice = NormalizeVoiceName(raw);
                if (string.IsNullOrWhiteSpace(voice))
                    continue;

                if (!Configuration.VoiceProfiles.TryGetValue(voice, out var vp) || vp == null)
                {
                    vp = new VoiceProfile();
                    Configuration.VoiceProfiles[voice] = vp;
                }

                var suggested = AutoTagger.SuggestVoiceTagsFromFilename(voice);
                vp.Tags ??= new List<string>();
                foreach (var t in suggested)
                    TagUtil.AddTag(vp.Tags, t);

                vp.Tags = TagUtil.NormDistinct(vp.Tags);

                // If we still have no tags, keep it eligible for fallback.
                if (vp.Tags.Count == 0)
                    TagUtil.AddTag(vp.Tags, "default");
            }

            Configuration.Save();
        }

        // ============================================================
        // Gender detection: reflection-based Customize read
        // ============================================================
        private bool TryGetNpcGenderFromMetadata(string npcName, out byte gender)
        {
            gender = 255;

            if (string.IsNullOrWhiteSpace(npcName))
                return false;

            var target = Svc.Targets.Target;
            if (target != null)
            {
                var tname = (target.Name?.ToString() ?? "").Trim();
                if (tname.Equals(npcName, StringComparison.OrdinalIgnoreCase))
                {
                    if (TryReadGenderFromGameObject(target, out gender))
                        return true;
                }
            }

            var candidate = FindNearestNamedObject(npcName);
            if (candidate != null)
            {
                if (TryReadGenderFromGameObject(candidate, out gender))
                    return true;
            }

            return false;
        }

        private IGameObject? FindNearestNamedObject(string npcName)
        {
            try
            {
                IGameObject? best = null;
                float bestDist = float.MaxValue;

                var lp = Svc.Objects.LocalPlayer;
                var playerPos = lp != null ? lp.Position : Vector3.Zero;

                foreach (var obj in Svc.Objects)
                {
                    if (obj == null) continue;

                    var name = (obj.Name?.ToString() ?? "").Trim();
                    if (!name.Equals(npcName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var kind = obj.ObjectKind;
                    if (kind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                        kind != Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc)
                        continue;

                    var d = Vector3.DistanceSquared(playerPos, obj.Position);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = obj;
                    }
                }

                return best;
            }
            catch
            {
                return null;
            }
        }

        private bool TryReadGenderFromGameObject(IGameObject obj, out byte gender)
        {
            gender = 255;

            try
            {
                var t = obj.GetType();
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

                object? customizeObj = null;

                var prop = t.GetProperty("Customize", flags);
                if (prop != null)
                    customizeObj = prop.GetValue(obj);

                if (customizeObj == null)
                {
                    var field = t.GetField("Customize", flags);
                    if (field != null)
                        customizeObj = field.GetValue(obj);
                }

                if (customizeObj == null)
                    return false;

                if (customizeObj is byte[] bytes)
                {
                    if (bytes.Length > 1 && (bytes[1] == 0 || bytes[1] == 1))
                    {
                        gender = bytes[1];
                        return true;
                    }
                    return false;
                }

                if (customizeObj is IEnumerable enumerable)
                {
                    var list = new List<byte>(64);
                    foreach (var item in enumerable)
                    {
                        if (item is byte b) list.Add(b);
                        else if (item is int i && i >= 0 && i <= 255) list.Add((byte)i);
                        else if (item is sbyte sb && sb >= 0) list.Add((byte)sb);

                        if (list.Count > 64) break;
                    }

                    if (list.Count > 1 && (list[1] == 0 || list[1] == 1))
                    {
                        gender = list[1];
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static string GuessGenderBucketFromName(string npcName)
        {
            var n = (npcName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(n))
                return "";

            var lower = n.ToLowerInvariant();
            var tokens = TokenSplit.Split(lower).Where(t => !string.IsNullOrWhiteSpace(t)).ToHashSet();

            if (tokens.Contains("lady") || tokens.Contains("miss") || tokens.Contains("madam") || tokens.Contains("mrs") || tokens.Contains("sister"))
                return "woman";

            if (tokens.Contains("sir") || tokens.Contains("lord") || tokens.Contains("mr") || tokens.Contains("brother"))
                return "male";

            return "";
        }

        // ============================================================
        // Named-voice auto match (exact + first-name alias)
        // ============================================================
        private string ResolveVoiceForNpc(string npcName)
        {
            npcName ??= "";
            npcName = npcName.Trim();

            LastResolvedBucket = "";
            LastResolvedVoice = "";
            LastDetectedGender = "unknown";
            LastResolvePath = "";

            // 1) Manual exact override wins
            var ov = Configuration.NpcExactVoiceOverrides?
                .FirstOrDefault(x => x.Enabled && x.NpcKey.Equals(npcName, StringComparison.OrdinalIgnoreCase));

            if (ov != null && !string.IsNullOrWhiteSpace(ov.Voice))
            {
                LastResolvePath = "manual override";
                LastResolvedVoice = ov.Voice;
                return ov.Voice;
            }

            // 2) Auto-match voice by NPC name or first-name alias
            if (TryFindNamedVoiceMatch(npcName, out var matchedVoice, out var matchKind) && !string.IsNullOrWhiteSpace(matchedVoice))
            {
                Configuration.NpcExactVoiceOverrides ??= new List<NpcExactVoiceOverride>();
                var existing = Configuration.NpcExactVoiceOverrides.FirstOrDefault(x => x.NpcKey.Equals(npcName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    existing.Voice = matchedVoice;
                    existing.Enabled = true;
                }
                else
                {
                    Configuration.NpcExactVoiceOverrides.Add(new NpcExactVoiceOverride
                    {
                        NpcKey = npcName,
                        Voice = matchedVoice,
                        Enabled = true
                    });
                }

                Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Configuration.NpcAssignedVoices[npcName] = matchedVoice;

                Configuration.Save();

                LastResolvePath = matchKind;
                LastResolvedVoice = matchedVoice;
                return matchedVoice;
            }

            // 3) Sticky assigned voice
            if (Configuration.NpcAssignedVoices != null &&
                Configuration.NpcAssignedVoices.TryGetValue(npcName, out var assigned) &&
                !string.IsNullOrWhiteSpace(assigned))
            {
                LastResolvePath = "assigned voice";
                LastResolvedVoice = assigned;
                return assigned;
            }

            // 4) Tag-based resolution (buckets removed)
            var voice = ResolveVoiceByTags(npcName);

            if (!string.IsNullOrWhiteSpace(voice))
            {
                Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Configuration.NpcAssignedVoices[npcName] = voice;

                Configuration.Save();

                LastResolvePath = "tag match";
                LastResolvedVoice = voice;
                LastResolvedBucket = ""; // legacy field; left blank intentionally
                return voice;
            }

            // Final fallback: pick any NON-RESERVED enabled voice first (never silent).
            var all = GetAllConfiguredVoices();
            if (all.Count > 0)
            {
                var eligible = new List<string>(all.Count);
                foreach (var v in all)
                {
                    if (Configuration.VoiceProfiles != null &&
                        Configuration.VoiceProfiles.TryGetValue(v, out var vp) &&
                        vp != null &&
                        vp.Reserved)
                    {
                        continue; // reserved voices should not appear in random fallback
                    }
                    eligible.Add(v);
                }

                var pool = eligible.Count > 0 ? eligible : all; // if everything is reserved, still don't go silent
                var pick = pool[_rng.Next(pool.Count)];

                LastResolvePath = eligible.Count > 0 ? "fallback (non-reserved)" : "fallback (all reserved)";
                LastResolvedVoice = pick;
                LastResolvedBucket = "";
                return pick;
            }

            LastResolvePath = "no voices";
            LastResolvedVoice = "";
            LastResolvedBucket = "";
            return "";
        }

        private bool TryFindNamedVoiceMatch(string npcName, out string voice, out string matchKind)
        {
            voice = "";
            matchKind = "";

            var allVoices = GetAllConfiguredVoices();
            if (allVoices.Count == 0)
                return false;

            var npcNormFull = NormalizeNameKey(npcName);
            var npcFirst = ExtractFirstName(npcName);
            var npcNormFirst = NormalizeNameKey(npcFirst);

            // Exact normalized full-name match
            foreach (var v in allVoices)
            {
                var baseName = Path.GetFileNameWithoutExtension(v) ?? v;
                var vNorm = NormalizeNameKey(baseName);

                if (!string.IsNullOrWhiteSpace(npcNormFull) &&
                    string.Equals(vNorm, npcNormFull, StringComparison.OrdinalIgnoreCase))
                {
                    voice = v;
                    matchKind = "named voice (exact)";
                    return true;
                }
            }

            // First-name alias match
            if (!string.IsNullOrWhiteSpace(npcNormFirst))
            {
                foreach (var v in allVoices)
                {
                    var baseName = Path.GetFileNameWithoutExtension(v) ?? v;
                    var vNorm = NormalizeNameKey(baseName);

                    if (string.Equals(vNorm, npcNormFirst, StringComparison.OrdinalIgnoreCase))
                    {
                        voice = v;
                        matchKind = "named voice (first-name alias)";
                        return true;
                    }
                }
            }

            return false;
        }


        // ============================================================
        // Accent normalization + matching
        // ============================================================
        private static string CanonAccent(string? s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(s))
                return "";

            // Common aliases / legacy values
            return s switch
            {
                "us" or "usa" or "american" or "america" => "american",
                "uk" or "british" or "english" or "england" => "british",
                "cockney" => "cockney",
                "irish" or "ireland" => "irish",
                "scottish" or "scotland" => "scottish",
                "indian" or "india" => "indian",
                _ => s
            };
        }

        private static bool AccentMatches(string npcAccentCanon, string voiceAccentCanon)
        {
            if (string.IsNullOrWhiteSpace(npcAccentCanon) || string.IsNullOrWhiteSpace(voiceAccentCanon))
                return false;

            // Cockney is a subset of British:
            // - NPC wants "british" -> accept both "british" and "cockney"
            // - NPC wants "cockney" -> accept only "cockney"
            if (npcAccentCanon == "british")
                return voiceAccentCanon == "british" || voiceAccentCanon == "cockney";

            if (npcAccentCanon == "cockney")
                return voiceAccentCanon == "cockney";

            return string.Equals(npcAccentCanon, voiceAccentCanon, StringComparison.OrdinalIgnoreCase);
        }


        private string ResolveVoiceByTags(string npcName)
        {

            npcName ??= "";
            npcName = npcName.Trim();

            Configuration.NpcProfiles ??= new Dictionary<string, NpcProfile>(StringComparer.OrdinalIgnoreCase);
            Configuration.VoiceProfiles ??= new Dictionary<string, VoiceProfile>(StringComparer.OrdinalIgnoreCase);

            // Pull (or create) profile. We do NOT auto-create unless we need a default tag.
            Configuration.NpcProfiles.TryGetValue(npcName, out var npcProfile);

            var required = TagUtil.ToSet(npcProfile?.RequiredVoiceTags);
            var preferred = TagUtil.ToSet(npcProfile?.PreferredVoiceTags);

            // If NPC has no tags, apply default required tag.
            if (required.Count == 0 && preferred.Count == 0)
            {
                var fallbackTag = TagUtil.Norm(Configuration.DefaultNpcTag);
                if (string.IsNullOrWhiteSpace(fallbackTag))
                    fallbackTag = "default";
                required.Add(fallbackTag);
            }

            var npcAccentCanon = CanonAccent(npcProfile?.Accent);

            // Score each enabled voice (tags first, accent heavily weighted).
            var scoredAll = new List<(string Voice, int Score, string AccentCanon)>();

            foreach (var kv in Configuration.VoiceProfiles)
            {
                var voiceName = (kv.Key ?? "").Trim();
                if (string.IsNullOrWhiteSpace(voiceName)) continue;

                var vp = kv.Value;
                if (vp == null || !vp.Enabled) continue;

                var voiceTags = TagUtil.ToSet(vp.Tags);

                // Required tags must all match.
                if (required.Count > 0 && !required.All(t => voiceTags.Contains(t)))
                    continue;

                var score = 0;

                // Preferred tag matches add points.
                foreach (var t in preferred)
                    if (voiceTags.Contains(t)) score += 10;

                // Having required tags at all is a mild bonus (keeps "default" from swamping everything).
                score += required.Count;

                // Accent: BIG boost (tone intentionally ignored).
                var voiceAccentCanon = CanonAccent(vp.Accent);
                if (!string.IsNullOrWhiteSpace(npcAccentCanon) && AccentMatches(npcAccentCanon, voiceAccentCanon))
                    score += 100;

                // Tiny bonus for richer tagging (helps prefer well-tagged voices when tied).
                score += Math.Min(voiceTags.Count, 6);

                scoredAll.Add((voiceName, score, voiceAccentCanon));
            }

            if (scoredAll.Count == 0)
                return "";

            // Accent filtering: if NPC has an accent and at least one voice matches, restrict to those.
            var pool = scoredAll;
            if (!string.IsNullOrWhiteSpace(npcAccentCanon))
            {
                var accentPool = scoredAll.Where(x => AccentMatches(npcAccentCanon, x.AccentCanon)).ToList();
                if (accentPool.Count > 0)
                    pool = accentPool;
            }

            var bestScore = int.MinValue;
            var best = new List<string>();

            foreach (var x in pool)
            {
                if (x.Score > bestScore)
                {
                    bestScore = x.Score;
                    best.Clear();
                    best.Add(x.Voice);
                }
                else if (x.Score == bestScore)
                {
                    best.Add(x.Voice);
                }
            }

            if (best.Count == 0)
                return "";

            return best[_rng.Next(best.Count)];
        }

        private List<string> GetAllConfiguredVoices()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // New system
            if (Configuration.VoiceProfiles != null)
            {
                foreach (var kv in Configuration.VoiceProfiles)
                {
                    var name = (kv.Key ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(name) && (kv.Value?.Enabled ?? false))
                        set.Add(name);
                }
            }

            // Legacy buckets (kept for backward compatibility / migration)
            if (Configuration.VoiceBuckets != null)
            {
                foreach (var b in Configuration.VoiceBuckets)
                {
                    if (b?.Voices == null) continue;
                    foreach (var v in b.Voices)
                    {
                        if (!string.IsNullOrWhiteSpace(v))
                            set.Add(v.Trim());
                    }
                }
            }

            // Exact overrides
            if (Configuration.NpcExactVoiceOverrides != null)
            {
                foreach (var o in Configuration.NpcExactVoiceOverrides)
                {
                    if (o != null && o.Enabled && !string.IsNullOrWhiteSpace(o.Voice))
                        set.Add(o.Voice.Trim());
                }
            }

            return set.ToList();
        }

        private static string ExtractFirstName(string npcName)
        {
            var s = (npcName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s))
                return "";

            // Split on spaces first; if no spaces, split on punctuation tokens.
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
                return parts[0];

            var tokens = TokenSplit.Split(s).Where(t => !string.IsNullOrWhiteSpace(t)).ToArray();
            return tokens.Length > 0 ? tokens[0] : "";
        }

        private static string NormalizeNameKey(string s)
        {
            s = (s ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(s))
                return "";

            // Turn "Cid nan Garlond" -> "cidnangarlond"
            var tokens = TokenSplit.Split(s).Where(t => !string.IsNullOrWhiteSpace(t));
            return string.Concat(tokens);
        }

        private static void EnsureDirectoryExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            Directory.CreateDirectory(path);
        }

        private static string SafePathPart(string s)
        {
            s = (s ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s)) s = "Unknown";

            foreach (var c in Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            if (s.Length > 64) s = s.Substring(0, 64);
            return s;
        }

        private string GetCacheFilePath(string npcName, string voice, string text)
        {
            EnsureCacheRootExists();

            var root = ResolvedCacheFolder;
            var npcFolder = SafePathPart(npcName);
            var voiceFolder = SafePathPart(voice);

            var folder = Path.Combine(root, npcFolder, voiceFolder);
            EnsureDirectoryExists(folder);

            var key = $"{voice}\n{npcName}\n{text}";
            var hash = Sha256Hex(key);

            return Path.Combine(folder, $"{hash}.wav");
        }

        private static string Sha256Hex(string s)
        {
            using var sha = SHA256.Create();
            var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private async Task<byte[]?> GetOrCreateCachedTtsAsync(string npcName, string text, string voice, bool useCache = true, bool isPlayerCache = false)
        {
            // If caching is disabled (e.g., for one-off player voice lines), bypass the cache entirely.
            if (!useCache)
            {
                return await GenerateTtsAndDownloadAsync(text, voice);
            }

            // For player caches, store all audio under a dedicated folder rather than the NPC name. This makes it
            // easy to prune old player dialogue independently of NPC dialogue. When not a player cache, use the
            // provided npcName as before.
            var nameForCache = isPlayerCache ? PlayerCacheFolder : npcName;
            var path = GetCacheFilePath(nameForCache, voice, text);

            // Try fast path: return cached audio if present.
            if (File.Exists(path))
            {
                try { return await File.ReadAllBytesAsync(path); }
                catch { /* ignore read failures, regenerate below */ }
            }

            var sem = CacheLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
                // Re-check after acquiring the lock in case another thread generated the file.
                if (File.Exists(path))
                    return await File.ReadAllBytesAsync(path);

                var audio = await GenerateTtsAndDownloadAsync(text, voice);
                if (audio == null || audio.Length == 0)
                    return null;

                try
                {
                    EnsureDirectoryExists(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, audio);
                }
                catch (Exception ex)
                {
                    Svc.Log.Warning(ex, $"[NpcVoiceMaster] Failed to write cache: {path}");
                }

                return audio;
            }
            finally
            {
                sem.Release();
            }
        }

        private static string NormalizeBaseUrl(string url)
        {
            url = (url ?? "").Trim();
            while (url.EndsWith("/"))
                url = url.Substring(0, url.Length - 1);
            return url;
        }

        public async Task<List<string>> FetchAllTalkVoicesAsync()
        {
            var baseUrl = NormalizeBaseUrl(Configuration.AllTalkBaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return new List<string>();

            try
            {
                var json = await _http.GetStringAsync($"{baseUrl}/api/voices");
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("voices", out var voicesEl) && voicesEl.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<string>();
                    foreach (var v in voicesEl.EnumerateArray())
                    {
                        var s = v.GetString();
                        if (!string.IsNullOrWhiteSpace(s))
                            list.Add(s);
                    }

                    return list.Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                return new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        private async Task<byte[]?> GenerateTtsAndDownloadAsync(string text, string voice)
        {
            var baseUrl = NormalizeBaseUrl(Configuration.AllTalkBaseUrl);
            if (string.IsNullOrWhiteSpace(baseUrl))
                return null;

            var form = new Dictionary<string, string>
            {
                ["text_input"] = text,
                ["text_filtering"] = "standard",
                ["character_voice_gen"] = voice,
                ["narrator_enabled"] = "false",
                ["text_not_inside"] = "character",
                ["language"] = "auto",
                ["output_file_name"] = "npcvoicemaster",
                ["output_file_timestamp"] = "true",
                ["autoplay"] = "false"
            };

            using var resp = await _http.PostAsync($"{baseUrl}/api/tts-generate", new FormUrlEncodedContent(form));
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var status = doc.RootElement.TryGetProperty("status", out var sEl) ? sEl.GetString() : null;
            if (!string.Equals(status, "generate-success", StringComparison.OrdinalIgnoreCase))
                return null;

            string? relUrl = null;

            if (doc.RootElement.TryGetProperty("output_cache_url", out var cacheEl))
                relUrl = cacheEl.GetString();

            if (string.IsNullOrWhiteSpace(relUrl) && doc.RootElement.TryGetProperty("output_file_url", out var fileEl))
                relUrl = fileEl.GetString();

            if (string.IsNullOrWhiteSpace(relUrl))
                return null;

            return await _http.GetByteArrayAsync($"{baseUrl}{relUrl}");
        }

        /// <summary>
        /// Removes cached audio files generated for player dialogue that are older than the specified retention period.
        /// Only files stored under the PlayerCacheFolder folder are considered. NPC caches are not touched.
        /// </summary>
        /// <param name="retention">The maximum age of files to keep. Files older than this will be deleted.</param>
        private void CleanOldPlayerCache(TimeSpan retention)
        {
            if (retention <= TimeSpan.Zero)
                return;

            try
            {
                var root = ResolvedCacheFolder;
                var playerDir = Path.Combine(root, PlayerCacheFolder);
                if (!Directory.Exists(playerDir))
                    return;

                var now = DateTime.UtcNow;
                foreach (var file in Directory.EnumerateFiles(playerDir, "*.wav", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(file);
                        if (now - info.LastWriteTimeUtc > retention)
                        {
                            info.Delete();
                        }
                    }
                    catch
                    {
                        // Ignore individual file deletion errors and continue with the next file.
                    }
                }
            }
            catch
            {
                // Swallow any unexpected exceptions during cleanup; this should never crash the plugin.
            }
        }

        private Configuration LoadConfigurationSafe()
        {
            try
            {
                var cfg = _pi.GetPluginConfig() as Configuration;
                if (cfg == null)
                {
                    cfg = new Configuration();
                    cfg.Initialize(_pi);
                    cfg.Save();
                    return cfg;
                }

                cfg.Initialize(_pi);
                return cfg;
            }
            catch
            {
                var cfg = new Configuration();
                cfg.Initialize(_pi);
                cfg.Save();
                return cfg;
            }
        }
    }
}