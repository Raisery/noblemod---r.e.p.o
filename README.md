# NobleMod

Mod BepInEx / Harmony pour REPO : remplacement de sons vanilla (tout le jeu par défaut, filtre optionnel) et configuration optionnelle des mobs principaux par numéro de niveau.

Auteur : **Raisery**

## Build

1. Copier les références du jeu :
   - `powershell -ExecutionPolicy Bypass -File ".\scripts\Copy-RepoRefs.ps1" -RepoPath "G:\SteamLibrary\steamapps\common\REPO"`
2. Compiler :
   - `dotnet build -c Release`

## Installation (dev)

Copier `bin\Release\net472\NobleMod.dll` dans :

- `...\BepInEx\plugins\NobleMod\NobleMod.dll`

Ajouter les sons personnalisés dans :

- `...\BepInEx\plugins\NobleMod\CustomSounds\replacements.json`
- les fichiers `.ogg` référencés dans ce JSON

Ajouter la config de spawn dans :

- `...\BepInEx\plugins\NobleMod\SpawnConfig\level_enemy_overrides.json`

Après mise à jour majeure du mod, vérifie qu’il n’y a qu’un seul dossier plugin correspondant dans `BepInEx\plugins\`.

## Config

Fichier BepInEx généré :

- `BepInEx\config\raisery.noblemod.cfg`

Entrées principales :

- `EnableCustomSounds` (défaut `true`)
- `AudioSourceHierarchyFilter` (défaut vide) : limite aux sources dont la hiérarchie contient cette sous-chaîne ; vide = **toutes** les `AudioSource` (comportement générique). Ancienne clé `HuntsmanHierarchyHint` : recopier la valeur ici si tu utilisais un filtre (ex. `hunter`).
- `LogReplacements` (défaut `false`)
- `LogUnknownVanillaClipNamesOnce` (défaut `false`)
- `WriteDiscoveredClipsHierarchyJson` (défaut `true`)
- `DiscoveredClipsHierarchyFileName` (défaut `discovered_clips_hierarchy.json`)
- `DiscoveredClipsOutputPath` (défaut vide) : chemin absolu du dossier où écrire le JSON des clips découverts (ex. le dossier `CustomSounds` de ton clone du dépôt). Vide = même dossier que les sons du plugin. Hiérarchie : feuilles `"name": "nom exact du clip vanilla"` (sans tableau ; l’ancien `"__clips": ["…"]` reste pris en charge au rechargement).
- `EnableSpawnOverrides` (défaut `false`)
- `SpawnOverridesFileName` (défaut `level_enemy_overrides.json`)
- `LogSpawnOverrides` (défaut `true`)

## Mapping JSON (exact match)

Exemple de `replacements.json` :

```json
{
  "vanilla_clip_name_exact": "mon_son.ogg",
  "contains:footstep": [
    { "file": "pas_a.ogg", "weight": 50 },
    { "vanilla": true, "weight": 25 },
    { "file": "pas_b.ogg", "weight": 25 }
  ]
}
```

- À gauche : nom exact du clip vanilla, ou préfixe `contains:` (sous-chaîne insensible à la casse dans le nom du clip), ou `exact:` (équivalent au nom seul).
- Valeur : soit **une chaîne** (un seul fichier, équivalent à 100 %), soit un **tableau** de variantes avec **`weight` en pourcentage** :
  - `{ "file": "...", "weight": N }` — joue ce fichier custom ;
  - `{ "vanilla": true, "weight": N }` — pour ce tirage, **garde le son vanilla** (pas de remplacement).
- La somme des `weight` peut différer de 100 : le mod **normalise** (et logue un rappel si ce n’est pas 100).
- Chaque lecture du clip en jeu tire **au hasard** une variante selon ces proportions (léger : les fichiers custom sont chargés une fois au démarrage).

## Spawn mapping JSON (niveau -> mob principal)

Fichier : `SpawnConfig/level_enemy_overrides.json`

```json
{
  "1": "huntsman",
  "2": "headman"
}
```

Si le mob demandé n'est pas trouvé dans les setups du niveau, le jeu garde le spawn vanilla et le mod écrit un log de fallback.

## Menu in-game

Dans le menu principal, clique sur `NobleMod Settings` pour ouvrir un panneau simple avec 2 options :

- `Custom Sounds` (active/desactive les remplacements audio)
- `Spawn Override` (active/desactive les overrides de mob principal par niveau)

Les changements sont sauvegardés immédiatement dans `raisery.noblemod.cfg`.
