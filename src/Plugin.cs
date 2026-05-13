using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using NobleMod.Multiplayer;
using NobleMod.Patches;
using NobleMod.SoundPack;

namespace NobleMod;

[BepInPlugin(PluginInfo.Guid, PluginInfo.Name, PluginInfo.Version)]
[BepInDependency("me.loaforc.soundapi")]
[BepInDependency(REPOLib.MyPluginInfo.PLUGIN_GUID)]
[BepInDependency("nickklmao.menulib", BepInDependency.DependencyFlags.SoftDependency)]
public sealed class Plugin : BaseUnityPlugin
{
    internal static Plugin Instance;
    internal static Harmony Harmony;
    internal static ManualLogSource Log;

    /// <summary>Répertoire contenant NobleMod.dll (ex. BepInEx/plugins/NobleMod/).</summary>
    internal static string AssemblyDirectory { get; private set; } = "";

    public void Awake()
    {
        try
        {
            Logger.LogInfo($"[NobleMod] Awake() begin (dll={typeof(Plugin).Assembly.Location}).");

            AssemblyDirectory = Path.GetDirectoryName(typeof(Plugin).Assembly.Location) ?? "";
            Instance = this;
            Log = Logger;

            ModConfig.Bind(Config);
            NobleModSoundPackConditionBootstrap.TryRegister(Logger);
            LevelEnemyOverrideBank.Initialize(Logger);

            Harmony = new Harmony(PluginInfo.Guid);
            Harmony.PatchAll();

            // Patch tolerant aux changements d'API SoundAPI (resolution par nom, install manuel pour ne pas
            // casser PatchAll si la methode cible bouge dans une future version de loaforcsSoundAPI).
            SoundApiReplacementSeedPatch.TryInstall(Harmony);

            NobleModSoundSyncNet.Initialize();

            Logger.LogInfo($"{PluginInfo.Name} v{PluginInfo.Version} loaded.");

            NobleModMenuIntegration.TryRegister(Logger);
        }
        catch (System.Exception ex)
        {
            Logger.LogError(ex);
        }
    }
}
