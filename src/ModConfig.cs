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

    /// <summary>
    /// Active la cohérence multijoueur des tirages aléatoires (conditions NobleMod ET tirages internes SoundAPI).
    /// Le master diffuse un pool partagé via REPOLib NetworkedEvent, chaque client indexe ce pool via une clé stable
    /// (matches du groupe + ViewID du PhotonView le plus proche + occurrence temporelle). Solo : pas de pool, fallback local.
    /// </summary>
    public static ConfigEntry<bool> EnableMultiplayerSoundSync;

    /// <summary>Taille du pool partagé (nombre d'entrées). Plus grand = moins de collisions de clés.</summary>
    public static ConfigEntry<int> MultiplayerSoundPoolSize;

    /// <summary>Intervalle entre deux régénérations complètes du pool par le master, en secondes (0 = jamais après init / changement de niveau).</summary>
    public static ConfigEntry<int> MultiplayerSoundPoolRefreshIntervalSeconds;

    /// <summary>Debug : log les broadcasts pool, lookups (clé -> R) et fallbacks locaux.</summary>
    public static ConfigEntry<bool> LogMultiplayerSoundSync;

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

        EnableMultiplayerSoundSync = config.Bind(
            "Multiplayer",
            "EnableMultiplayerSoundSync",
            true,
            "Active la coherence multijoueur des tirages aleatoires de sons (conditions NobleMod:random_slot + tirages internes SoundAPI quand plusieurs sons d'un meme groupe sont possibles, ex. headman). Le master diffuse un pool partage via REPOLib NetworkedEvent, chaque client le consulte avec une cle stable (matches du groupe + ViewID + occurrence temporelle). En solo : fallback Random.Range local (comportement actuel)."
        );

        MultiplayerSoundPoolSize = config.Bind(
            "Multiplayer",
            "MultiplayerSoundPoolSize",
            256,
            "Taille du pool partage (nombre d'entrees int). Borne [16..4096]. Plus grand = moins de collisions de cles (sons differents qui tomberaient sur la meme entree). 256 est largement suffisant."
        );

        MultiplayerSoundPoolRefreshIntervalSeconds = config.Bind(
            "Multiplayer",
            "MultiplayerSoundPoolRefreshIntervalSeconds",
            300,
            "Intervalle entre deux regenerations completes du pool par le master, en secondes (0 = jamais apres init / changement de niveau). Defaut 300s = 5 minutes."
        );

        LogMultiplayerSoundSync = config.Bind(
            "Multiplayer",
            "LogMultiplayerSoundSync",
            false,
            "Debug : log VERBEUX par evaluation (ex. seed Random utilisee pour chaque appel SoundAPI). Les events critiques (broadcast pool, reception, transitions room/master/player count) sont TOUJOURS logues quel que soit ce reglage."
        );
    }
}
