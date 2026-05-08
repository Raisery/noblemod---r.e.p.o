using BepInEx.Configuration;

namespace NobleMod;

internal static class ModConfig
{
    public static ConfigEntry<bool> EnableCustomSounds;
    /// <summary>Vide = toutes les AudioSource. Sinon, sous-chaîne recherchée dans la hiérarchie (source + parents).</summary>
    public static ConfigEntry<string> AudioSourceHierarchyFilter;
    public static ConfigEntry<bool> LogReplacements;
    public static ConfigEntry<bool> LogUnknownVanillaClipNamesOnce;
    public static ConfigEntry<bool> WriteDiscoveredClipsHierarchyJson;
    public static ConfigEntry<string> DiscoveredClipsHierarchyFileName;
    public static ConfigEntry<string> DiscoveredClipsOutputPath;
    public static ConfigEntry<bool> EnableSpawnOverrides;
    public static ConfigEntry<string> SpawnOverridesFileName;
    public static ConfigEntry<bool> LogSpawnOverrides;
    public static ConfigEntry<bool> DebugLogHookEntrypoints;
    public static ConfigEntry<int> DebugLogHookEntrypointsPerMethod;
    public static ConfigEntry<bool> DebugSuppressMenuSpam;
    public static ConfigEntry<bool> LogWeightedSoundPicks;

    public static void Bind(ConfigFile config)
    {
        EnableCustomSounds = config.Bind(
            "General",
            "EnableCustomSounds",
            true,
            "Active les remplacements de sons personnalises."
        );

        AudioSourceHierarchyFilter = config.Bind(
            "General",
            "AudioSourceHierarchyFilter",
            "",
            "Limite remplacements / decouverte aux AudioSource dont la hierarchie (noms des transforms) contient cette sous-chaine. Vide = tout le jeu (generique)."
        );

        LogReplacements = config.Bind(
            "General",
            "LogReplacements",
            false,
            "Active les logs de remplacement audio."
        );

        LogUnknownVanillaClipNamesOnce = config.Bind(
            "General",
            "LogUnknownVanillaClipNamesOnce",
            false,
            "Log chaque nom de clip vanilla non mappe une seule fois (utile pour completer replacements.json)."
        );

        WriteDiscoveredClipsHierarchyJson = config.Bind(
            "General",
            "WriteDiscoveredClipsHierarchyJson",
            true,
            "Ecrit les nouveaux noms de clips rencontres dans un fichier JSON hierarchique (sans doublons)."
        );

        DiscoveredClipsHierarchyFileName = config.Bind(
            "General",
            "DiscoveredClipsHierarchyFileName",
            "discovered_clips_hierarchy.json",
            "Nom du fichier JSON hierarchique qui stocke les clips decouverts."
        );

        DiscoveredClipsOutputPath = config.Bind(
            "General",
            "DiscoveredClipsOutputPath",
            "",
            "Chemin absolu du dossier ou ecrire le JSON des clips decouverts (ex: .../votre-clone/CustomSounds). Vide = CustomSounds du plugin."
        );

        EnableSpawnOverrides = config.Bind(
            "Spawning",
            "EnableSpawnOverrides",
            false,
            "Active le remplacement du mob principal via un mapping niveau -> mob (fichier JSON dans SpawnConfig)."
        );

        SpawnOverridesFileName = config.Bind(
            "Spawning",
            "SpawnOverridesFileName",
            "level_enemy_overrides.json",
            "Nom du fichier JSON contenant le mapping niveau -> identifiant de mob."
        );

        LogSpawnOverrides = config.Bind(
            "Spawning",
            "LogSpawnOverrides",
            true,
            "Log les decisions de remplacement des mobs principaux (selection, fallback, erreurs)."
        );

        DebugLogHookEntrypoints = config.Bind(
            "Debug",
            "DebugLogHookEntrypoints",
            false,
            "Log les entrees des hooks audio (meme si source/clip est null)."
        );

        DebugLogHookEntrypointsPerMethod = config.Bind(
            "Debug",
            "DebugLogHookEntrypointsPerMethod",
            500,
            "Nombre max de logs d'entree par hook/methode pour eviter le spam (<= 0 = illimite)."
        );

        DebugSuppressMenuSpam = config.Bind(
            "Debug",
            "DebugSuppressMenuSpam",
            true,
            "Masque le bruit des logs audio menu/UI (ex: menu hover, PlayHelper) pour faciliter le debug en partie."
        );

        LogWeightedSoundPicks = config.Bind(
            "Debug",
            "LogWeightedSoundPicks",
            false,
            "Log chaque tirage pondere (Random.value, creneaux cumules, resultat). Utile pour comprendre vanilla % vs customs. Les reutilisations meme frame (cache) ou boucle (sticky) sont en LogDebug."
        );
    }
}
