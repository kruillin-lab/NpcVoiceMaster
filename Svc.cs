using Dalamud.IoC;
using Dalamud.Plugin.Services;

namespace NPCVoiceMaster;

public static class Svc
{
    [PluginService] public static IChatGui Chat { get; private set; } = null!;
    [PluginService] public static IPluginLog Log { get; private set; } = null!;
}
