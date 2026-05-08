using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace NobleMod.Patches;

[HarmonyPatch]
internal static class LevelEnemyOverridePatches
{
    private static readonly HashSet<int> LoggedLevelNoRule = new HashSet<int>();
    private static readonly HashSet<string> LoggedFallbacks = new HashSet<string>();
    private static readonly HashSet<int> AppliedOverrideLevels = new HashSet<int>();

    static MethodBase TargetMethod()
    {
        var enemyDirectorType = AccessTools.TypeByName("EnemyDirector");
        if (enemyDirectorType == null)
            return null;
        return AccessTools.Method(enemyDirectorType, "GetEnemy");
    }

    [HarmonyPostfix]
    private static void AfterGetEnemy(MethodBase __originalMethod, object __instance, ref object __result)
    {
        if (__instance == null)
            return;

        try
        {
            if (!ModConfig.EnableSpawnOverrides.Value)
                return;
            if (__result == null)
                return;
            if (!LooksLikeEnemySetupResult(__result))
                return;

            var levelNumber = TryGetCurrentLevelNumber();
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

            var replacementSetup = FindBestSetupMatch(__instance, desiredMobKey, __result.GetType());
            if (replacementSetup == null)
            {
                if (ModConfig.LogSpawnOverrides.Value)
                {
                    var fallbackKey = $"{levelNumber}:{desiredMobKey}";
                    if (LoggedFallbacks.Add(fallbackKey))
                        Plugin.Log.LogWarning($"[SpawnOverride] Niveau {levelNumber}: mob '{desiredMobKey}' introuvable, fallback vanilla conserve.");
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

    private static int TryGetCurrentLevelNumber()
    {
        try
        {
            var runManagerType = AccessTools.TypeByName("RunManager");
            if (runManagerType == null)
                return 0;

            var instanceField = AccessTools.Field(runManagerType, "instance");
            var runManager = instanceField?.GetValue(null);
            if (runManager == null)
                return 0;

            var levelsCompletedObj = Traverse.Create(runManager).Field("levelsCompleted").GetValue();
            if (levelsCompletedObj == null)
                return 0;

            var levelsCompleted = Convert.ToInt32(levelsCompletedObj);
            return levelsCompleted + 1;
        }
        catch
        {
            return 0;
        }
    }

    private static object FindBestSetupMatch(object enemyDirector, string desiredMobKey, Type expectedSetupType)
    {
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
        var blob = BuildSearchBlob(setup);
        return blob.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool SetupMatchesAlias(object setup, string token)
    {
        var blob = BuildSearchBlob(setup).ToLowerInvariant();
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

    private static string BuildSearchBlob(object setup)
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
                var tr = Traverse.Create(spawnObj);
                var prefabName = tr.Field("prefabName").GetValue<string>();
                var resourcePath = tr.Field("resourcePath").GetValue<string>();
                if (!string.IsNullOrWhiteSpace(prefabName))
                    pieces.Add(prefabName);
                if (!string.IsNullOrWhiteSpace(resourcePath))
                    pieces.Add(resourcePath);
            }
        }

        return string.Join(" ", pieces);
    }
}
