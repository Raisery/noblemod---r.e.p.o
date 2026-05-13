using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NobleMod.Replacers;

namespace NobleMod.Patches;

/// <summary>
/// Postfix sur <c>SoundPackDataHandler.AddLoadedPack(SoundPack)</c> pour declarer au
/// <see cref="ReplacerToggleRegistry"/> chaque pack charge par SoundAPI (filtre par nom 'NobleMod' au sein du registry).
///
/// Methode cible privee dans SoundAPI : install manuel et tolerant (resolution par nom, pas
/// d'exception remontee si la signature change dans une future version).
/// </summary>
internal static class SoundPackLoadedDiscoveryPatch
{
    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            var t = AccessTools.TypeByName("loaforcsSoundAPI.SoundPacks.SoundPackDataHandler");
            if (t == null)
            {
                Plugin.Log.LogWarning("[NobleMod] Replacers discovery : SoundPackDataHandler introuvable, decouverte ignoree.");
                return;
            }

            var candidates = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == "AddLoadedPack")
                .ToArray();
            if (candidates.Length == 0)
            {
                Plugin.Log.LogWarning("[NobleMod] Replacers discovery : AddLoadedPack introuvable.");
                return;
            }

            var postfix = new HarmonyMethod(typeof(SoundPackLoadedDiscoveryPatch), nameof(Postfix));
            foreach (var m in candidates)
            {
                try
                {
                    harmony.Patch(m, postfix: postfix);
                    Plugin.Log.LogInfo($"[NobleMod] Replacers discovery : patch postfix installe sur {m.DeclaringType?.FullName}.{m.Name}.");
                }
                catch (Exception exPatch)
                {
                    Plugin.Log.LogWarning($"[NobleMod] Replacers discovery : echec patch surcharge : {exPatch.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NobleMod] Replacers discovery : exception install ignoree : {ex.Message}");
        }
    }

    /// <summary>Parametre aligne sur <c>AddLoadedPack(SoundPack pack)</c> — type pleinement qualifie (evite conflit avec le namespace <see cref="NobleMod.SoundPack"/>).</summary>
    static void Postfix(loaforcsSoundAPI.SoundPacks.Data.SoundPack pack)
    {
        if (pack == null)
            return;
        try
        {
            ReplacerToggleRegistry.DiscoverPack(pack);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NobleMod] Replacers discovery postfix : {ex.Message}");
        }
    }
}
