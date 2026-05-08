using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using SoundResolve = NobleMod.SoundReplacementResolve;

namespace NobleMod.Patches;

/// <summary>
/// Remplacement de clips et découverte des noms vanilla pour tout le jeu (pas limité à un ennemi).
/// Filtre optionnel : <see cref="ModConfig.AudioSourceHierarchyFilter"/> (vide = toutes les sources).
/// </summary>
[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), typeof(AudioClip))]
internal static class CustomSoundPlayOneShotPatch
{
    private static bool _loggedRuntimeError;
    private static readonly Dictionary<string, int> DebugEntryCounts = new Dictionary<string, int>();

    [HarmonyPrefix]
    private static void BeforePlayOneShot(AudioSource __instance, ref AudioClip clip)
    {
        LogHookEntry(
            "AudioSource.PlayOneShot(AudioClip)",
            __instance,
            clip
        );
        SafeTryReplace(__instance, ref clip);
    }

    internal static void TryReplace(AudioSource source, ref AudioClip clip)
    {
        if (!ModConfig.EnableCustomSounds.Value)
            return;

        if (source == null || clip == null)
            return;

        if (SoundBank.IsManagedClip(clip))
        {
            SoundBank.TryRerollManagedClipOnSource(source, ref clip);
            return;
        }

        if (!IsAudioSourceInScope(source))
            return;

        SoundBank.RememberVanillaPlaybackContext(source, clip);

        var resolve = SoundBank.TryResolveReplacement(clip.name, clip, out var replacement);
        if (resolve == SoundResolve.NoMatch)
        {
            SoundBank.TrackDiscoveredClip(clip.name);
            if (ModConfig.LogUnknownVanillaClipNamesOnce.Value && SoundBank.ShouldLogUnknownClip(clip.name))
                Plugin.Log.LogInfo($"No mapping for vanilla clip: '{clip.name}'");
            return;
        }

        if (resolve == SoundResolve.KeepVanilla)
            return;

        if (ModConfig.LogReplacements.Value)
            Plugin.Log.LogInfo($"Replace '{clip.name}' -> '{replacement.name}'");

        clip = replacement;
    }

    internal static void SafeTryReplace(AudioSource source, ref AudioClip clip)
    {
        try
        {
            TryReplace(source, ref clip);
        }
        catch (Exception e)
        {
            if (_loggedRuntimeError)
                return;

            _loggedRuntimeError = true;
            Plugin.Log.LogError($"Runtime error in audio replacement patch: {e}");
        }
    }

    /// <summary>
    /// Si le filtre est vide : toutes les <see cref="AudioSource"/> sont concernées.
    /// Sinon : au moins un transform (la source ou un ancêtre) doit contenir la sous-chaîne (insensible à la casse).
    /// </summary>
    internal static bool IsAudioSourceInScope(AudioSource source) =>
        SoundBank.IsAudioSourceInFilterScope(source);

    internal static void LogHookEntry(string hookName, AudioSource source, AudioClip clip)
    {
        if (!ModConfig.DebugLogHookEntrypoints.Value)
            return;
        if (ShouldSuppressDebugEntry(hookName, source, clip))
            return;

        var maxPerMethod = Mathf.Max(0, ModConfig.DebugLogHookEntrypointsPerMethod.Value);
        if (!DebugEntryCounts.TryGetValue(hookName, out var count))
            count = 0;
        if (maxPerMethod > 0 && count >= maxPerMethod)
            return;

        DebugEntryCounts[hookName] = count + 1;

        var srcName = source != null ? source.name : "<null-source>";
        var clipName = clip != null ? clip.name : "<null-clip>";
        Plugin.Log.LogInfo($"[HookEntry] {hookName} src='{srcName}' clip='{clipName}'");
    }

    internal static bool ShouldSuppressDebugEntry(string hookName, AudioSource source, AudioClip clip)
    {
        if (!ModConfig.DebugSuppressMenuSpam.Value)
            return false;

        var srcName = source != null ? source.name : string.Empty;
        var clipName = clip != null ? clip.name : string.Empty;
        var combined = $"{hookName} {srcName} {clipName}".ToLowerInvariant();

        if (combined.Contains("menu") || combined.Contains("ui "))
            return true;

        if (hookName.IndexOf("PlayHelper", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }
}

[HarmonyPatch(typeof(AudioSource), nameof(AudioSource.PlayOneShot), typeof(AudioClip), typeof(float))]
internal static class CustomSoundPlayOneShotWithVolumePatch
{
    [HarmonyPrefix]
    private static void BeforePlayOneShot(AudioSource __instance, ref AudioClip clip, float volumeScale)
    {
        CustomSoundPlayOneShotPatch.LogHookEntry(
            "AudioSource.PlayOneShot(AudioClip,float)",
            __instance,
            clip
        );
        CustomSoundPlayOneShotPatch.SafeTryReplace(__instance, ref clip);
    }
}

[HarmonyPatch]
internal static class CustomSoundAudioSourcePlayPatch
{
    static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(AudioSource), nameof(AudioSource.Play), Type.EmptyTypes);
    }

    [HarmonyPrefix]
    private static void BeforePlay(AudioSource __instance)
    {
        CustomSoundPlayOneShotPatch.LogHookEntry(
            "AudioSource.Play()",
            __instance,
            __instance != null ? __instance.clip : null
        );
        if (__instance == null || __instance.clip == null)
            return;

        var clip = __instance.clip;
        CustomSoundPlayOneShotPatch.SafeTryReplace(__instance, ref clip);
        __instance.clip = clip;
    }
}

[HarmonyPatch]
internal static class CustomSoundGameSoundPatch
{
    private static bool _loggedRuntimeError;
    private static readonly Dictionary<string, int> DebugEntryCounts = new Dictionary<string, int>();

    static IEnumerable<MethodBase> TargetMethods()
    {
        var soundType = AccessTools.TypeByName("Sound");
        if (soundType == null)
            return Enumerable.Empty<MethodBase>();

        return AccessTools
            .GetDeclaredMethods(soundType)
            .Where(m => m.Name == "Play" || m.Name == "PlayLoop" || m.Name == "PlayOneShot");
    }

    [HarmonyPrefix]
    private static void BeforeSoundMethod(object __instance, MethodBase __originalMethod)
    {
        try
        {
            if (!ModConfig.EnableCustomSounds.Value)
                return;

            if (__instance == null)
                return;

            var tr = Traverse.Create(__instance);
            var source = tr.Property("Source").GetValue<AudioSource>() ?? tr.Field("Source").GetValue<AudioSource>();
            LogHookEntry(__originalMethod.Name, source);
            if (source == null || source.clip == null)
                return;

            if (SoundBank.IsManagedClip(source.clip))
            {
                var mc = source.clip;
                if (!CustomSoundPlayOneShotPatch.IsAudioSourceInScope(source))
                    return;
                if (SoundBank.TryRerollManagedClipOnSource(source, ref mc))
                {
                    source.clip = mc;
                    if (SoundBank.IsManagedClip(mc))
                        tr.Field("LoopClip").SetValue(mc);
                    else
                        SyncSoundLoopClipIfStaleCustom(tr, mc);
                }

                return;
            }

            if (!CustomSoundPlayOneShotPatch.IsAudioSourceInScope(source))
                return;

            SoundBank.RememberVanillaPlaybackContext(source, source.clip);

            var clip = source.clip;
            var resolve = SoundBank.TryResolveReplacement(clip.name, clip, out var replacement);
            if (resolve == SoundResolve.NoMatch)
            {
                SoundBank.TrackDiscoveredClip(clip.name);
                if (ModConfig.LogUnknownVanillaClipNamesOnce.Value && SoundBank.ShouldLogUnknownClip(clip.name))
                    Plugin.Log.LogInfo($"[Sound.{__originalMethod.Name}] No mapping for vanilla clip: '{clip.name}'");
                SyncSoundLoopClipIfStaleCustom(tr, clip);
                return;
            }

            if (resolve == SoundResolve.KeepVanilla)
            {
                // PlayLoop utilise souvent LoopClip pour la boucle : si on ne le réaligne pas, un ancien
                // remplacement custom reste en boucle alors que clip est redevenu vanilla (tirage pondere).
                SyncSoundLoopClipIfStaleCustom(tr, clip);
                return;
            }

            if (ModConfig.LogReplacements.Value)
                Plugin.Log.LogInfo($"[Sound.{__originalMethod.Name}] Replace '{clip.name}' -> '{replacement.name}'");

            source.clip = replacement;
            tr.Field("LoopClip").SetValue(replacement);
        }
        catch (Exception e)
        {
            if (_loggedRuntimeError)
                return;

            _loggedRuntimeError = true;
            Plugin.Log.LogError($"Runtime error in Sound patch: {e}");
        }
    }

    /// <summary>
    /// <c>PlayLoop</c> s'appuie souvent sur <c>LoopClip</c> pour la partie boucle. Si un tour precedent
    /// y a mis un de nos remplacements, il faut le remettre sur le clip vanilla courant quand le tirage
    /// dit vanilla (ou pas de mapping), sinon le custom continue en boucle indefiniment.
    /// </summary>
    private static void SyncSoundLoopClipIfStaleCustom(Traverse tr, AudioClip vanillaClip)
    {
        if (vanillaClip == null)
            return;
        try
        {
            var loopClip = tr.Field("LoopClip").GetValue<AudioClip>();
            if (loopClip == null || SoundBank.IsManagedClip(loopClip))
                tr.Field("LoopClip").SetValue(vanillaClip);
        }
        catch
        {
            // Sound sans LoopClip selon version
        }
    }

    private static void LogHookEntry(string methodName, AudioSource source)
    {
        if (!ModConfig.DebugLogHookEntrypoints.Value)
            return;

        var hookName = $"Sound.{methodName}";
        var maxPerMethod = Mathf.Max(0, ModConfig.DebugLogHookEntrypointsPerMethod.Value);
        if (!DebugEntryCounts.TryGetValue(hookName, out var count))
            count = 0;
        if (maxPerMethod > 0 && count >= maxPerMethod)
            return;

        DebugEntryCounts[hookName] = count + 1;

        var srcName = source != null ? source.name : "<null-source>";
        var clipName = source != null && source.clip != null ? source.clip.name : "<null-clip>";
        if (CustomSoundPlayOneShotPatch.ShouldSuppressDebugEntry(hookName, source, source != null ? source.clip : null))
            return;
        Plugin.Log.LogInfo($"[HookEntry] {hookName} src='{srcName}' clip='{clipName}'");
    }
}

[HarmonyPatch]
internal static class CustomSoundAudioSourcePlayAnyPatch
{
    private static bool _loggedRuntimeError;
    private static readonly Dictionary<string, int> DebugEntryCounts = new Dictionary<string, int>();

    static IEnumerable<MethodBase> TargetMethods()
    {
        // Evite un double tirage pondere sur Play()/PlayOneShot deja patches ailleurs.
        return AccessTools
            .GetDeclaredMethods(typeof(AudioSource))
            .Where(m => m.Name.StartsWith("Play", StringComparison.Ordinal))
            .Where(m => !IsHandledByDedicatedAudioSourcePlayPatch(m));
    }

    private static bool IsHandledByDedicatedAudioSourcePlayPatch(MethodInfo m)
    {
        if (m.Name == nameof(AudioSource.Play) && m.GetParameters().Length == 0)
            return true;
        if (m.Name == nameof(AudioSource.PlayOneShot))
        {
            var p = m.GetParameters();
            if (p.Length == 1 && p[0].ParameterType == typeof(AudioClip))
                return true;
            if (p.Length == 2 && p[0].ParameterType == typeof(AudioClip) && p[1].ParameterType == typeof(float))
                return true;
        }

        return false;
    }

    [HarmonyPrefix]
    private static void BeforeAnyPlay(MethodBase __originalMethod, AudioSource __instance, object[] __args)
    {
        try
        {
            if (!ModConfig.EnableCustomSounds.Value)
                return;

            AudioClip argClip = null;
            if (__args != null)
            {
                foreach (var arg in __args)
                {
                    if (arg is AudioClip clipArg)
                    {
                        argClip = clipArg;
                        break;
                    }
                }
            }

            var sourceClip = __instance != null ? __instance.clip : null;
            var currentClip = argClip ?? sourceClip;

            LogHookEntry($"AudioSource.{__originalMethod.Name}", __instance, currentClip);

            if (__instance == null || currentClip == null)
                return;

            if (SoundBank.IsManagedClip(currentClip))
            {
                var mc = currentClip;
                if (!CustomSoundPlayOneShotPatch.IsAudioSourceInScope(__instance))
                    return;
                if (SoundBank.TryRerollManagedClipOnSource(__instance, ref mc))
                {
                    if (argClip != null && __args != null)
                    {
                        for (var i = 0; i < __args.Length; i++)
                        {
                            if (__args[i] is AudioClip)
                            {
                                __args[i] = mc;
                                break;
                            }
                        }
                    }
                    else
                        __instance.clip = mc;
                }

                return;
            }

            if (!CustomSoundPlayOneShotPatch.IsAudioSourceInScope(__instance))
                return;

            SoundBank.RememberVanillaPlaybackContext(__instance, currentClip);

            var resolve = SoundBank.TryResolveReplacement(currentClip.name, currentClip, out var replacement);
            if (resolve == SoundResolve.NoMatch)
            {
                SoundBank.TrackDiscoveredClip(currentClip.name);
                if (ModConfig.LogUnknownVanillaClipNamesOnce.Value && SoundBank.ShouldLogUnknownClip(currentClip.name))
                    Plugin.Log.LogInfo($"[AudioSource.{__originalMethod.Name}] No mapping for vanilla clip: '{currentClip.name}'");
                return;
            }

            if (resolve == SoundResolve.KeepVanilla)
                return;

            if (ModConfig.LogReplacements.Value)
                Plugin.Log.LogInfo($"[AudioSource.{__originalMethod.Name}] Replace '{currentClip.name}' -> '{replacement.name}'");

            if (argClip != null && __args != null)
            {
                for (var i = 0; i < __args.Length; i++)
                {
                    if (__args[i] is AudioClip)
                    {
                        __args[i] = replacement;
                        break;
                    }
                }
            }
            else
            {
                __instance.clip = replacement;
            }
        }
        catch (Exception e)
        {
            if (_loggedRuntimeError)
                return;

            _loggedRuntimeError = true;
            Plugin.Log.LogError($"Runtime error in AudioSource Play* patch: {e}");
        }
    }

    private static void LogHookEntry(string hookName, AudioSource source, AudioClip clip)
    {
        if (!ModConfig.DebugLogHookEntrypoints.Value)
            return;
        if (CustomSoundPlayOneShotPatch.ShouldSuppressDebugEntry(hookName, source, clip))
            return;

        var maxPerMethod = Mathf.Max(0, ModConfig.DebugLogHookEntrypointsPerMethod.Value);
        if (!DebugEntryCounts.TryGetValue(hookName, out var count))
            count = 0;
        if (maxPerMethod > 0 && count >= maxPerMethod)
            return;

        DebugEntryCounts[hookName] = count + 1;

        var srcName = source != null ? source.name : "<null-source>";
        var clipName = clip != null ? clip.name : "<null-clip>";
        Plugin.Log.LogInfo($"[HookEntry] {hookName} src='{srcName}' clip='{clipName}'");
    }
}

[HarmonyPatch]
internal static class CustomSoundAudioSourceSetClipPatch
{
    private static bool _loggedRuntimeError;
    private static readonly Dictionary<string, int> DebugEntryCounts = new Dictionary<string, int>();

    static MethodBase TargetMethod()
    {
        return AccessTools.PropertySetter(typeof(AudioSource), nameof(AudioSource.clip));
    }

    [HarmonyPrefix]
    private static void BeforeSetClip(AudioSource __instance, ref AudioClip value)
    {
        try
        {
            if (!ModConfig.EnableCustomSounds.Value)
                return;

            const string hookName = "AudioSource.set_clip";

            if (ModConfig.DebugLogHookEntrypoints.Value &&
                !CustomSoundPlayOneShotPatch.ShouldSuppressDebugEntry(hookName, __instance, value))
            {
                var maxPerMethod = Mathf.Max(0, ModConfig.DebugLogHookEntrypointsPerMethod.Value);
                if (!DebugEntryCounts.TryGetValue(hookName, out var count))
                    count = 0;
                if (maxPerMethod <= 0 || count < maxPerMethod)
                {
                    DebugEntryCounts[hookName] = count + 1;
                    var srcName = __instance != null ? __instance.name : "<null-source>";
                    var clipName = value != null ? value.name : "<null-clip>";
                    Plugin.Log.LogInfo($"[HookEntry] {hookName} src='{srcName}' clip='{clipName}'");
                }
            }

            if (__instance == null || value == null)
                return;

            if (SoundBank.IsManagedClip(value))
            {
                var mc = value;
                if (SoundBank.TryRerollManagedClipOnSource(__instance, ref mc))
                    value = mc;
                return;
            }

            if (!CustomSoundPlayOneShotPatch.IsAudioSourceInScope(__instance))
                return;

            SoundBank.RememberVanillaPlaybackContext(__instance, value);

            var resolve = SoundBank.TryResolveReplacement(value.name, value, out var replacement);
            if (resolve == SoundResolve.NoMatch)
            {
                SoundBank.TrackDiscoveredClip(value.name);
                if (ModConfig.LogUnknownVanillaClipNamesOnce.Value && SoundBank.ShouldLogUnknownClip(value.name))
                    Plugin.Log.LogInfo($"[AudioSource.set_clip] No mapping for vanilla clip: '{value.name}'");
                return;
            }

            if (resolve == SoundResolve.KeepVanilla)
                return;

            if (ModConfig.LogReplacements.Value)
                Plugin.Log.LogInfo($"[AudioSource.set_clip] Replace '{value.name}' -> '{replacement.name}'");

            value = replacement;
        }
        catch (Exception e)
        {
            if (_loggedRuntimeError)
                return;

            _loggedRuntimeError = true;
            Plugin.Log.LogError($"Runtime error in AudioSource.set_clip patch: {e}");
        }
    }
}
