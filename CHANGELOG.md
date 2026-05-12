# Changelog

## [1.0.3] - 2026-05-12

### Ajouté

- Ce fichier `CHANGELOG.md` pour suivre les changements entre releases.
- **Condition SoundAPI** `NobleMod:random_slot` : mode **slot** (`slot`, `count`, `slot_weights` optionnel pour pondération), mode **plage** (`random_match` / `random_number`, syntaxe [value-range SoundAPI](https://soundapi.loaforc.me/soundpack-api/value-range.html), tirage **`R` uniforme 1..1000** partagé, `..` et normalisation `...` → `..`), option **`sticky`** ; **Harmony** sur `loaforcsSoundAPI.Core.AudioSourceAdditionalData.Update` pour le contexte d’évaluation + clamp `AudioSource.time` après swap de clip.
- **Config** : `EnableNobleModSoundPackConditions` (`[SoundPack]`, défaut `true`) + toggle menu **« Conditions pack NobleMod (SoundAPI) »** ; `LogRandomSlotRange` (défaut `false`) — log `R = … -> son ….ogg` **uniquement quand `R` change** par source ; `LogStickyAttachDetach` (défaut `false`) — logs **Sticky plage|slot : branché / débranché** (premier attache, nouveau clip, boucle) ; `LogSoundPickEachMatch` (défaut `false`) + toggle **« Log chaque son selectionne (random_slot) »** — à chaque évaluation où la ligne matche, log du nom du fichier `sound` (très verbeux avec `update_every_frame`).
- **Sound pack** : `replacers/noblemod_hunter_hum.json` avec **`update_every_frame`**, conditions plage + **`sticky`: true** pour le clip `enemy hunter humming loop`.

### Modifié

- **Audio** : délégation à **loaforcsSoundAPI** (sound pack) ; suppression de l’ancien reshuffle humming côté DLL au profit de `update_every_frame` dans le JSON.
- **Dépôt** : arborescence `src/`, `content/` (`sound-pack`, `spawn`, `samples`), `build/` ; code C# déplacé hors racine (anciens `Plugin.cs`, `SoundBank`, patches audio internes, parseurs remplacements, etc. supprimés au profit du sound pack) ; `BepInDependency` **`me.loaforc.soundapi`** ; référence de build `me.loaforc.soundapi.dll` (copie via `build/Copy-RepoRefs.ps1`).
- **Spawn** : helper **`RunLevel.TryGetCurrentLevelNumber()`** (`src/RunLevel.cs`) partagé avec les patches d’override pour lire le niveau courant via `RunManager` / `levelsCompleted`.
- **`.gitignore`** : ignore les assets lourds ou locaux (`content/sound-pack/sounds/`, `content/spawn/`, `refs/`, `dist/`, `bin/`, `obj/`, `thunderstore/`, `.cursor/`, etc.) pour garder le dépôt léger.
- **Thunderstore** : `description` du `manifest.json` courte ; détails dans le README ; pas de dépendance au paquet deprecated **`loaforcsSoundAPI_REPO`** (BepInEx + **REPOLib** + **loaforcsSoundAPI** + MenuLib) ; **REPOLib** en dépendance BepInEx **dure** côté plugin et **`Zehs-REPOLib-4.0.2`** épinglé dans le manifest.
- **Sound pack** : `matches` au format trois segments `*:*:nom du clip`.

### Corrigé

- **Audio (clamp)** : le clamp sur **chaque** écriture de `AudioSource.time` et la marge sous la fin du clip **coupaient** souvent le son avant la fin et perturbaient le début ; désormais correction **seulement** si la position est **hors clip** (`< 0`, `≥ length`, NaN). Patch du setter **`time`** retiré.
- **`LogSoundPickEachMatch`** : résolution du nom du fichier via **`SoundInstance.Sound`** ou **réflexion** sur `Parent` (propriétés/champs courants) ; si introuvable, log du **type** `Parent` pour diagnostic.
- **Retrait** du cycle « hold après débranchement + une frame de latch » sur le sticky plage : il forçait des conditions à `false` trop longtemps et provoquait des **sons qui ne démarrent pas au début** et **coupures avant la fin** avec SoundAPI.
- **`NobleMod:random_slot` (sticky)** : si `isPlaying` était faux un court instant, le code retirait un nouveau `R` (coalesced) au lieu de garder `ChosenPick` → coupure de son ; réutilisation de `ChosenPick` avec le même clip ; seuil de saut arrière **`0.18s`**. Idem mode slot sticky. **Warmup par groupe** : tant que la source n’est pas prête (`null` / pas de clip / `!isPlaying` sans état pause), un même `R` (ou slot) est figé par `SoundReplacementGroup` au lieu du tirage coalescé par frame, pour éviter plusieurs changements de tirage avant le premier play réel. **Anti-churn** : pendant **64 frames** après un attache complet, une **empreinte** qui fluctue encore trop vite ne déclenche pas refresh de `R`/logs. **Anti-faux « boucle »** : **40 frames** après attache, pas de détection boucle ; `jumpedBack` exige en plus une position **inférieure à 0,4 s**. **Identité logique** : nom normalisé (sans préfixe `NobleMod `) ⇒ empreinte = **hash du nom seul** (les copies vanilla / pack ne divergent plus sur la durée) ; clip au nom vide ⇒ même empreinte si longueur à **≤ 0,35 s** près du dernier nom vu ; sinon hash longueur/fréquence/canaux ; reset d’ancre sur vrai changement de clip.
- **Packaging** : zip Thunderstore via **ZipArchive** et chemins relatifs explicites.

### Note

- L’expérimentation **reshuffle humming** en DLL a été retirée au profit du sound pack SoundAPI documenté ci-dessus.

## [1.0.2] - 2026-05-09

### Modifié

- **Audio** : délégation à **loaforcsSoundAPI** (sound pack : `sound_pack.json`, `replacers/`, `sounds/`). Suppression du remplacement audio Harmony interne (`SoundBank`, patches dédiés).
- **Dépôt** : arborescence `src/`, `content/` (`sound-pack`, `spawn`, `samples`), `build/` (scripts PowerShell).
- **Thunderstore** : archive avec chemins `BepInEx/plugins/NobleMod/...` (aligné sur les paquets REPO courants).

### Corrigé

- Génération du zip Thunderstore via **ZipArchive** et chemins relatifs explicites (évite les soucis de `Compress-Archive` selon les versions PowerShell).

## [1.0.1] - 2026-05-08

### Modifié

- Itérations sur le packaging Thunderstore et les scripts d’installation profil (voir 1.0.2 pour la forme stabilisée).

## [1.0.0]

### Ajouté

- Première base publique du mod : plugin BepInEx, intégration menu (MenuLib), config spawn, remplacements audio (format interne, remplacé en 1.0.2 par un sound pack SoundAPI).
