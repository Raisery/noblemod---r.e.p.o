using System;
using System.Text;
using loaforcsSoundAPI.SoundPacks.Data;
using Photon.Pun;
using UnityEngine;

namespace NobleMod.Multiplayer;

/// <summary>
/// Construit une cle deterministe et stable entre clients pour identifier l'evenement audio en cours
/// d'evaluation. Composantes :
/// <list type="bullet">
/// <item><b>Matches du groupe</b> : <see cref="SoundReplacementGroup.Matches"/> — meme pack JSON sur tous les clients ⇒ stable.</item>
/// <item><b>ViewID du PhotonView le plus proche</b> de l'<see cref="AudioSource"/> (0 si aucun) ⇒ identifie une instance reseau.</item>
/// <item><b>Clip vanilla</b> (nom du <see cref="AudioClip"/> actuel, prefixe pack retire) ⇒ utile si le groupe a plusieurs matches.</item>
/// <item><b>Occurrence temporelle</b> : <c>floor(PhotonNetwork.Time / quantum)</c> ⇒ change a intervalle regulier
/// (sticky : ~clip.length ; non-sticky : <see cref="QuantumNonStickySeconds"/>).</item>
/// </list>
///
/// La cle finale est un <see cref="uint"/> FNV-1a 32 bits.
/// </summary>
internal static class NobleModSoundSyncKey
{
    /// <summary>Pas par defaut pour les tirages non-sticky : 0.25s ≈ 4 changements / seconde (suffisamment fin et tolerant au decalage Photon ~100ms).</summary>
    const double QuantumNonStickySeconds = 0.25;

    /// <summary>Clamp duree minimum (en s) pour le sticky : pour des clips tres courts, on quantifie quand meme assez grossierement pour absorber l'ecart Photon.</summary>
    const double MinStickyQuantumSeconds = 0.5;

    /// <summary>Prefix appose par SoundAPI sur le nom du clip remplace : <c>"&lt;PackName&gt; &lt;vanillaName&gt;"</c>.</summary>
    const string NobleModPackName = "noblemod";

    static readonly StringBuilder _sb = new();

    /// <summary>
    /// Mode sticky : occurrence = <c>floor(PhotonNetwork.Time / max(clipLength, MinStickyQuantumSeconds))</c>.
    /// Quand le clip boucle (changement de bucket), R change automatiquement.
    /// </summary>
    internal static uint BuildStickyKey(SoundReplacementGroup group, AudioSource source, float clipLengthSec, int variant)
    {
        var occurrence = ComputeStickyOccurrence(clipLengthSec);
        return BuildInternal(group, source, occurrence, variant);
    }

    /// <summary>
    /// Mode non-sticky : occurrence = <c>floor(PhotonNetwork.Time / QuantumNonStickySeconds)</c> ⇒ R change toutes les ~250ms.
    /// </summary>
    internal static uint BuildNonStickyKey(SoundReplacementGroup group, AudioSource source, int variant)
    {
        var occurrence = ComputeNonStickyOccurrence();
        return BuildInternal(group, source, occurrence, variant);
    }

    /// <summary>
    /// Variante "constante" : pas de quantum temporel. Utile pour seeder un tirage one-shot (ex. choix du clip dans SoundAPI au moment du replacement initial).
    /// </summary>
    internal static uint BuildSnapshotKey(SoundReplacementGroup group, AudioSource source, int variant)
    {
        return BuildInternal(group, source, occurrenceBucket: 0, variant);
    }

    /// <summary>
    /// Surcharge low-level pour le patch SoundAPI : sans <see cref="SoundReplacementGroup"/> (pas encore choisi), on a juste
    /// les 3 tokens de matching <c>parent:object:clip</c>. La meme chaine est calculee a l'identique sur tous les clients
    /// (SoundAPI deduplique <c>(Clone)</c> / <c>(123)</c>).
    /// </summary>
    internal static uint BuildSnapshotKeyFromTokens(string parent, string objectName, string clip, AudioSource source, int variant)
    {
        var viewId = TryGetViewId(source);
        _sb.Clear();
        _sb.Append("tk|");
        _sb.Append(parent ?? string.Empty);
        _sb.Append(':');
        _sb.Append(objectName ?? string.Empty);
        _sb.Append(':');
        _sb.Append(clip ?? string.Empty);
        _sb.Append('|');
        _sb.Append(viewId);
        _sb.Append('|');
        _sb.Append(variant);
        return Fnv1a32(_sb.ToString());
    }

    static uint BuildInternal(SoundReplacementGroup group, AudioSource source, long occurrenceBucket, int variant)
    {
        var viewId = TryGetViewId(source);
        var clip = TryGetVanillaClipName(source);

        _sb.Clear();
        _sb.Append("gr|");
        if (group != null && group.Matches != null && group.Matches.Count > 0)
        {
            for (var i = 0; i < group.Matches.Count; i++)
            {
                if (i > 0)
                    _sb.Append(',');
                _sb.Append(group.Matches[i] ?? string.Empty);
            }
        }
        else
        {
            _sb.Append('*');
        }

        _sb.Append('|');
        _sb.Append(viewId);
        _sb.Append('|');
        _sb.Append(clip);
        _sb.Append('|');
        _sb.Append(occurrenceBucket);
        _sb.Append('|');
        _sb.Append(variant);
        return Fnv1a32(_sb.ToString());
    }

    static long ComputeStickyOccurrence(float clipLengthSec)
    {
        var len = Math.Max(MinStickyQuantumSeconds, clipLengthSec);
        var now = SafePhotonTime();
        return (long)Math.Floor(now / len);
    }

    static long ComputeNonStickyOccurrence()
    {
        var now = SafePhotonTime();
        return (long)Math.Floor(now / QuantumNonStickySeconds);
    }

    static double SafePhotonTime()
    {
        try
        {
            return PhotonNetwork.Time;
        }
        catch
        {
            return 0d;
        }
    }

    static int TryGetViewId(AudioSource source)
    {
        if (source == null || !source)
            return 0;
        try
        {
            var pv = source.GetComponentInParent<PhotonView>();
            return pv != null ? pv.ViewID : 0;
        }
        catch
        {
            return 0;
        }
    }

    static string TryGetVanillaClipName(AudioSource source)
    {
        try
        {
            if (source == null || !source || source.clip == null)
                return string.Empty;
            var n = source.clip.name ?? string.Empty;
            // SoundAPI prefixe le clip remplace par "<PackName> ". Retirer pour stabilite entre clients
            // (un client en pleine bascule peut avoir le vanilla, un autre le replacement deja appose).
            if (n.StartsWith(NobleModPackName + " ", StringComparison.OrdinalIgnoreCase))
                return n.Substring(NobleModPackName.Length + 1).Trim();
            return n;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>FNV-1a 32-bit : stable, sans dependance, bonne dispersion pour des chaines courtes.</summary>
    internal static uint Fnv1a32(string s)
    {
        unchecked
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            var h = offset;
            if (s == null)
                return h;
            for (var i = 0; i < s.Length; i++)
            {
                h ^= s[i];
                h *= prime;
            }

            return h;
        }
    }
}
