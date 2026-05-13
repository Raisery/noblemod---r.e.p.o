# NobleMod

Mod BepInEx / Harmony pour REPO : **menu in-game** (MenuLib), **overrides de spawn** optionnels (dev uniquement), et **remplacements audio** fournis comme **sound pack [loaforcsSoundAPI](https://thunderstore.io/c/repo/p/loaforc/loaforcsSoundAPI/)** (pas de patch Harmony sur l’audio **vanilla** du jeu ; un patch ciblé **SoundAPI** existe pour le mode `sticky` des conditions NobleMod).

Auteur : **Raisery**

**Dernière release documentée : 1.1.0** — **cohérence multijoueur des sons aléatoires** (pool partagé Photon via REPOLib `NetworkedEvent`, indexé par clé stable ; couvre conditions `NobleMod:random_slot` et tirages internes SoundAPI). Détail : [CHANGELOG.md](CHANGELOG.md).

**Historique des versions** : [CHANGELOG.md](CHANGELOG.md) (obligatoire pour suivre les changements entre releases). La **description courte** affichée sur Thunderstore est dans `thunderstore/manifest.json` ; la page mod Thunderstore utilise `thunderstore/README.md` (copiée dans le zip).

## Arborescence du dépôt

| Dossier               | Rôle                                                                                                                               |
| --------------------- | ---------------------------------------------------------------------------------------------------------------------------------- |
| `src/`                | Code C# du plugin (`Plugin.cs`, `ModConfig.cs`, `Patches/`, …)                                                                     |
| `content/sound-pack/` | `sound_pack.json`, `replacers/`, **`sounds/*.ogg`** (tout le pack SoundAPI en dev ; les `.ogg` sont ignorés par git)               |
| `content/spawn/`      | JSON d’exemple / local pour les overrides de spawn (copié vers `SpawnConfig/` dans le zip)                                         |
| `content/samples/`    | Exemples optionnels (ex. hiérarchie de clips découverts)                                                                           |
| `build/`              | Scripts PowerShell (refs, zip Thunderstore, install profil TMM)                                                                    |
| `thunderstore/`       | `manifest.json`, `icon.png`, `README.md` (page courte Thunderstore ; le zip inclut aussi `CHANGELOG.md` depuis la racine du dépôt) |
| `refs/`               | DLL Unity / BepInEx (copiées en local, non versionnées)                                                                            |

À l’installation jeu, la structure reste `BepInEx/plugins/NobleMod/` (DLL, sound pack, `sounds/`, `SpawnConfig/`).

## Dépendances (joueur)

- **BepInEx**
- **[REPOLib](https://thunderstore.io/c/repo/p/Zehs/REPOLib/)** (dépendance BepInEx dure ; le manifest Thunderstore épingle `Zehs-REPOLib-4.0.2`).
- **[loaforcsSoundAPI](https://thunderstore.io/c/repo/p/loaforc/loaforcsSoundAPI/)** (version récente pour REPO).
- **MenuLib** (optionnel pour le menu ; dépendance soft côté code)

**Boucles + plusieurs `weight` (ex. humming chasseur)** : le pack utilise un fichier replacer dédié `replacers/noblemod_hunter_hum.json` avec **`update_every_frame`** au niveau de la collection SoundAPI (voir [code source SoundAPI](https://github.com/loaforcsSoundAPI/loaforcsSoundAPI) : `SoundReplacementCollection`, sérialisation JSON en snake_case). Le reste des remplacements reste dans `noblemod.json` sans ce flag pour éviter un coût inutile sur d’autres sons.

## Build

1. Copier les références du jeu :
   - `powershell -ExecutionPolicy Bypass -File ".\build\Copy-RepoRefs.ps1" -RepoPath "G:\SteamLibrary\steamapps\common\REPO"`
   - Le script copie aussi **`me.loaforc.soundapi.dll`** vers `refs/` s’il la trouve sous `BepInEx/plugins` (à la racine, **dans le même dossier que NobleMod.dll**, ou plus profond jusqu’à 6 niveaux). Sinon, déposez la DLL à la main dans `refs\me.loaforc.soundapi.dll`.
2. Compiler :
   - `dotnet build -c Release`

## Sound pack (SoundAPI)

- `content/sound-pack/sound_pack.json` — métadonnées du pack (le champ `version` est aligné sur `PluginInfo.Version` lors du **packaging** Thunderstore).
- `content/sound-pack/replacers/*.json` — règles SoundAPI (`matches`, `sounds`, `weight`, conditions JSON ; voir [conditions custom C#](https://soundapi.loaforc.me/csharp-api/conditions.html)). NobleMod enregistre **`NobleMod:random_slot`** si `EnableNobleModSoundPackConditions` est activé au démarrage.
- `content/sound-pack/sounds/*.ogg` — sources audio en dev (le script vérifie que chaque `sound` des replacers existe ici).

**Index doc SoundAPI** (officiel) : [soundapi.loaforc.me](https://soundapi.loaforc.me/) — [Getting Started](https://soundapi.loaforc.me/soundpack-tutorials/guide/getting-started.html), [Sound-Pack API](https://soundapi.loaforc.me/soundpack-api/soundpack-api.html), [Mappings](https://soundapi.loaforc.me/soundpack-api/mappings.html), [Value Range](https://soundapi.loaforc.me/soundpack-api/value-range.html), [conditions C#](https://soundapi.loaforc.me/csharp-api/conditions.html). Même index pour les agents : `.cursor/rules/noblemod-architecture.mdc`.

Format des `matches` SoundAPI : trois segments `parent:objet:clip` (ex. `*:*:nom du clip` pour ignorer parent et objet). Pour le humming chasseur, **`noblemod_hunter_hum.json`** combine **`update_every_frame`**, la condition **`NobleMod:random_slot`** (mode **slot** : `slot` / `count` / `slot_weights` optionnel ; mode **plage** : `random_match` avec tirage **`R` fixe 1..1000** partagé, **`sticky`**, etc.) et un **patch Harmony NobleMod** sur la classe SoundAPI `AudioSourceAdditionalData` (contexte d’évaluation + clamp `time`).

Pour **régénérer** `content/sound-pack/replacers/noblemod.json` :

- `powershell -ExecutionPolicy Bypass -File ".\build\Generate-NobleModReplacer.ps1"`

## Installation (dev)

Sous le profil ou le dossier plugins :

- `NobleMod.dll`, `sound_pack.json`, `replacers/` (ex. `noblemod.json`, `noblemod_hunter_hum.json`), `sounds/*.ogg`, optionnellement `SpawnConfig/level_enemy_overrides.json`

Script Thunderstore Mod Manager :

- `.\build\Build-And-Install.ps1 -ProfileName test`

## Config BepInEx

Fichier : `BepInEx\config\raisery.noblemod.cfg`

- `EnableSpawnOverrides`, `SpawnOverridesFileName`, `LogSpawnOverrides` — spawn uniquement.
- `EnableNobleModSoundPackConditions` — enregistrement au démarrage des conditions SoundAPI NobleMod (`NobleMod:random_slot`, etc.). **Redémarrage** pour appliquer ON/OFF ; si OFF, retirez ces `type` du JSON des replacers ou le chargement du pack peut échouer. Le reste des réglages audio reste dans les fichiers **SoundAPI** du pack.
- **Section `[Multiplayer]` (fichier de config uniquement, pas de toggle menu)** : `EnableMultiplayerSoundSync` (défaut `true`) — synchronise les tirages aléatoires de sons entre joueurs en multijoueur ; `MultiplayerSoundPoolSize` (défaut `256`) ; `MultiplayerSoundPoolRefreshIntervalSeconds` (défaut `300` s ; `0` = jamais après init/changement de niveau) ; `LogMultiplayerSoundSync` (défaut `false`). Détails et architecture : section **Cohérence multijoueur des sons** ci-dessous.
- **Debug (fichier de config uniquement, pas de toggle menu)** : `LogRandomSlotRange` — log `R = … -> son ….ogg` quand `R` change par source ; `LogStickyAttachDetach` — logs d’attache / détache du mode **sticky** sur l’`AudioSource`. Voir [CHANGELOG.md](CHANGELOG.md) section **1.0.3** pour le détail du comportement SoundAPI / Harmony.

## Cohérence multijoueur des sons

Sans cette feature, chaque client tire indépendamment ses sons aléatoires (50/50 chacarron vs smash-mouth, R 1..1000 pour les conditions plage, etc.) ⇒ deux joueurs peuvent entendre des variantes différentes pour le même évènement audio.

NobleMod active par défaut une synchronisation **autoritaire pool indexé par clé** :

1. **Pool partagé** : le master Photon génère un tableau d'entiers (taille `MultiplayerSoundPoolSize`) et le **broadcast une fois** via REPOLib `NetworkedEvent` (Photon `RaiseEvent`) avec `EventCaching.AddToRoomCacheGlobal` ⇒ tout joueur qui rejoint en cours de partie reçoit immédiatement le dernier pool. Topup automatique toutes les `MultiplayerSoundPoolRefreshIntervalSeconds` secondes et à chaque changement de master / joueur qui rejoint.
2. **Clé stable** par évènement audio, identique sur tous les clients par construction :
   - `matches` du `SoundReplacementGroup` (string du JSON, identique chez tous).
   - `ViewID` du `PhotonView` le plus proche de l'`AudioSource` (sync Photon).
   - Nom du clip vanilla (préfixe pack retiré).
   - Occurrence temporelle quantifiée sur `PhotonNetwork.Time` : `clip.length` pour le `sticky`, ~250 ms pour le non-sticky.
3. **Lookup local** : `R = pool[ FNV-1a(clé) % pool.Length ]` ⇒ aucun aller-retour réseau par tirage, juste de l'arithmétique locale.
4. **Couverture des tirages internes SoundAPI** : un patch Harmony sur `loaforcsSoundAPI.SoundPacks.SoundReplacementHandler.TryGetReplacementClip` sauvegarde/restaure `UnityEngine.Random.state` autour d'un `Random.InitState(seedDeLaClé)`, ce qui synchronise les `Random.Range` que SoundAPI fait pour choisir entre plusieurs groupes qui matchent et entre plusieurs sons d'un groupe (ex. `noblemod_headman.json`).
5. **Fallback** automatique sur `Random.Range` local quand : feature désactivée (`EnableMultiplayerSoundSync=false`), pas en room, room solo (≤ 1 joueur), pool pas encore reçu après join, ou clé non identifiable.

Couvre nativement les ennemis spawnés via **REPOLib NetworkPrefab** (le ViewID Photon est sync par construction).

## Spawn mapping JSON (niveau -> mob principal)

Fichier source dans le dépôt : `content/spawn/level_enemy_overrides.json` (copié en `SpawnConfig/` dans le paquet).

```json
{
  "1": "huntsman",
  "2": "headman"
}
```

Si le mob demandé n'est pas trouvé dans les setups du niveau, le jeu garde le spawn vanilla et le mod écrit un log de fallback.

## Menu in-game

Paramètres du jeu → bouton **NOBLEMOD** (ou **NobleMod — réglages du mod** selon le layout MenuLib) → popup **NobleMod** avec :

- **Spawn override** — active ou désactive le mapping JSON des mobs principaux par niveau.
- **Conditions pack NobleMod (SoundAPI)** — enregistrement au démarrage des conditions C# NobleMod (`NobleMod:random_slot`, etc.) ; **redémarrage du jeu** pour appliquer ON/OFF.
- **Log chaque son selectionne (random_slot)** — branche `LogSoundPickEachMatch` (très verbeux avec `update_every_frame` sur le humming chasseur).

Les changements sont enregistrés dans `raisery.noblemod.cfg` via `Config.Save()`.

## Paquet Thunderstore

- `.\build\build-thunderstore-package.ps1` — build + zip Thunderstore (`manifest.json`, `README.md`, **`CHANGELOG.md`**, `icon.png` à la racine du zip, comme attendu par Thunderstore pour la page du mod).
