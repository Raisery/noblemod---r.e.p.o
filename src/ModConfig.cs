using BepInEx.Configuration;

namespace NobleMod;

internal static class ModConfig
{
    public static ConfigEntry<bool> EnableSpawnOverrides;
    public static ConfigEntry<string> SpawnOverridesFileName;
    public static ConfigEntry<bool> LogSpawnOverrides;

    /// <summary>Debug : dump une fois les blobs de matching des ennemis (voir description au Bind).</summary>
    public static ConfigEntry<bool> DumpEnemyMobKeysOnce;

    /// <summary>
    /// Si activé au démarrage, enregistre les conditions SoundAPI fournies par NobleMod (ex. NobleMod:random_slot).
    /// Désactivé : le pack ne doit pas référencer ces types (sinon erreur de chargement SoundAPI). Redémarrage requis pour prendre effet.
    /// </summary>
    public static ConfigEntry<bool> EnableNobleModSoundPackConditions;

    /// <summary>Debug : log quand une ligne mode plage matche (R + nom du .ogg).</summary>
    public static ConfigEntry<bool> LogRandomSlotRange;

    /// <summary>Debug : log « Sticky … branché / débranché » sur l’AudioSource (init, nouveau clip, boucle).</summary>
    public static ConfigEntry<bool> LogStickyAttachDetach;

    /// <summary>Debug : à chaque évaluation où la ligne matche, log le nom du fichier son (<c>sound</c>).</summary>
    public static ConfigEntry<bool> LogSoundPickEachMatch;

    public static void Bind(ConfigFile config)
    {
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

        DumpEnemyMobKeysOnce = config.Bind(
            "Spawning",
            "DumpEnemyMobKeysOnce",
            false,
            "Debug : au prochain reveil d'EnemyDirector en partie, log une ligne par mob (blob utilise pour matcher le JSON spawn override), puis repasse a false et sauvegarde la config. Utile pour choisir les chaines dans level_enemy_overrides.json."
        );

        EnableNobleModSoundPackConditions = config.Bind(
            "SoundPack",
            "EnableNobleModSoundPackConditions",
            true,
            "Enregistre au demarrage les conditions SoundAPI NobleMod (ex. NobleMod:random_slot dans replacers/*.json). OFF : ne pas utiliser ces conditions dans le JSON (sinon echec chargement pack). Redemarrage jeu pour appliquer."
        );

        LogRandomSlotRange = config.Bind(
            "SoundPack",
            "LogRandomSlotRange",
            false,
            "Debug : pour NobleMod:random_slot en mode plage, log BepInEx « R = … -> son ….ogg » uniquement quand R change (par AudioSource). Peut rester verbeux si R change souvent (ex. boucle)."
        );

        LogStickyAttachDetach = config.Bind(
            "SoundPack",
            "LogStickyAttachDetach",
            false,
            "Debug : pour NobleMod:random_slot avec sticky, log quand le sticky se branche sur l'AudioSource (premier play, nouveau clip) et quand il se débranche (boucle détectée, changement de clip avant re-branchement)."
        );

        LogSoundPickEachMatch = config.Bind(
            "SoundPack",
            "LogSoundPickEachMatch",
            false,
            "Debug : à chaque fois qu'une ligne NobleMod:random_slot matche (condition vraie), log BepInEx le nom du son (fichier .ogg). Active via le menu NobleMod. Si le nom n'est pas lisible, un log indique le type du Parent SoundAPI (reflexion sur proprietes/champs courants). Tres verbeux avec update_every_frame."
        );
    }
}
