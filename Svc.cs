using Dalamud.IoC;
using Dalamud.Plugin.Services;

namespace NPCVoiceMaster
{
    // NOTE: Not static so it can be used with pluginInterface.Create<Svc>()
    internal sealed class Svc
    {
        [PluginService] internal static IChatGui Chat { get; private set; } = null!;
        [PluginService] internal static IPluginLog Log { get; private set; } = null!;
        [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
        [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
        [PluginService] internal static ITargetManager Targets { get; private set; } = null!;

        // Used to find NPC objects by name and to get LocalPlayer (avoids obsolete IClientState.LocalPlayer)
        [PluginService] internal static IObjectTable Objects { get; private set; } = null!;
    }
}
