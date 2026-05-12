using System.Reflection;
using BepInEx.Logging;
using loaforcsSoundAPI;

namespace NobleMod.SoundPack;

internal static class NobleModSoundPackConditionBootstrap
{
    internal static void TryRegister(ManualLogSource log)
    {
        if (!ModConfig.EnableNobleModSoundPackConditions.Value)
        {
            log.LogInfo("[NobleMod] Conditions pack NobleMod desactivees (config) : pas d'enregistrement SoundAPI.");
            return;
        }

        try
        {
            SoundAPI.RegisterAll(Assembly.GetExecutingAssembly());
            log.LogInfo("[NobleMod] Conditions SoundAPI NobleMod enregistrees (ex. NobleMod:random_slot).");
        }
        catch (System.Exception ex)
        {
            log.LogError($"[NobleMod] Echec enregistrement conditions SoundAPI : {ex}");
        }
    }
}
