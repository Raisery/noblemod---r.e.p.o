using HarmonyLib;
using loaforcsSoundAPI.Core;
using NobleMod.SoundPack;
using UnityEngine;

namespace NobleMod.Patches;

/// <summary>
/// Corrige <see cref="AudioSource.time"/> seulement en cas de **dépassement réel** du clip
/// (<c>&lt; 0</c> ou <c>&gt;= length</c>, NaN) — évite les erreurs FMOD « invalid seek » sans couper les dernières ms
/// ni intercepter chaque écriture sur <c>time</c> (ce qui cassait début/fin de lecture).
/// </summary>
internal static class AudioSourceTimeToClipClamp
{
    /// <summary>Juste sous <c>length</c> pour éviter un seek exactement à la fin (FMOD).</summary>
    const float UnderEndEpsilon = 0.0001f;

    internal static void Clamp(AudioSource src)
    {
        if (src == null || !src || !src.clip)
            return;
        var len = src.clip.length;
        if (len < 0.001f)
            return;
        var t = src.time;
        if (float.IsNaN(t) || float.IsInfinity(t))
        {
            src.time = 0f;
            return;
        }

        if (t < 0f)
        {
            src.time = 0f;
            return;
        }

        if (t >= len)
            src.time = Mathf.Max(0f, len - UnderEndEpsilon);
    }
}

/// <summary>
/// Contexte d'évaluation pour les conditions NobleMod (sticky) + correction du <see cref="AudioSource.time"/>
/// avant / après <c>AudioSourceAdditionalData.Update</c> (SoundAPI, <c>update_every_frame</c>).
/// </summary>
[HarmonyPatch(typeof(AudioSourceAdditionalData), "Update")]
internal static class SoundApiUpdateEvalContextPatch
{
    [HarmonyPrefix]
    static void Prefix(AudioSourceAdditionalData __instance)
    {
        if (__instance?.Source != null)
        {
            AudioSourceTimeToClipClamp.Clamp(__instance.Source);
            NobleModSoundEvalContext.Enter(__instance.Source);
        }
    }

    [HarmonyPostfix]
    static void Postfix(AudioSourceAdditionalData __instance)
    {
        try
        {
            if (__instance?.Source != null)
                AudioSourceTimeToClipClamp.Clamp(__instance.Source);
        }
        finally
        {
            if (__instance?.Source != null)
                NobleModSoundEvalContext.Exit();
        }
    }
}

/// <summary>
/// Après assignation d'un <see cref="AudioClip"/>, si la position dépasse la nouvelle longueur, on ramène <c>time</c>.
/// </summary>
[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.clip), MethodType.Setter)]
internal static class AudioSourceClipSetterClampPatch
{
    static void Postfix(AudioSource __instance) => AudioSourceTimeToClipClamp.Clamp(__instance);
}
