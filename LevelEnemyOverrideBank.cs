using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Logging;

namespace NobleMod;

internal static class LevelEnemyOverrideBank
{
    private static readonly Dictionary<int, string> MobByLevel = new Dictionary<int, string>();
    private static ManualLogSource _logger;
    private static string _spawnConfigRootPath;

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        Reload();
    }

    public static bool TryGetMobKey(int levelNumber, out string mobKey)
    {
        if (MobByLevel.TryGetValue(levelNumber, out mobKey) && !string.IsNullOrWhiteSpace(mobKey))
            return true;

        mobKey = null;
        return false;
    }

    private static void Reload()
    {
        MobByLevel.Clear();
        _spawnConfigRootPath = Path.Combine(Paths.PluginPath, PluginInfo.Name, "SpawnConfig");
        Directory.CreateDirectory(_spawnConfigRootPath);

        var fileName = ModConfig.SpawnOverridesFileName.Value;
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = "level_enemy_overrides.json";
        var mappingPath = Path.Combine(_spawnConfigRootPath, fileName);

        if (!File.Exists(mappingPath))
        {
            WriteDefaultTemplate(mappingPath);
            _logger.LogWarning($"Missing spawn override file: {mappingPath}. Template created.");
            return;
        }

        try
        {
            var json = File.ReadAllText(mappingPath, Encoding.UTF8);
            // Accept keys like "1", 1, "level_1", "level1", "lvl1".
            var matches = Regex.Matches(
                json,
                "(?:\"(?<key1>[^\"]+)\"|(?<key2>\\d+))\\s*:\\s*\"(?<value>[^\"]+)\"",
                RegexOptions.IgnoreCase
            );

            foreach (Match match in matches)
            {
                var rawKey = match.Groups["key1"].Success
                    ? match.Groups["key1"].Value
                    : match.Groups["key2"].Value;
                var mobKey = match.Groups["value"].Value.Trim();
                if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(mobKey))
                    continue;

                if (!TryParseLevelKey(rawKey, out var levelNumber) || levelNumber <= 0)
                    continue;

                MobByLevel[levelNumber] = NormalizeMobKey(mobKey);
            }

            if (ModConfig.LogSpawnOverrides.Value)
                _logger.LogInfo($"Loaded {MobByLevel.Count} level mob override(s) from '{mappingPath}'.");
        }
        catch (System.Exception e)
        {
            _logger.LogError($"Failed to parse spawn override file '{mappingPath}': {e.Message}");
        }
    }

    private static bool TryParseLevelKey(string rawKey, out int levelNumber)
    {
        levelNumber = 0;
        if (string.IsNullOrWhiteSpace(rawKey))
            return false;

        var digitMatch = Regex.Match(rawKey, "\\d+");
        if (!digitMatch.Success)
            return false;

        return int.TryParse(digitMatch.Value, out levelNumber);
    }

    private static string NormalizeMobKey(string value)
    {
        return value.Trim().ToLowerInvariant();
    }

    private static void WriteDefaultTemplate(string outputPath)
    {
        const string template =
            "{\n" +
            "  \"1\": \"huntsman\",\n" +
            "  \"2\": \"headman\"\n" +
            "}\n";
        File.WriteAllText(outputPath, template, Encoding.UTF8);
    }
}
