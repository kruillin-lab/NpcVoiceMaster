using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
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
using System.Numerics;
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

        private string _lastTalkKeyInternal = "";

        private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex TokenSplit = new Regex(@"[^a-zA-Z0-9]+", RegexOptions.Compiled);

        // Debug overlay fields (public getters)
        public string LastTalkNpc { get; private set; } = "";
        public string LastTalkLine { get; private set; } = "";
        public string LastTalkKey { get; private set; } = "";
        public DateTime LastTalkAt { get; private set; } = DateTime.MinValue;

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
                HelpMessage = "Set bucket override for current target to: male"
            });

            _commands.AddHandler(CmdLady, new CommandInfo((c, a) => SetBucketForCurrentTarget("woman"))
            {
                HelpMessage = "Set bucket override for current target to: woman"
            });

            _commands.AddHandler(CmdWay, new CommandInfo((c, a) => SetBucketForCurrentTarget("loporrit"))
            {
                HelpMessage = "Set bucket override for current target to: loporrit (Way)"
            });

            _commands.AddHandler(CmdBot, new CommandInfo((c, a) => SetBucketForCurrentTarget("machine"))
            {
                HelpMessage = "Set bucket override for current target to: machine"
            });

            _commands.AddHandler(CmdMon, new CommandInfo((c, a) => SetBucketForCurrentTarget("monsters"))
            {
                HelpMessage = "Set bucket override for current target to: monsters"
            });

            Svc.AddonLifecycle.RegisterListener(AddonEvent.PostDraw, "Talk", OnTalkPostDraw);

            try { Svc.Chat.Print($"[NpcVoiceMaster] Loaded. Cache: {ResolvedCacheFolder}"); } catch { }
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

        private void DrawUI() => WindowSystem.Draw();
        private void DrawConfigUI() => ConfigWindow.IsOpen = true;

        public void SetDebugOverlayOpen(bool open)
        {
            DebugOverlayWindow.IsOpen = open;
            Configuration.DebugOverlayEnabled = open;
            Configuration.Save();
        }

        private void SetBucketForCurrentTarget(string bucketName)
        {
            try
            {
                var target = Svc.Targets.Target;
                if (target == null)
                {
                    Svc.Chat.Print("[NpcVoiceMaster] No target selected.");
                    return;
                }

                var name = (target.Name?.ToString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name))
                {
                    Svc.Chat.Print("[NpcVoiceMaster] Target has no readable name.");
                    return;
                }

                Configuration.NpcBucketOverrides ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                Configuration.NpcBucketOverrides[name] = bucketName;
                Configuration.NpcAssignedVoices.Remove(name);

                Configuration.Save();

                Svc.Chat.Print($"[NpcVoiceMaster] Set bucket override: {name} -> {bucketName}");
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[NpcVoiceMaster] Failed to set bucket override.");
                try { Svc.Chat.Print($"[NpcVoiceMaster] Failed to set bucket override: {ex.Message}"); } catch { }
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

                if (string.IsNullOrWhiteSpace(line))
                    return;

                var key = LastTalkKey;

                if (key == _lastTalkKeyInternal)
                    return;

                _lastTalkKeyInternal = key;

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
            s = Regex.Replace(s, @"\s+", " ");
            return s;
        }

        private async Task SpeakTalkLineQueuedAsync(string npcName, string lineText)
        {
            await _speakQueue.WaitAsync();
            try
            {
                var voice = ResolveVoiceForNpc(npcName);
                if (string.IsNullOrWhiteSpace(voice))
                    return;

                var audio = await GetOrCreateCachedTtsAsync(npcName, lineText, voice);
                if (audio == null || audio.Length == 0)
                    return;

                _player.PlayAudio(audio);
            }
            catch (Exception ex)
            {
                Svc.Log.Error(ex, "[NpcVoiceMaster] SpeakTalkLineQueuedAsync failed.");
            }
            finally
            {
                _speakQueue.Release();
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
        public void AutoBucketVoicesFromNames(List<string> voiceNames)
            => AutoBucketVoicesFromNames(voiceNames, clearBucketsFirst: false);

        // Primary method (supports the ConfigWindow "clear first" checkbox pattern).
        public void AutoBucketVoicesFromNames(List<string> voiceNames, bool clearBucketsFirst)
        {
            voiceNames ??= new List<string>();

            Configuration.VoiceBuckets ??= new List<VoiceBucket>();

            // Ensure the standard buckets exist.
            EnsureBucketExists("male");
            EnsureBucketExists("woman");
            EnsureBucketExists("boy");
            EnsureBucketExists("girl");
            EnsureBucketExists("loporrit");
            EnsureBucketExists("machine");
            EnsureBucketExists("monsters");
            EnsureBucketExists("default");

            if (clearBucketsFirst)
            {
                foreach (var b in Configuration.VoiceBuckets)
                    b?.Voices?.Clear();
            }

            foreach (var raw in voiceNames)
            {
                var v = (raw ?? "").Trim();
                if (string.IsNullOrWhiteSpace(v))
                    continue;

                var bucket = GuessBucketFromVoiceFileName(v);
                if (string.IsNullOrWhiteSpace(bucket))
                    bucket = "default";

                var bkt = GetOrCreateBucket(bucket);
                bkt.Voices ??= new List<string>();

                if (!bkt.Voices.Any(x => string.Equals(x, v, StringComparison.OrdinalIgnoreCase)))
                    bkt.Voices.Add(v);
            }

            // De-dupe + tidy
            foreach (var b in Configuration.VoiceBuckets)
            {
                if (b?.Voices == null) continue;

                b.Voices = b.Voices
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            Configuration.Save();
        }

        private void EnsureBucketExists(string bucketName)
        {
            if (string.IsNullOrWhiteSpace(bucketName))
                return;

            Configuration.VoiceBuckets ??= new List<VoiceBucket>();

            if (!Configuration.VoiceBuckets.Any(b => b != null && b.Name.Equals(bucketName, StringComparison.OrdinalIgnoreCase)))
            {
                Configuration.VoiceBuckets.Add(new VoiceBucket
                {
                    Name = bucketName,
                    Voices = new List<string>()
                });
            }
        }

        private VoiceBucket GetOrCreateBucket(string bucketName)
        {
            Configuration.VoiceBuckets ??= new List<VoiceBucket>();

            var existing = Configuration.VoiceBuckets
                .FirstOrDefault(b => b != null && b.Name.Equals(bucketName, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
                return existing;

            var created = new VoiceBucket
            {
                Name = bucketName,
                Voices = new List<string>()
            };

            Configuration.VoiceBuckets.Add(created);
            return created;
        }

        private static string GuessBucketFromVoiceFileName(string voiceFile)
        {
            var baseName = Path.GetFileNameWithoutExtension(voiceFile) ?? voiceFile;
            var lower = (baseName ?? "").Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(lower))
                return "default";

            // Tokenize on non-alnum
            var tokens = TokenSplit.Split(lower).Where(t => !string.IsNullOrWhiteSpace(t)).ToHashSet();

            // Strong cues first
            if (tokens.Contains("loporrit") || tokens.Contains("way"))
                return "loporrit";

            if (tokens.Contains("bot") || tokens.Contains("machine") || tokens.Contains("robot") || tokens.Contains("android") || tokens.Contains("mech"))
                return "machine";

            if (tokens.Contains("mon") || tokens.Contains("monster") || tokens.Contains("monsters") || tokens.Contains("beast") || tokens.Contains("creature"))
                return "monsters";

            // If you want to distinguish boy vs girl later, add stricter tags in filenames.
            if (tokens.Contains("boy") || tokens.Contains("lad"))
                return "boy";

            if (tokens.Contains("girl") || tokens.Contains("lass"))
                return "girl";

            if (tokens.Contains("woman") || tokens.Contains("female") || tokens.Contains("lady") || tokens.Contains("miss") || tokens.Contains("madam") || tokens.Contains("mrs"))
                return "woman";

            if (tokens.Contains("male") || tokens.Contains("man") || tokens.Contains("sir") || tokens.Contains("mr") || tokens.Contains("lord"))
                return "male";

            // Soft substring fallback
            if (lower.Contains("female") || lower.Contains("woman") || lower.Contains("lady"))
                return "woman";

            if (lower.Contains("male") || lower.Contains("man") || lower.Contains("sir"))
                return "male";

            return "default";
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

            // 4) Bucket decision: override > ends-with-way > gender > guess > default
            string bucketName;

            if (Configuration.NpcBucketOverrides != null &&
                Configuration.NpcBucketOverrides.TryGetValue(npcName, out var npcBucket) &&
                !string.IsNullOrWhiteSpace(npcBucket))
            {
                bucketName = npcBucket.Trim();
                LastResolvePath = "bucket override";
            }
            else if (EndsWithWay(npcName))
            {
                bucketName = "loporrit";
                LastResolvePath = "endswith Way";
            }
            else if (TryGetNpcGenderFromMetadata(npcName, out var g))
            {
                bucketName = (g == 0) ? "male" : "woman";
                LastDetectedGender = (g == 0) ? "male" : "woman";
                LastResolvePath = "gender metadata";
            }
            else
            {
                var guessed = GuessGenderBucketFromName(npcName);
                if (!string.IsNullOrWhiteSpace(guessed))
                {
                    bucketName = guessed;
                    LastDetectedGender = guessed;
                    LastResolvePath = "name guess";
                }
                else
                {
                    bucketName = (Configuration.DefaultBucket ?? "male").Trim();
                    LastResolvePath = "default bucket";
                }
            }

            LastResolvedBucket = bucketName;

            var bucket = Configuration.VoiceBuckets?
                .FirstOrDefault(b => b.Name.Equals(bucketName, StringComparison.OrdinalIgnoreCase));

            if (bucket == null || bucket.Voices == null || bucket.Voices.Count == 0)
                return "";

            var pick = bucket.Voices[_rng.Next(bucket.Voices.Count)];

            Configuration.NpcAssignedVoices ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Configuration.NpcAssignedVoices[npcName] = pick;
            Configuration.Save();

            LastResolvedVoice = pick;
            return pick;
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

        private List<string> GetAllConfiguredVoices()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

        private async Task<byte[]?> GetOrCreateCachedTtsAsync(string npcName, string text, string voice)
        {
            var path = GetCacheFilePath(npcName, voice, text);

            if (File.Exists(path))
            {
                try { return await File.ReadAllBytesAsync(path); }
                catch { }
            }

            var sem = CacheLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync();
            try
            {
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
