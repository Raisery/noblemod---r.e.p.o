using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NobleMod.Multiplayer;
using NobleMod.SoundPack;
using UnityEngine;
using Random = UnityEngine.Random;

namespace NobleMod.Patches;

/// <summary>
/// Synchronise les tirages aleatoires <see cref="UnityEngine.Random.Range(int,int)"/> faits par SoundAPI
/// dans <c>SoundReplacementHandler.TryGetReplacementClip</c> (selection du groupe parmi plusieurs matches,
/// et selection ponderee du son). Sans ce patch, des groupes pondereses (ex. <c>noblemod_headman.json</c>
/// chacarron 50 / smash-mouth 50) seraient choisis differemment sur chaque client meme avec la sync NobleMod.
///
/// Strategie : Prefix sauvegarde <see cref="Random.state"/> et le force a une seed deterministe derivee de
/// la cle de matching SoundAPI (<c>parent:object:clip</c>) — meme cle entre clients par construction.
/// Postfix restaure <see cref="Random.state"/> pour ne pas pourrir l'aleatoire global du jeu.
///
/// Patch installe manuellement depuis <see cref="Plugin"/> pour rester silencieusement no-op si la version
/// installee de SoundAPI ne contient pas la methode cible (pas d'exception Harmony qui casserait PatchAll).
/// </summary>
internal static class SoundApiReplacementSeedPatch
{
    const int KeyVariantSoundApiReplacement = 7;

    /// <summary>Install : recherche tolerante de la methode cible et application du Prefix/Postfix. Pas d'exception remontee.</summary>
    internal static void TryInstall(Harmony harmony)
    {
        try
        {
            var t = AccessTools.TypeByName("loaforcsSoundAPI.SoundPacks.SoundReplacementHandler");
            if (t == null)
            {
                Plugin.Log.LogWarning("[NobleMod] SoundApi seed patch : type SoundReplacementHandler introuvable, patch ignore.");
                return;
            }

            // Pas d'ambiguite : TryGetReplacementClip n'a pas de surcharge dans SoundAPI 2.x.
            // On evite de specifier les types des parametres (IContext peut etre dans un namespace different selon la version).
            var candidates = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Where(m => m.Name == "TryGetReplacementClip")
                .ToArray();
            if (candidates.Length == 0)
            {
                Plugin.Log.LogWarning("[NobleMod] SoundApi seed patch : methode TryGetReplacementClip introuvable, patch ignore.");
                return;
            }

            if (candidates.Length > 1)
                Plugin.Log.LogWarning($"[NobleMod] SoundApi seed patch : {candidates.Length} surcharges TryGetReplacementClip trouvees, on patch toutes.");

            var prefix = new HarmonyMethod(typeof(SoundApiReplacementSeedPatch), nameof(Prefix));
            var postfix = new HarmonyMethod(typeof(SoundApiReplacementSeedPatch), nameof(Postfix));
            foreach (var m in candidates)
            {
                try
                {
                    harmony.Patch(m, prefix: prefix, postfix: postfix);
                    Plugin.Log.LogInfo($"[NobleMod] SoundApi seed patch : patch installe sur {m.DeclaringType?.FullName}.{m.Name}.");
                }
                catch (Exception exPatch)
                {
                    Plugin.Log.LogWarning($"[NobleMod] SoundApi seed patch : echec patch d'une surcharge : {exPatch.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[NobleMod] SoundApi seed patch : exception inattendue lors de l'install, patch ignore : {ex.Message}");
        }
    }

    static void Prefix(string[] name, ref Random.State __state)
    {
        __state = Random.state;
        if (ModConfig.EnableMultiplayerSoundSync == null || !ModConfig.EnableMultiplayerSoundSync.Value)
            return;
        if (!NobleModSoundSyncNet.IsActiveInRoom)
            return;
        if (name == null || name.Length < 3)
            return;

        try
        {
            var src = NobleModSoundEvalContext.CurrentAudioSource;
            var key = NobleModSoundSyncKey.BuildSnapshotKeyFromTokens(
                parent: name[0],
                objectName: name[1],
                clip: name[2],
                source: src,
                variant: KeyVariantSoundApiReplacement);

            // Combine avec une entree du pool : la seed change a chaque generation de pool
            // (sinon meme son joue 10 fois donnerait toujours la meme variante au sein d'un niveau).
            if (NobleModSoundSyncNet.TryLookup(key, out var poolValue))
            {
                Random.InitState(unchecked((int)key ^ poolValue));
                if (ModConfig.LogMultiplayerSoundSync != null && ModConfig.LogMultiplayerSoundSync.Value)
                    Plugin.Log.LogInfo($"[NobleMod] SoundApi seed patch : seed Random pour {name[0]}:{name[1]}:{name[2]} (key=0x{key:X8}).");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[NobleMod] SoundApi seed patch : exception Prefix : {ex}");
        }
    }

    static void Postfix(Random.State __state)
    {
        Random.state = __state;
    }
}
