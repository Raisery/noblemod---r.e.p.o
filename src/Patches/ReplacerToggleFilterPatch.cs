using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using loaforcsSoundAPI.SoundPacks.Data;
using NobleMod.Replacers;
using NobleMod.SoundPack;
using UnityEngine;

namespace NobleMod.Patches;

/// <summary>
/// Postfix sur <c>SoundReplacementHandler.TryGetReplacementClip</c> : si le replacement est issu d'un
/// fichier JSON dont le toggle est OFF cote joueur, on annule le resultat ; le jeu retombe sur le clip
/// vanilla. Sinon, on enregistre le couple <see cref="AudioSource"/> + chemin pour pouvoir couper
/// instantanement la source si le joueur desactive le toggle plus tard.
///
/// Strictement local : aucune replication reseau. Le pool de sync multijoueur continue de
/// fonctionner independamment (le tirage SoundAPI seed peut tomber identique chez tous les clients,
/// mais ceux qui ont le toggle OFF n'appliquent simplement pas le remplacement).
///
/// Installe manuellement depuis <see cref="Plugin"/> pour rester silencieusement no-op si la
/// methode cible n'existe pas dans la version installee de SoundAPI.
/// </summary>
internal static class ReplacerToggleFilterPatch
{
    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            var t = AccessTools.TypeByName("loaforcsSoundAPI.SoundPacks.SoundReplacementHandler");
            if (t == null)
            {
                Plugin.Log.LogWarning("[NobleMod] Replacers filter : SoundReplacementHandler introuvable, patch ignore.");
                return;
            }

            var candidates = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "TryGetReplacementClip")
                .ToArray();
            if (candidates.Length == 0)
            {
                Plugin.Log.LogWarning("[NobleMod] Replacers filter : TryGetReplacementClip introuvable, patch ignore.");
                return;
            }

            var postfix = new HarmonyMethod(typeof(ReplacerToggleFilterPatch), nameof(Postfix));
            foreach (var m in candidates)
            {
                try
                {
                    harmony.Patch(m, postfix: postfix);
                    Plugin.Log.LogInfo($"[NobleMod] Replacers filter : patch installe sur {m.DeclaringType?.FullName}.{m.Name}.");
                }
                catch (Exception exPatch)
                {
                    Plugin.Log.LogWarning($"[NobleMod] Replacers filter : echec patch d'une surcharge : {exPatch.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NobleMod] Replacers filter : exception install ignoree : {ex.Message}");
        }
    }

    /// <summary>Postfix : on lit le succes/le group selectionne et on annule le replacement si le toggle est OFF.</summary>
    static void Postfix(ref bool __result, ref SoundReplacementGroup group, ref AudioClip clip)
    {
        if (!__result)
            return;
        if (!ReplacerToggleRegistry.IsReady)
            return;

        try
        {
            var filePath = TryGetFilePath(group);
            if (string.IsNullOrEmpty(filePath))
                return;

            if (!ReplacerToggleRegistry.IsEnabled(filePath))
            {
                __result = false;
                group = null;
                clip = null;
                return;
            }

            // Toggle ON : on garde le replacement et on enregistre l'AudioSource pour
            // permettre la coupure instantanee si le joueur passe a OFF en cours de partie.
            var src = NobleModSoundEvalContext.CurrentAudioSource;
            if (src != null)
                ReplacerToggleRegistry.TrackPlayback(src, filePath);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NobleMod] Replacers filter postfix : exception ignoree : {ex.Message}");
        }
    }

    static string TryGetFilePath(SoundReplacementGroup group)
    {
        if (group == null)
            return null;
        var col = group.Parent;
        if (col == null)
            return null;
        // FilePath est expose publiquement par SoundReplacementCollection (et via IFilePathAware).
        var fpProp = col.GetType().GetProperty("FilePath", BindingFlags.Public | BindingFlags.Instance);
        return fpProp?.GetValue(col) as string;
    }
}
