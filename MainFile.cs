using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace ModConfig;

[ModInitializer("Initialize")]
public class MainFile
{
    internal const string ModId = "sts2.piyixiajiuhenfen.modconfig";
    internal const string Version = "0.1.8";
    internal static readonly Logger Log = new(ModId, LogType.Generic);

    public static void Initialize()
    {
        I18n.Initialize();
        ModConfigManager.Initialize();
        SettingsTabInjector.Initialize();

        Log.Info($"ModConfig v{Version} initialized! (zero Harmony, cross-platform)");
    }
}
