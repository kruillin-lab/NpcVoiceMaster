using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System;

namespace NPCVoiceMaster
{
    public sealed class DebugOverlayWindow : Window
    {
        private readonly Plugin _plugin;

        public DebugOverlayWindow(Plugin plugin)
            : base("NPC Voice Master Debug Overlay##NpcVoiceMasterDebug", ImGuiWindowFlags.AlwaysAutoResize)
        {
            _plugin = plugin;

            RespectCloseHotkey = false;
            DisableWindowSounds = true;
        }

        public override void Draw()
        {
            ImGui.TextUnformatted("NPC Voice Master — Debug");
            ImGui.Separator();

            DrawRow("Last NPC", _plugin.LastTalkNpc);
            DrawRow("Last Line", _plugin.LastTalkLine);
            DrawRow("Last Key", _plugin.LastTalkKey);
            DrawRow("Last At", _plugin.LastTalkAt == DateTime.MinValue ? "" : _plugin.LastTalkAt.ToString("HH:mm:ss.fff"));

            ImGui.Separator();

            DrawRow("Gender", _plugin.LastDetectedGender);
            DrawRow("Tags/Group", _plugin.LastResolvedBucket);
            DrawRow("Voice", _plugin.LastResolvedVoice);
            DrawRow("Resolve Path", _plugin.LastResolvePath);

            ImGui.Separator();

            DrawRow("Cache Folder", _plugin.ResolvedCacheFolder);
        }

        private static void DrawRow(string label, string value)
        {
            value ??= "";

            ImGui.TextUnformatted(label);
            ImGui.SameLine(140);

            // Wrap to keep overlay from stretching into orbit
            ImGui.PushTextWrapPos(520);
            ImGui.TextUnformatted(value);
            ImGui.PopTextWrapPos();
        }
    }
}
