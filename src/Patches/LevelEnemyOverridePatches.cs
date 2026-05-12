using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NobleMod.Patches;

[HarmonyPatch]
internal static class LevelEnemyOverridePatches
{
    private static readonly HashSet<int> LoggedLevelNoRule = new HashSet<int>();
    private static readonly HashSet<string> LoggedFallbacks = new HashSet<string>();
    private static readonly HashSet<int> AppliedOverrideLevels = new HashSet<int>();

    /// <summary>
    /// Dernière valeur observée de <see cref="RunManager.levelsCompleted"/> dans <see cref="AfterGetEnemy"/>.
    /// Quand elle repasse à 0 après avoir été &gt; 0, c'est typiquement une nouvelle run : il faut vider les sets statiques.
    /// </summary>
    private static int? _lastObservedLevelsCompleted;

    static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(EnemyDirector), "GetEnemy");
    }

    [HarmonyPostfix]
    private static void AfterGetEnemy(MethodBase __originalMethod, object __instance, ref object __result)
    {
        if (__instance == null)
            return;

        try
        {
            MaybeResetAppliedOverridesForNewRun();

            if (!ModConfig.EnableSpawnOverrides.Value)
                return;
            if (__result == null)
                return;
            if (!LooksLikeEnemySetupResult(__result))
                return;

            var levelNumber = RunLevel.TryGetCurrentLevelNumber();
            if (levelNumber <= 0)
                return;

            // Keep behavior stable: force only once per level to avoid replacing
            // every GetEnemy call (which can lead to multiple identical mobs).
            if (AppliedOverrideLevels.Contains(levelNumber))
                return;

            if (!LevelEnemyOverrideBank.TryGetMobKey(levelNumber, out var desiredMobKey))
            {
                if (ModConfig.LogSpawnOverrides.Value && LoggedLevelNoRule.Add(levelNumber))
                    Plugin.Log.LogInfo($"[SpawnOverride] Aucun override pour niveau {levelNumber}. Spawn vanilla conserve.");
                return;
            }

            var replacementSetup = FindBestSetupMatch(__instance, desiredMobKey, __result);
            if (replacementSetup == null)
            {
                if (ModConfig.LogSpawnOverrides.Value)
                {
                    var fallbackKey = $"{levelNumber}:{desiredMobKey}";
                    if (LoggedFallbacks.Add(fallbackKey))
                    {
                        Plugin.Log.LogWarning(
                            $"[SpawnOverride] Niveau {levelNumber}: mob '{desiredMobKey}' introuvable, fallback vanilla conserve. " +
                            "Astuce : Spawning -> DumpEnemyMobKeysOnce = true puis lancer une partie pour lister les blobs utilisables.");
                    }
                }

                return;
            }

            __result = replacementSetup;
            AppliedOverrideLevels.Add(levelNumber);
            if (ModConfig.LogSpawnOverrides.Value)
                Plugin.Log.LogInfo($"[SpawnOverride] Niveau {levelNumber}: mob principal force vers '{desiredMobKey}' (applique une seule fois).");
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"[SpawnOverride] Erreur runtime: {e.Message}");
        }
    }

    /// <summary>
    /// Les HashSet sont statiques pour toute la session BepInEx : sans reset, une 2e partie réutilise les niveaux
    /// déjà marqués comme appliqués lors de la 1re partie.
    /// </summary>
    private static void MaybeResetAppliedOverridesForNewRun()
    {
        try
        {
            if (RunManager.instance == null)
                return;

            var lc = RunManager.instance.levelsCompleted;
            if (_lastObservedLevelsCompleted.HasValue && lc == 0 && _lastObservedLevelsCompleted.Value > 0)
            {
                if (AppliedOverrideLevels.Count > 0 && ModConfig.LogSpawnOverrides.Value)
                    Plugin.Log.LogInfo("[SpawnOverride] Nouvelle run (levelsCompleted repasse a 0) : reinitialisation des overrides par niveau.");

                AppliedOverrideLevels.Clear();
                LoggedLevelNoRule.Clear();
                LoggedFallbacks.Clear();
            }

            _lastObservedLevelsCompleted = lc;
        }
        catch
        {
            // ignore
        }
    }

    private static bool LooksLikeEnemySetupResult(object result)
    {
        var typeName = result.GetType().Name;
        if (typeName.IndexOf("Enemy", StringComparison.OrdinalIgnoreCase) >= 0 &&
            typeName.IndexOf("Setup", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        // Fallback heuristic: result has spawnObjects field, typical for setup-like data.
        try
        {
            var spawnObjectsField = AccessTools.Field(result.GetType(), "spawnObjects");
            return spawnObjectsField != null;
        }
        catch
        {
            return false;
        }
    }

    private static object FindBestSetupMatch(object enemyDirector, string desiredMobKey, object currentResult)
    {
        var expectedSetupType = currentResult.GetType();
        var candidates = CollectEnemySetups(enemyDirector, expectedSetupType);
        if (candidates.Count == 0)
            return null;

        var token = desiredMobKey.Trim().ToLowerInvariant();

        // First pass: strict token matching.
        foreach (var setup in candidates)
        {
            if (SetupContainsToken(setup, token))
                return setup;
        }

        // Second pass: relaxed alias matching for known mobs.
        foreach (var setup in candidates)
        {
            if (SetupMatchesAlias(setup, token))
                return setup;
        }

        return null;
    }

    private static List<object> CollectEnemySetups(object enemyDirector, Type expectedSetupType)
    {
        if (RepolibSpawnSupport.TryGetAllEnemySetupsAsObjects(expectedSetupType, out var fromRepolib) && fromRepolib.Count > 0)
            return fromRepolib;

        return CollectEnemySetupsViaReflection(enemyDirector, expectedSetupType);
    }

    private static List<object> CollectEnemySetupsViaReflection(object enemyDirector, Type expectedSetupType)
    {
        var list = new List<object>();
        var directorType = enemyDirector.GetType();
        var fields = AccessTools.GetDeclaredFields(directorType);
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            object value;
            try
            {
                value = field.GetValue(enemyDirector);
            }
            catch
            {
                continue;
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                foreach (var entry in enumerable)
                {
                    if (entry == null)
                        continue;
                    if (!expectedSetupType.IsInstanceOfType(entry))
                        continue;
                    if (!list.Contains(entry))
                        list.Add(entry);
                }
            }
            else if (value != null && expectedSetupType.IsInstanceOfType(value))
            {
                if (!list.Contains(value))
                    list.Add(value);
            }
        }

        return list;
    }

    private static bool SetupContainsToken(object setup, string token)
    {
        var blob = BuildMobSearchBlob(setup);
        return blob.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool SetupMatchesAlias(object setup, string token)
    {
        var blob = BuildMobSearchBlob(setup).ToLowerInvariant();
        switch (token)
        {
            case "huntsman":
            case "hunter":
                return blob.Contains("hunter");
            case "headman":
                return blob.Contains("headman");
            default:
                return false;
        }
    }

    /// <summary>
    /// Texte unique pour matcher la valeur JSON : type, noms de setup, prefabs, chemins, nom du GameObject prefab, <see cref="EnemyParent.enemyName"/>.
    /// </summary>
    private static string BuildMobSearchBlob(object setup)
    {
        var pieces = new List<string>();
        var setupType = setup.GetType();
        pieces.Add(setupType.Name);

        var setupName = Traverse.Create(setup).Field("name").GetValue<string>();
        if (!string.IsNullOrWhiteSpace(setupName))
            pieces.Add(setupName);

        var spawnObjects = Traverse.Create(setup).Field("spawnObjects").GetValue() as IEnumerable;
        if (spawnObjects != null)
        {
            foreach (var spawnObj in spawnObjects)
            {
                if (spawnObj == null)
                    continue;
                AppendSpawnRefTokens(spawnObj, pieces);
            }
        }

        return string.Join(" ", pieces);
    }

    private static void AppendSpawnRefTokens(object spawnObj, List<string> pieces)
    {
        var tr = Traverse.Create(spawnObj);
        GameObject prefabGo = null;
        try
        {
            var prop = spawnObj.GetType().GetProperty("Prefab", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            prefabGo = prop?.GetValue(spawnObj, null) as GameObject;
        }
        catch
        {
            // ignore
        }

        if (prefabGo == null)
        {
            foreach (var fieldName in new[] { "prefab", "m_Prefab", "gamePrefab", "m_GamePrefab" })
            {
                try
                {
                    var f = AccessTools.Field(spawnObj.GetType(), fieldName);
                    if (f == null)
                        continue;
                    prefabGo = f.GetValue(spawnObj) as GameObject;
                    if (prefabGo != null)
                        break;
                }
                catch
                {
                    // ignore
                }
            }
        }

        if (prefabGo != null)
        {
            pieces.Add(prefabGo.name);
            var parent = prefabGo.GetComponent<EnemyParent>();
            if (parent != null && !string.IsNullOrWhiteSpace(parent.enemyName))
                pieces.Add(parent.enemyName);
        }

        try
        {
            var prefabName = tr.Field("prefabName").GetValue<string>();
            if (!string.IsNullOrWhiteSpace(prefabName))
                pieces.Add(prefabName);
        }
        catch
        {
            // ignore
        }

        try
        {
            var resourcePath = tr.Field("resourcePath").GetValue<string>();
            if (!string.IsNullOrWhiteSpace(resourcePath))
                pieces.Add(resourcePath);
        }
        catch
        {
            // ignore
        }
    }

    [HarmonyPatch(typeof(EnemyDirector))]
    private static class EnemyDirectorAwakeMobDumpPatch
    {
        [HarmonyPatch("Awake")]
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void AfterAwake(EnemyDirector __instance)
        {
            if (!ModConfig.DumpEnemyMobKeysOnce.Value || __instance == null)
                return;

            try
            {
                var setups = CollectEnemySetupsViaReflection(__instance, typeof(EnemySetup));
                Plugin.Log.LogInfo($"[SpawnOverride:Dump] {setups.Count} setup(s) — sous-chaine du blob a utiliser dans level_enemy_overrides.json :");
                for (var i = 0; i < setups.Count; i++)
                    Plugin.Log.LogInfo($"[SpawnOverride:Dump]  [{i}] {BuildMobSearchBlob(setups[i])}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[SpawnOverride:Dump] {e.Message}");
            }
            finally
            {
                ModConfig.DumpEnemyMobKeysOnce.Value = false;
                Plugin.Instance?.Config.Save();
            }
        }
    }
}
