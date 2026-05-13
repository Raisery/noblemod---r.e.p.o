using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace NobleMod.Replacers;

/// <summary>
/// Decouvre les fichiers JSON replacers du pack NobleMod charges par SoundAPI, bind un
/// <see cref="ConfigEntry{T}"/> bool par fichier (section <c>Replacers</c>, defaut ON) et fournit
/// le lookup runtime utilise par le patch de filtrage. Tient aussi un registre des
/// <see cref="AudioSource"/> qui jouent actuellement un clip replace, pour permettre la coupure
/// instantanee quand le joueur passe le toggle ON -> OFF en plein jeu.
///
/// Decision : le toggle est strictement local (preference du joueur). Aucune replication reseau,
/// le pool partage de la sync multijoueur continue de fonctionner normalement.
/// </summary>
internal static class ReplacerToggleRegistry
{
    /// <summary>Nom du pack tel que declare dans <c>content/sound-pack/sound_pack.json</c>. Sert de fallback si le filtre par chemin echoue (ex. installation atypique).</summary>
    const string NobleModPackName = "NobleMod";

    static readonly object Sync = new();
    static readonly Dictionary<string, ReplacerEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConditionalWeakTable<AudioSource, ActiveBinding> ActiveBindings = new();

    static ConfigFile _config;
    static ManualLogSource _log;
    static volatile bool _ready;

    /// <summary>True si au moins un pack NobleMod a ete decouvert et au moins un toggle bind.</summary>
    internal static bool IsReady => _ready;

    /// <summary>Snapshot trie (par nom de fichier) pour l'UI menu.</summary>
    internal static IReadOnlyList<ReplacerEntry> SnapshotEntries()
    {
        lock (Sync)
        {
            return Entries.Values
                .OrderBy(e => e.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    /// <summary>A appeler une seule fois depuis <c>Plugin.Awake</c>. Le scan reel se fait par patch Harmony quand SoundAPI charge un pack.</summary>
    internal static void Bootstrap(ConfigFile config, ManualLogSource log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Scan principal : itere sur le dictionnaire interne <c>SoundPackDataHandler.SoundReplacements</c>
    /// (dictionnaire <c>match -&gt; List&lt;SoundReplacementGroup&gt;</c> rempli par SoundAPI quand
    /// elle charge les <c>replacers/*.json</c>) et regroupe par <c>group.Parent.FilePath</c>.
    ///
    /// Pourquoi pas <c>pack.ReplacementCollections</c> ? Pour les sound-packs construits via
    /// <c>replacers/</c> (cas NobleMod), SoundAPI ne peuple PAS cette liste sur le pack — elle ne sert
    /// qu'aux collections declarees inline dans <c>sound_pack.json</c>. Le dictionnaire interne est la
    /// seule source fiable.
    /// </summary>
    internal static void DiscoverAlreadyLoadedPacks()
    {
        try
        {
            var handlerType = AccessTools.TypeByName("loaforcsSoundAPI.SoundPacks.SoundPackDataHandler");
            if (handlerType == null)
            {
                _log?.LogWarning("[NobleMod] Replacers : SoundPackDataHandler introuvable au scan.");
                return;
            }

            var field = AccessTools.Field(handlerType, "SoundReplacements")
                ?? handlerType.GetField(
                    "SoundReplacements",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field == null)
            {
                _log?.LogWarning("[NobleMod] Replacers : champ SoundReplacements introuvable (reflection).");
                return;
            }

            if (field.GetValue(null) is not System.Collections.IDictionary dict)
            {
                _log?.LogWarning("[NobleMod] Replacers : SoundReplacements null / type inattendu au scan.");
                return;
            }

            var rootDir = NormalizeDir(Plugin.AssemblyDirectory);
            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var addedThisScan = 0;
            var foreignFiles = 0;

            foreach (System.Collections.DictionaryEntry kv in dict)
            {
                if (kv.Value is not System.Collections.IEnumerable groups)
                    continue;
                foreach (var group in groups)
                {
                    if (group == null)
                        continue;
                    var parentProp = group.GetType().GetProperty("Parent", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    var parent = parentProp?.GetValue(group);
                    if (parent == null)
                        continue;
                    var fpProp = parent.GetType().GetProperty("FilePath", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    var filePath = fpProp?.GetValue(parent) as string;
                    if (string.IsNullOrEmpty(filePath))
                        continue;
                    if (!seenFiles.Add(filePath))
                        continue;

                    // Filtre : fichier livre dans BepInEx/plugins/NobleMod/.
                    var belongs = !string.IsNullOrEmpty(rootDir)
                        && NormalizePath(filePath).StartsWith(rootDir, StringComparison.OrdinalIgnoreCase);
                    if (!belongs)
                    {
                        foreignFiles++;
                        continue;
                    }

                    if (TryRegisterFile(filePath))
                        addedThisScan++;
                }
            }

            int totalRegistered;
            lock (Sync)
                totalRegistered = Entries.Count;

            _log?.LogInfo(
                $"[NobleMod] Replacers : scan SoundReplacements — {seenFiles.Count} fichier(s) source(s) unique(s) vu(s), " +
                $"{foreignFiles} hors-NobleMod ignore(s), +{addedThisScan} ajoute(s) ce scan, total={totalRegistered} (rootDir='{rootDir}').");

            if (totalRegistered > 0)
                _ready = true;
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NobleMod] Replacers : exception scan ignoree : {ex.Message}");
        }
    }

    /// <summary>
    /// Hook appele par <see cref="Patches.SoundPackLoadedDiscoveryPatch"/> apres chaque <c>AddLoadedPack</c>.
    /// Best-effort : selon les versions de SoundAPI, <c>pack.ReplacementCollections</c> peut etre vide
    /// (cas NobleMod). Le scan reel et fiable est <see cref="DiscoverAlreadyLoadedPacks"/> via le
    /// dictionnaire interne <c>SoundReplacements</c>, declenche a l'ouverture du menu NobleMod.
    /// </summary>
    internal static void DiscoverPack(object soundPack)
    {
        if (soundPack == null || _config == null)
            return;
        try
        {
            var packType = soundPack.GetType();
            var collectionsProp = packType.GetProperty("ReplacementCollections", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (collectionsProp?.GetValue(soundPack) is not System.Collections.IEnumerable collections)
                return;

            var rootDir = NormalizeDir(Plugin.AssemblyDirectory);
            var added = 0;
            foreach (var col in collections)
            {
                if (col == null)
                    continue;
                var fpProp = col.GetType().GetProperty("FilePath", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                var filePath = fpProp?.GetValue(col) as string;
                if (string.IsNullOrEmpty(filePath))
                    continue;
                var belongs = !string.IsNullOrEmpty(rootDir)
                    && NormalizePath(filePath).StartsWith(rootDir, StringComparison.OrdinalIgnoreCase);
                if (!belongs)
                    continue;
                if (TryRegisterFile(filePath))
                    added++;
            }

            if (added > 0)
            {
                _ready = true;
                _log?.LogInfo($"[NobleMod] Replacers DiscoverPack : +{added} fichier(s) via pack.ReplacementCollections.");
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning($"[NobleMod] Replacers DiscoverPack : exception ignoree : {ex.Message}");
        }
    }

    static string NormalizeDir(string dir)
    {
        if (string.IsNullOrEmpty(dir))
            return string.Empty;
        var n = NormalizePath(dir);
        if (n.Length > 0 && n[n.Length - 1] != Path.DirectorySeparatorChar)
            n += Path.DirectorySeparatorChar;
        return n;
    }

    static string NormalizePath(string p) =>
        string.IsNullOrEmpty(p)
            ? string.Empty
            : Path.GetFullPath(p.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True si le replacer du fichier est actif. Inconnu = true (pas de blocage par defaut, fallback safe).</summary>
    internal static bool IsEnabled(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return true;
        lock (Sync)
        {
            return !Entries.TryGetValue(filePath, out var e) || e.Entry.Value;
        }
    }

    /// <summary>Enregistre qu'un <see cref="AudioSource"/> joue un clip remplace par <paramref name="filePath"/>. Ecrase l'eventuelle entree precedente.</summary>
    internal static void TrackPlayback(AudioSource source, string filePath)
    {
        if (source == null || string.IsNullOrEmpty(filePath))
            return;
        if (ActiveBindings.TryGetValue(source, out var existing))
            existing.FilePath = filePath;
        else
            ActiveBindings.Add(source, new ActiveBinding { FilePath = filePath });
    }

    /// <summary>Coupe instantanement (<c>AudioSource.Stop()</c>) toutes les sources actuellement actives pour ce fichier.</summary>
    internal static int StopActiveSourcesFor(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return 0;
        var stopped = 0;
        var dead = new List<AudioSource>();
        foreach (var kv in ActiveBindings)
        {
            if (kv.Key == null || !kv.Key)
            {
                dead.Add(kv.Key);
                continue;
            }
            if (!string.Equals(kv.Value.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (kv.Key.isPlaying)
                {
                    kv.Key.Stop();
                    stopped++;
                }
            }
            catch
            {
                // Source detruite entre-temps : on la retire au prochain passage.
            }
        }

        foreach (var d in dead)
        {
            if (d != null)
                ActiveBindings.Remove(d);
        }

        return stopped;
    }

    static bool TryRegisterFile(string absolutePath)
    {
        lock (Sync)
        {
            if (Entries.ContainsKey(absolutePath))
                return false;

            var fileName = Path.GetFileNameWithoutExtension(absolutePath);
            if (string.IsNullOrEmpty(fileName))
                return false;

            // Cles de config volontairement stables : si l'utilisateur renomme un fichier, on aura
            // une nouvelle entree (defaut ON), l'ancienne reste dans le .cfg comme orpheline.
            var key = $"Enable_{SanitizeKey(fileName)}";
            var entry = _config.Bind(
                "Replacers",
                key,
                true,
                $"Active/desactive le replacer NobleMod issu de '{fileName}.json'. " +
                $"Modifiable en jeu via le menu NobleMod (section Replacers). " +
                $"OFF : aucun son de ce fichier ne remplace le son vanilla. " +
                $"Toggle strictement local : ne se replique pas aux autres joueurs en multi.");

            Entries[absolutePath] = new ReplacerEntry(absolutePath, fileName, entry);
            return true;
        }
    }

    static string SanitizeKey(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray();
        return new string(chars);
    }

    sealed class ActiveBinding
    {
        internal string FilePath;
    }

    /// <summary>Une entree (fichier JSON replacer) decouverte + son <see cref="ConfigEntry{Boolean}"/> bind.</summary>
    internal sealed class ReplacerEntry
    {
        internal string FilePath { get; }
        internal string DisplayName { get; }
        internal ConfigEntry<bool> Entry { get; }

        internal ReplacerEntry(string filePath, string displayName, ConfigEntry<bool> entry)
        {
            FilePath = filePath;
            DisplayName = displayName;
            Entry = entry;
        }
    }
}
