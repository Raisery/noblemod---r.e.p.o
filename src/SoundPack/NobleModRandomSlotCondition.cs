using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using loaforcsSoundAPI.Core.Data;
using loaforcsSoundAPI.SoundPacks.Data;
using loaforcsSoundAPI.SoundPacks.Data.Conditions;
using NobleMod;
using UnityEngine;

namespace NobleMod.SoundPack;

/// <summary>
/// Deux modes (mutuellement exclusifs via JSON) :
/// <list type="bullet">
/// <item><b>Slot</b> : <c>slot</c>, <c>count</c>, option <c>slot_weights</c> (même tableau sur chaque ligne).</item>
/// <item><b>Plage</b> : <c>random_match</c> ou <c>random_number</c> (syntaxe type
/// <see href="https://soundapi.loaforc.me/soundpack-api/value-range.html">SoundAPI value-range</see>, avec <c>..</c> ;
/// une typo <c>...</c> est ramenée à <c>..</c>). Un seul entier <c>R</c> est tiré uniformément dans <b>1..1000</b> (inclus),
/// identique pour toutes les lignes du groupe sur une même évaluation ; chaque ligne teste si <c>R</c> tombe dans sa plage.
/// Bornes ouvertes : <c>5..</c> ⇒ jusqu’à 1000 ; <c>..15</c> ⇒ à partir de 1.</item>
/// </list>
/// <c>sticky: true</c> + <c>update_every_frame</c> : même tirage par <see cref="AudioSource"/> jusqu’à fin de boucle du clip
/// (identité « logique » du clip : vanilla / préfixe pack / nom vide, pas seulement l’instance Unity).
/// </summary>
[SoundAPICondition("NobleMod:random_slot")]
public sealed class NobleModRandomSlotCondition : Condition
{
    /// <summary>Tirage <c>R</c> : inclus.</summary>
    public const int RangeRMin = 1;

    /// <summary>Tirage <c>R</c> : inclus.</summary>
    public const int RangeRMax = 1000;

    /// <summary>Après un attache complet, ignorer les changements d’empreinte trop rapprochés (SoundAPI / Unity en prélude).</summary>
    const int StickyClipChurnGraceFrames = 64;

    /// <summary>Après un attache complet, ne pas traiter <c>time</c> comme une boucle (évite faux positifs si <see cref="AudioSource.time"/> recule sans changement de clip).</summary>
    const int StickyLoopQuietFramesAfterAttach = 40;

    /// <summary>Clip au nom vide : rattacher à l’ancre du dernier nom normalisé si les longueurs sont proches (buffers SoundAPI).</summary>
    const float StickyUnnamedClipLengthSlopSeconds = 0.35f;

    sealed class StickyChoice
    {
        internal int ChosenPick;
        internal float LastTime = -1f;
        /// <summary>Identité logique du clip (pas <see cref="Object.GetInstanceID"/> : SoundAPI alterne vanilla / pack / runtime au nom vide).</summary>
        internal int ClipFingerprint;
        internal bool Initialized;
        /// <summary><see cref="Time.frameCount"/> du dernier attache « complet » (hors churn).</summary>
        internal int LastStickyAttachFrame = int.MinValue;
        /// <summary>Dernier clip nommé vu (empreinte = hash du nom seul) pour rattacher les <c>AudioClip</c> runtime au nom vide.</summary>
        internal bool HasNamedAnchor;
        internal int NamedAnchorFingerprint;
        internal float NamedAnchorLength;
    }

    static readonly ConditionalWeakTable<AudioSource, StickyChoice> StickyBySource = new();

    /// <summary>Dernier <c>R</c> loggé par source (évite un log à chaque frame si <c>R</c> est inchangé).</summary>
    static readonly ConditionalWeakTable<AudioSource, LastLoggedPickHolder> LastLoggedRBySource = new();

    sealed class LastLoggedPickHolder
    {
        internal int Value = int.MinValue;
    }

    /// <summary>Si pas de <see cref="AudioSource"/> dans le contexte SoundAPI.</summary>
    static int _lastLoggedRWithoutSource = int.MinValue;

    /// <summary>
    /// Sticky + plages : avant que la source soit dans le contexte / en lecture, <see cref="CoalescedRollThisFrame"/>
    /// changeait <c>R</c> à chaque frame. On fige un <c>R</c> par <see cref="SoundReplacementGroup"/> jusqu’à l’init sticky sur la source.
    /// </summary>
    sealed class RangeStickyWarmupHolder
    {
        internal int R;
    }

    /// <summary>Même idée que <see cref="RangeStickyWarmupHolder"/> pour le mode slot sticky.</summary>
    sealed class SlotStickyWarmupHolder
    {
        internal int SlotPick;
        internal int N;
        internal int[] WeightKey;
    }

    static readonly ConditionalWeakTable<SoundReplacementGroup, RangeStickyWarmupHolder> RangeStickyWarmupByGroup = new();
    static readonly ConditionalWeakTable<SoundReplacementGroup, SlotStickyWarmupHolder> SlotStickyWarmupByGroup = new();

    /// <summary>Index 0-based ; mode slot uniquement.</summary>
    public int Slot { get; private set; }

    /// <summary>Nombre de slots exclusifs ; mode slot uniquement.</summary>
    public int Count { get; private set; }

    /// <summary>Sticky : garde le même tirage par source jusqu’à boucle du clip.</summary>
    public bool Sticky { get; private set; }

    /// <summary>Poids par slot ; mode slot uniquement.</summary>
    public List<int> SlotWeights { get; private set; }

    /// <summary>Plage : <c>"5"</c>, <c>"5..15"</c>, <c>"5.."</c>, <c>"..15"</c> (JSON : <c>random_match</c>).</summary>
    public string RandomMatch { get; private set; }

    /// <summary>Synonyme JSON de <see cref="RandomMatch"/> (<c>random_number</c>).</summary>
    public string RandomNumber { get; private set; }

    static int _coalesceFrame = -1;
    static int _coalescePick;
    static int[] _coalesceWeightsKey;
    static int _rollCoalesceFrame = -1;
    static int _rollCoalescePick;

    string RangeExpr()
    {
        var a = RandomMatch?.Trim();
        if (string.IsNullOrEmpty(a))
            a = RandomNumber?.Trim();
        if (string.IsNullOrEmpty(a))
            return null;
        while (a.Contains("..."))
            a = a.Replace("...", "..");
        return a;
    }

    bool IsRangeMode => !string.IsNullOrEmpty(RangeExpr());

    static int PickUniform(int n)
    {
        if (n <= 1)
            return 0;
        return UnityEngine.Random.Range(0, n);
    }

    static int PickWeighted(int n, IReadOnlyList<int> weights)
    {
        if (weights == null || weights.Count < n)
            return PickUniform(n);
        int total = 0;
        for (var i = 0; i < n; i++)
            total += Mathf.Max(0, weights[i]);
        if (total <= 0)
            return PickUniform(n);
        var r = UnityEngine.Random.Range(0, total);
        for (var i = 0; i < n; i++)
        {
            r -= Mathf.Max(0, weights[i]);
            if (r < 0)
                return i;
        }

        return n - 1;
    }

    static bool WeightsKeyEquals(int[] a, IReadOnlyList<int> b, int n)
    {
        if (a == null && (b == null || b.Count < n))
            return true;
        if (a == null || b == null || a.Length != n || b.Count < n)
            return false;
        for (var i = 0; i < n; i++)
        {
            if (a[i] != b[i])
                return false;
        }

        return true;
    }

    static int[] CopyWeightsKey(IReadOnlyList<int> b, int n)
    {
        if (b == null || b.Count < n)
            return null;
        var a = new int[n];
        for (var i = 0; i < n; i++)
            a[i] = b[i];
        return a;
    }

    static int CoalescedSlotPickThisFrame(int n, IReadOnlyList<int> w)
    {
        var f = Time.frameCount;
        if (_coalesceFrame != f || !WeightsKeyEquals(_coalesceWeightsKey, w, n))
        {
            _coalesceFrame = f;
            _coalesceWeightsKey = CopyWeightsKey(w, n);
            _coalescePick = _coalesceWeightsKey != null ? PickWeighted(n, w) : PickUniform(n);
        }

        return _coalescePick;
    }

    /// <summary>Un seul <c>R</c> par frame (mode plage, hors sticky / sans source jouable).</summary>
    static int CoalescedRollThisFrame()
    {
        var f = Time.frameCount;
        if (_rollCoalesceFrame != f)
        {
            _rollCoalesceFrame = f;
            _rollCoalescePick = UnityEngine.Random.Range(RangeRMin, RangeRMax + 1);
        }

        return _rollCoalescePick;
    }

    static int PickR1To1000() => UnityEngine.Random.Range(RangeRMin, RangeRMax + 1);

    SoundReplacementGroup GetReplacementGroup()
    {
        if (Parent is not SoundInstance si)
            return null;
        return si.Parent;
    }

    static int GetStickyRangeWarmupPick(SoundReplacementGroup g)
    {
        if (g == null)
            return CoalescedRollThisFrame();
        if (!RangeStickyWarmupByGroup.TryGetValue(g, out var h))
        {
            h = new RangeStickyWarmupHolder { R = PickR1To1000() };
            RangeStickyWarmupByGroup.Add(g, h);
        }

        return h.R;
    }

    static void RefreshStickyRangeWarmup(SoundReplacementGroup g)
    {
        if (g == null)
            return;
        if (!RangeStickyWarmupByGroup.TryGetValue(g, out var h))
        {
            h = new RangeStickyWarmupHolder { R = PickR1To1000() };
            RangeStickyWarmupByGroup.Add(g, h);
            return;
        }

        h.R = PickR1To1000();
    }

    static int GetStickySlotWarmupPick(SoundReplacementGroup g, int n, IReadOnlyList<int> weights)
    {
        if (g == null)
            return CoalescedSlotPickThisFrame(n, weights);
        if (SlotStickyWarmupByGroup.TryGetValue(g, out var h))
        {
            if (h.N != n || !WeightsKeyEquals(h.WeightKey, weights, n))
            {
                h.N = n;
                h.WeightKey = CopyWeightsKey(weights, n);
                h.SlotPick = h.WeightKey != null ? PickWeighted(n, weights) : PickUniform(n);
            }

            return h.SlotPick;
        }

        h = new SlotStickyWarmupHolder();
        h.N = n;
        h.WeightKey = CopyWeightsKey(weights, n);
        h.SlotPick = h.WeightKey != null ? PickWeighted(n, weights) : PickUniform(n);
        SlotStickyWarmupByGroup.Add(g, h);
        return h.SlotPick;
    }

    static void RefreshStickySlotWarmup(SoundReplacementGroup g, int n, IReadOnlyList<int> weights)
    {
        if (g == null)
            return;
        if (!SlotStickyWarmupByGroup.TryGetValue(g, out var h))
        {
            GetStickySlotWarmupPick(g, n, weights);
            return;
        }

        h.N = n;
        h.WeightKey = CopyWeightsKey(weights, n);
        h.SlotPick = h.WeightKey != null ? PickWeighted(n, weights) : PickUniform(n);
    }

    /// <summary>Teste si <paramref name="value"/> (typiquement <c>R</c>) est dans la plage ; bornes ouvertes clampées à 1..1000.</summary>
    internal static bool ValueMatchesRange(int value, string expr)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return false;
        var s = expr.Trim();
        while (s.Contains("..."))
            s = s.Replace("...", "..");
        if (!s.Contains(".."))
        {
            if (!int.TryParse(s, out var v))
                return false;
            return value == v;
        }

        var parts = s.Split(new[] { ".." }, StringSplitOptions.None);
        if (parts.Length != 2)
            return false;
        var left = parts[0].Trim();
        var right = parts[1].Trim();
        var hasLo = left.Length > 0;
        var hasHi = right.Length > 0;
        if (!hasLo && !hasHi)
            return false;
        if (hasLo && hasHi)
        {
            if (!int.TryParse(left, out var lo) || !int.TryParse(right, out var hiBoth))
                return false;
            return value >= lo && value <= hiBoth;
        }

        if (hasLo)
        {
            if (!int.TryParse(left, out var loOpen))
                return false;
            return value >= loOpen && value <= RangeRMax;
        }

        if (!int.TryParse(right, out var hiOnly))
            return false;
        return value >= RangeRMin && value <= hiOnly;
    }

    /// <summary>Analyse syntaxique pour la validation.</summary>
    internal static bool TryGetRangeMaxInclusive(string expr, out int maxInclusive, out bool upperOpen)
    {
        maxInclusive = -1;
        upperOpen = false;
        if (string.IsNullOrWhiteSpace(expr))
            return false;
        var s = expr.Trim();
        while (s.Contains("..."))
            s = s.Replace("...", "..");
        if (!s.Contains(".."))
        {
            if (!int.TryParse(s, out var v))
                return false;
            maxInclusive = v;
            return true;
        }

        var parts = s.Split(new[] { ".." }, StringSplitOptions.None);
        if (parts.Length != 2)
            return false;
        var left = parts[0].Trim();
        var right = parts[1].Trim();
        var hasLo = left.Length > 0;
        var hasHi = right.Length > 0;
        if (!hasLo && !hasHi)
            return false;
        if (hasLo && hasHi)
        {
            if (!int.TryParse(right, out var hi))
                return false;
            maxInclusive = hi;
            return true;
        }

        if (hasLo && !hasHi)
        {
            upperOpen = true;
            return true;
        }

        if (!int.TryParse(right, out var hi2))
            return false;
        maxInclusive = hi2;
        return true;
    }

    static void TryLogRangePickIfChanged(AudioSource src, int pick, string soundFile)
    {
        if (src != null && src)
        {
            if (!LastLoggedRBySource.TryGetValue(src, out var holder))
            {
                holder = new LastLoggedPickHolder { Value = pick };
                LastLoggedRBySource.Add(src, holder);
                Plugin.Log.LogInfo($"[NobleMod] R = {pick} -> son {soundFile}");
                return;
            }

            if (holder.Value == pick)
                return;
            holder.Value = pick;
            Plugin.Log.LogInfo($"[NobleMod] R = {pick} -> son {soundFile}");
            return;
        }

        if (_lastLoggedRWithoutSource == pick)
            return;
        _lastLoggedRWithoutSource = pick;
        Plugin.Log.LogInfo($"[NobleMod] R = {pick} -> son {soundFile}");
    }

    void TryLogSoundPickEachMatch(bool matched)
    {
        if (!matched)
            return;
        if (ModConfig.LogSoundPickEachMatch == null || !ModConfig.LogSoundPickEachMatch.Value)
            return;
        var label = TryResolvePickSoundLabel();
        var src = NobleModSoundEvalContext.CurrentAudioSource;
        var srcHint = src != null && src ? $"AudioSource #{src.GetInstanceID()}" : "sans source";
        if (!string.IsNullOrEmpty(label))
            Plugin.Log.LogInfo($"[NobleMod] Son selectionne : {label} ({srcHint})");
        else
            Plugin.Log.LogInfo(
                $"[NobleMod] Son selectionne : (libelle introuvable, Parent={(Parent == null ? "null" : Parent.GetType().FullName)}) ({srcHint})");
    }

    /// <summary>Résout le nom de fichier <c>sound</c> (SoundAPI peut ne pas exposer <see cref="SoundInstance"/> comme parent direct).</summary>
    string TryResolvePickSoundLabel()
    {
        if (Parent is SoundInstance si && !string.IsNullOrEmpty(si.Sound))
            return si.Sound;
        var p = Parent;
        if (p == null)
            return null;
        var t = p.GetType();
        foreach (var propName in new[] { "Sound", "sound", "SoundFile", "soundFile", "File", "file" })
        {
            var pi = t.GetProperty(
                propName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (pi?.GetValue(p) is string s && !string.IsNullOrEmpty(s))
                return s;
        }

        foreach (var fieldName in new[] { "Sound", "sound", "soundFile", "_sound" })
        {
            var fi = t.GetField(
                fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (fi?.GetValue(p) is string s2 && !string.IsNullOrEmpty(s2))
                return s2;
        }

        return null;
    }

    static void TryLogStickyTransition(AudioSource src, string modeLabel, bool attach, string detail)
    {
        if (ModConfig.LogStickyAttachDetach == null || !ModConfig.LogStickyAttachDetach.Value)
            return;
        if (src == null || !src)
            return;
        var clipInfo = src.clip != null ? $"{src.clip.name}#{src.clip.GetInstanceID()}" : "null";
        var verb = attach ? "branché" : "débranché";
        Plugin.Log.LogInfo($"[NobleMod] Sticky {modeLabel}: {verb} (AudioSource #{src.GetInstanceID()}, clip={clipInfo}) — {detail}");
    }

    /// <summary>
    /// Même « morceau » pour le sticky alors que SoundAPI assigne plusieurs <see cref="AudioClip"/> (vanilla,
    /// copie préfixée <c>NobleMod </c>, runtime au nom vide, etc.). Nom non vide ⇒ empreinte = hash du nom seul
    /// (évite les écarts de longueur entre copies) ; nom vide ⇒ ancrage sur le dernier nom vu si longueur proche.
    /// </summary>
    static int GetStickyClipFingerprint(AudioClip clip, StickyChoice st)
    {
        if (clip == null)
            return 0;
        var name = clip.name ?? string.Empty;
        if (name.StartsWith("NobleMod ", StringComparison.OrdinalIgnoreCase))
            name = name.Substring("NobleMod ".Length).TrimStart();
        if (!string.IsNullOrEmpty(name))
        {
            var fp = StringComparer.OrdinalIgnoreCase.GetHashCode(name);
            st.HasNamedAnchor = true;
            st.NamedAnchorFingerprint = fp;
            st.NamedAnchorLength = clip.length;
            return fp;
        }

        unchecked
        {
            var lenMs = Mathf.RoundToInt(clip.length * 1000f);
            var h = lenMs ^ (clip.frequency * 397) ^ (clip.channels * 7919);
            if (st.HasNamedAnchor
                && Mathf.Abs(clip.length - st.NamedAnchorLength) <= StickyUnnamedClipLengthSlopSeconds)
                return st.NamedAnchorFingerprint;
            return h;
        }
    }

    bool EvaluateRangeMode(string expr, IContext context)
    {
        int pick;
        if (!Sticky)
            pick = CoalescedRollThisFrame();
        else
        {
            var g = GetReplacementGroup();
            var src = NobleModSoundEvalContext.CurrentAudioSource;
            if (src == null || !src || src.clip == null)
                pick = GetStickyRangeWarmupPick(g);
            else if (!src.isPlaying
                     && StickyBySource.TryGetValue(src, out var stPaused)
                     && stPaused.Initialized
                     && stPaused.ClipFingerprint == GetStickyClipFingerprint(src.clip, stPaused))
            {
                // Ne pas utiliser CoalescedRollThisFrame ici : un frame où isPlaying est faux
                // ferait changer R et SoundAPI couperait / re-swaperait le clip avant la fin.
                pick = stPaused.ChosenPick;
            }
            else if (!src.isPlaying)
                pick = GetStickyRangeWarmupPick(g);
            else
            {
                if (!StickyBySource.TryGetValue(src, out var st))
                {
                    st = new StickyChoice();
                    StickyBySource.Add(src, st);
                }

                var t = src.time;
                var len = src.clip.length;
                var fp = GetStickyClipFingerprint(src.clip, st);
                var logSticky = ModConfig.LogStickyAttachDetach != null && ModConfig.LogStickyAttachDetach.Value;

                if (!st.Initialized || st.ClipFingerprint != fp)
                {
                    var wasInit = st.Initialized;
                    var clipChanged = wasInit && st.ClipFingerprint != fp;
                    var sinceAttach = st.LastStickyAttachFrame == int.MinValue
                        ? int.MaxValue
                        : Time.frameCount - st.LastStickyAttachFrame;
                    var rapidFingerprintChurn = clipChanged && sinceAttach >= 0 && sinceAttach < StickyClipChurnGraceFrames;

                    if (rapidFingerprintChurn)
                    {
                        // Empreinte logique instable sur peu de frames : garder R, pas de logs.
                        st.ClipFingerprint = fp;
                        st.LastTime = t;
                    }
                    else
                    {
                        var isNewClip = clipChanged;
                        if (isNewClip)
                        {
                            st.HasNamedAnchor = false;
                            fp = GetStickyClipFingerprint(src.clip, st);
                        }

                        if (logSticky && isNewClip)
                            TryLogStickyTransition(src, "plage", attach: false, detail: "changement de clip (fin du cycle précédent)");
                        if (isNewClip)
                            RefreshStickyRangeWarmup(g);
                        st.Initialized = true;
                        st.ClipFingerprint = fp;
                        st.ChosenPick = GetStickyRangeWarmupPick(g);
                        st.LastTime = t;
                        st.LastStickyAttachFrame = Time.frameCount;
                        if (logSticky)
                        {
                            if (!wasInit)
                                TryLogStickyTransition(src, "plage", attach: true, detail: "premier attache sur la source");
                            else if (isNewClip)
                                TryLogStickyTransition(src, "plage", attach: true, detail: "nouveau clip");
                        }
                    }
                }
                else
                {
                    var sinceFullAttach = st.LastStickyAttachFrame == int.MinValue
                        ? int.MaxValue
                        : Time.frameCount - st.LastStickyAttachFrame;
                    var loopQuiet = sinceFullAttach >= 0 && sinceFullAttach < StickyLoopQuietFramesAfterAttach;
                    // jumpedBack seul : exiger aussi t proche du début (sinon glitch / resync milieu de clip).
                    var jumpedBack = !loopQuiet
                        && st.LastTime > 0.08f
                        && st.LastTime - t > 0.18f
                        && t < 0.4f;
                    var endToStart = !loopQuiet
                        && len > 0.1f
                        && st.LastTime > len * 0.88f
                        && t < len * 0.22f;
                    if (jumpedBack || endToStart)
                    {
                        if (logSticky)
                            TryLogStickyTransition(src, "plage", attach: false, detail: "boucle détectée (nouveau tirage R)");
                        st.ChosenPick = PickR1To1000();
                        if (g != null && RangeStickyWarmupByGroup.TryGetValue(g, out var hw))
                            hw.R = st.ChosenPick;
                        if (logSticky)
                            TryLogStickyTransition(src, "plage", attach: true, detail: $"nouveau R={st.ChosenPick}");
                    }

                    st.LastTime = t;
                }

                pick = st.ChosenPick;
            }
        }

        var matched = ValueMatchesRange(pick, expr);
        if (matched && ModConfig.LogRandomSlotRange != null && ModConfig.LogRandomSlotRange.Value && Parent is SoundInstance si && !string.IsNullOrEmpty(si.Sound))
            TryLogRangePickIfChanged(NobleModSoundEvalContext.CurrentAudioSource, pick, si.Sound);
        TryLogSoundPickEachMatch(matched);

        return matched;
    }

    public override bool Evaluate(IContext context)
    {
        var expr = RangeExpr();
        if (!string.IsNullOrEmpty(expr))
            return EvaluateRangeMode(expr, context);

        var n = Count < 1 ? 4 : Count;
        n = Mathf.Max(1, n);
        var s = Mathf.Clamp(Slot, 0, n - 1);

        if (!Sticky)
        {
            var pick = SlotWeights is { Count: var wc } && wc >= n
                ? PickWeighted(n, SlotWeights)
                : PickUniform(n);
            var slotMatch = pick == s;
            TryLogSoundPickEachMatch(slotMatch);
            return slotMatch;
        }

        var g = GetReplacementGroup();
        var src = NobleModSoundEvalContext.CurrentAudioSource;
        if (src == null || !src || src.clip == null)
        {
            var m0 = GetStickySlotWarmupPick(g, n, SlotWeights) == s;
            TryLogSoundPickEachMatch(m0);
            return m0;
        }

        if (!src.isPlaying
            && StickyBySource.TryGetValue(src, out var stPauseSlot)
            && stPauseSlot.Initialized
            && stPauseSlot.ClipFingerprint == GetStickyClipFingerprint(src.clip, stPauseSlot))
        {
            var m1 = stPauseSlot.ChosenPick == s;
            TryLogSoundPickEachMatch(m1);
            return m1;
        }

        if (!src.isPlaying)
        {
            var m2 = GetStickySlotWarmupPick(g, n, SlotWeights) == s;
            TryLogSoundPickEachMatch(m2);
            return m2;
        }

        if (!StickyBySource.TryGetValue(src, out var st))
        {
            st = new StickyChoice();
            StickyBySource.Add(src, st);
        }

        var t = src.time;
        var len = src.clip.length;
        var fp = GetStickyClipFingerprint(src.clip, st);
        var logSticky = ModConfig.LogStickyAttachDetach != null && ModConfig.LogStickyAttachDetach.Value;

        if (!st.Initialized || st.ClipFingerprint != fp)
        {
            var wasInit = st.Initialized;
            var clipChanged = wasInit && st.ClipFingerprint != fp;
            var sinceAttach = st.LastStickyAttachFrame == int.MinValue
                ? int.MaxValue
                : Time.frameCount - st.LastStickyAttachFrame;
            var rapidFingerprintChurn = clipChanged && sinceAttach >= 0 && sinceAttach < StickyClipChurnGraceFrames;

            if (rapidFingerprintChurn)
            {
                st.ClipFingerprint = fp;
                st.LastTime = t;
            }
            else
            {
                var isNewClip = clipChanged;
                if (isNewClip)
                {
                    st.HasNamedAnchor = false;
                    fp = GetStickyClipFingerprint(src.clip, st);
                }

                if (logSticky && isNewClip)
                    TryLogStickyTransition(src, "slot", attach: false, detail: "changement de clip (fin du cycle précédent)");
                if (isNewClip)
                    RefreshStickySlotWarmup(g, n, SlotWeights);
                st.Initialized = true;
                st.ClipFingerprint = fp;
                st.ChosenPick = GetStickySlotWarmupPick(g, n, SlotWeights);
                st.LastTime = t;
                st.LastStickyAttachFrame = Time.frameCount;
                if (logSticky)
                {
                    if (!wasInit)
                        TryLogStickyTransition(src, "slot", attach: true, detail: "premier attache sur la source");
                    else if (isNewClip)
                        TryLogStickyTransition(src, "slot", attach: true, detail: "nouveau clip");
                }
            }

            var m3 = st.ChosenPick == s;
            TryLogSoundPickEachMatch(m3);
            return m3;
        }

        var sinceFullAttachSlot = st.LastStickyAttachFrame == int.MinValue
            ? int.MaxValue
            : Time.frameCount - st.LastStickyAttachFrame;
        var loopQuietSlot = sinceFullAttachSlot >= 0 && sinceFullAttachSlot < StickyLoopQuietFramesAfterAttach;
        var jumpedBack = !loopQuietSlot
            && st.LastTime > 0.08f
            && st.LastTime - t > 0.18f
            && t < 0.4f;
        var endToStart = !loopQuietSlot
            && len > 0.1f
            && st.LastTime > len * 0.88f
            && t < len * 0.22f;
        if (jumpedBack || endToStart)
        {
            if (logSticky)
                TryLogStickyTransition(src, "slot", attach: false, detail: "boucle détectée (nouveau tirage slot)");
            st.ChosenPick = SlotWeights is { Count: var wc3 } && wc3 >= n
                ? PickWeighted(n, SlotWeights)
                : PickUniform(n);
            if (g != null && SlotStickyWarmupByGroup.TryGetValue(g, out var sw))
                sw.SlotPick = st.ChosenPick;
            if (logSticky)
                TryLogStickyTransition(src, "slot", attach: true, detail: $"nouveau slot={st.ChosenPick}");
        }

        st.LastTime = t;
        var m4 = st.ChosenPick == s;
        TryLogSoundPickEachMatch(m4);
        return m4;
    }

    static bool GroupHasSlotModeNoble(SoundReplacementGroup g)
    {
        foreach (var sound in g.Sounds)
        {
            if (sound.Condition is NobleModRandomSlotCondition c && !c.IsRangeMode)
                return true;
        }

        return false;
    }

    static bool GroupHasRangeModeNoble(SoundReplacementGroup g)
    {
        foreach (var sound in g.Sounds)
        {
            if (sound.Condition is NobleModRandomSlotCondition c && c.IsRangeMode)
                return true;
        }

        return false;
    }

    static void WarnIfRangeNeverMatches(string expr, List<IValidatable.ValidationResult> results)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return;
        var e = expr.Trim();
        while (e.Contains("..."))
            e = e.Replace("...", "..");
        if (!e.Contains(".."))
        {
            if (int.TryParse(e, out var v) && (v < RangeRMin || v > RangeRMax))
            {
                results.Add(new IValidatable.ValidationResult(
                    IValidatable.ResultType.WARN,
                    $"NobleMod:random_slot : '{expr}' ne peut jamais matcher (R est toujours entre {RangeRMin} et {RangeRMax})."
                ));
            }

            return;
        }

        var parts = e.Split(new[] { ".." }, StringSplitOptions.None);
        if (parts.Length != 2)
            return;
        if (!int.TryParse(parts[0].Trim(), out var lo) || !int.TryParse(parts[1].Trim(), out var hi))
            return;
        if (hi < RangeRMin || lo > RangeRMax)
        {
            results.Add(new IValidatable.ValidationResult(
                IValidatable.ResultType.WARN,
                $"NobleMod:random_slot : la plage '{expr}' ne croise pas [{RangeRMin}..{RangeRMax}] (R ne matchera jamais)."
            ));
        }
    }

    public override List<IValidatable.ValidationResult> Validate()
    {
        var results = base.Validate();
        if (Constant == true)
        {
            results.Add(new IValidatable.ValidationResult(
                IValidatable.ResultType.FAIL,
                "NobleMod:random_slot ne doit pas être utilisé avec constant: true (le tirage serait figé au chargement)."
            ));
        }

        if (IsRangeMode)
        {
            var expr = RangeExpr();
            if (!TryGetRangeMaxInclusive(expr, out _, out _))
            {
                results.Add(new IValidatable.ValidationResult(
                    IValidatable.ResultType.FAIL,
                    $"NobleMod:random_slot : random_match / random_number invalide : '{expr}'."
                ));
            }
            else
            {
                WarnIfRangeNeverMatches(expr, results);
            }

            var g = GetReplacementGroup();
            if (g != null && GroupHasSlotModeNoble(g) && GroupHasRangeModeNoble(g))
            {
                results.Add(new IValidatable.ValidationResult(
                    IValidatable.ResultType.FAIL,
                    "NobleMod:random_slot : ne pas melanger dans le meme groupe des lignes mode plage (random_match) et mode slot (slot/count)."
                ));
            }

            if (Sticky)
            {
                results.Add(new IValidatable.ValidationResult(
                    IValidatable.ResultType.WARN,
                    "NobleMod:random_slot (plage) avec sticky: true : prevu pour update_every_frame + patch NobleMod sur SoundAPI.Update."
                ));
            }

            return results;
        }

        var n = Count < 1 ? 4 : Count;
        if (Count < 1)
        {
            results.Add(new IValidatable.ValidationResult(
                IValidatable.ResultType.WARN,
                "NobleMod:random_slot : count absent ou inferieur a 1, defaut 4 utilise pour la validation."
            ));
        }

        if (Slot < 0 || Slot >= n)
        {
            results.Add(new IValidatable.ValidationResult(
                IValidatable.ResultType.FAIL,
                $"NobleMod:random_slot : slot ({Slot}) invalide pour count={n} (attendu 0 .. {n - 1})."
            ));
        }

        if (SlotWeights != null && SlotWeights.Count > 0)
        {
            if (SlotWeights.Count != n)
            {
                results.Add(new IValidatable.ValidationResult(
                    IValidatable.ResultType.FAIL,
                    $"NobleMod:random_slot : slot_weights doit avoir exactement {n} entrees (count={n}), trouve {SlotWeights.Count}."
                ));
            }
            else
            {
                for (var i = 0; i < n; i++)
                {
                    if (SlotWeights[i] < 0)
                    {
                        results.Add(new IValidatable.ValidationResult(
                            IValidatable.ResultType.FAIL,
                            $"NobleMod:random_slot : slot_weights[{i}] ne peut pas etre negatif."
                        ));
                        break;
                    }
                }

                var sum = 0;
                for (var i = 0; i < n; i++)
                    sum += Mathf.Max(0, SlotWeights[i]);
                if (sum <= 0)
                {
                    results.Add(new IValidatable.ValidationResult(
                        IValidatable.ResultType.FAIL,
                        "NobleMod:random_slot : la somme des slot_weights doit etre strictement positive."
                    ));
                }
            }
        }

        var g2 = GetReplacementGroup();
        if (g2 != null && GroupHasSlotModeNoble(g2) && GroupHasRangeModeNoble(g2))
        {
            results.Add(new IValidatable.ValidationResult(
                IValidatable.ResultType.FAIL,
                "NobleMod:random_slot : ne pas melanger dans le meme groupe des lignes mode plage et mode slot."
            ));
        }

        if (Sticky)
        {
            results.Add(new IValidatable.ValidationResult(
                IValidatable.ResultType.WARN,
                "NobleMod:random_slot avec sticky: true : prevu pour update_every_frame + patch NobleMod sur SoundAPI.Update (fin de boucle detectee via AudioSource.time)."
            ));
        }

        return results;
    }
}
