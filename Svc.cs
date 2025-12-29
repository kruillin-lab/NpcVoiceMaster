using Dalamud.IoC;
using Dalamud.Plugin.Services;

namespace NPCVoiceMaster
{
    public sealed class Svc
    {
        [PluginService] public static IChatGui Chat { get; private set; } = null!;

        // Some Dalamud builds use IPluginLog instead of ILog.
        [PluginService] public static IPluginLog Log { get; private set; } = null!;
    }
}
