using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace NobleMod;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
[BepInDependency("nickklmao.menulib", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    internal static Harmony Harmony;
    internal static ManualLogSource Log;

    private void Awake()
    {
        Instance = this;
        Log = Logger;
        ModConfig.Bind(Config);
        SoundBank.Initialize(Logger);
        LevelEnemyOverrideBank.Initialize(Logger);

        Harmony = new Harmony(PluginInfo.Guid);
        Harmony.PatchAll();

        Logger.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} loaded.");
        NobleModMenuIntegration.TryRegister(Logger);
    }

    private void OnDestroy()
    {
        // Do not unpatch here; lifecycle transitions may destroy/recreate plugin objects.
    }
}
