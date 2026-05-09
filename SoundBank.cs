using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx.Logging;
using UnityEngine;

namespace NobleMod;

internal enum SoundReplacementResolve
{
    /// <summary>Aucune regle ne correspond : comportement discovery / vanilla implicite.</summary>
    NoMatch,
    /// <summary>Regle ponderee : ce tirage garde le clip vanilla.</summary>
    KeepVanilla,
    /// <summary>Remplacer par <c>replacementClip</c>.</summary>
    Replace
}

internal static class SoundBank
{

    private sealed class ClipTreeNode
    {
        public readonly Dictionary<string, ClipTreeNode> Children = new Dictionary<string, ClipTreeNode>();
        public readonly List<string> Clips = new List<string>();
    }

    private readonly struct PickerSlot
    {
        public readonly bool IsVanilla;
        public readonly AudioClip Clip;

        public PickerSlot(bool isVanilla, AudioClip clip)
        {
            IsVanilla = isVanilla;
            Clip = clip;
        }
    }

    /// <summary>Tirage pondere (poids normalises au chargement). Une lecture = un Pick().</summary>
    private sealed class WeightedClipPicker
    {
        private readonly PickerSlot[] _slots;
        private readonly float[] _cumulative;

        public WeightedClipPicker(PickerSlot[] slots, float[] weightsNormalized)
        {
            _slots = slots;
            _cumulative = new float[slots.Length];
            var acc = 0f;
            for (var i = 0; i < weightsNormalized.Length; i++)
            {
                acc += weightsNormalized[i];
                _cumulative[i] = acc;
            }
        }

        /// <summary>Tirage pondere ; logs optionnels via <see cref="ModConfig.LogWeightedSoundPicks"/>.</summary>
        public ReplacementPick Pick(string vanillaClipNameHeard, string ruleLabel)
        {
            if (!TryPickCore(out var pick, out var r, out var slotIndex))
                return default;

            if (ModConfig.LogWeightedSoundPicks.Value && _logger != null)
                LogPick(vanillaClipNameHeard, ruleLabel, pick, r, slotIndex);

            return pick;
        }

        private bool TryPickCore(out ReplacementPick pick, out float r, out int slotIndex)
        {
            pick = default;
            r = 0f;
            slotIndex = 0;
            if (_slots == null || _slots.Length == 0)
                return false;
            if (_slots.Length == 1)
            {
                slotIndex = 0;
                r = float.NaN;
                var s = _slots[0];
                pick = s.IsVanilla ? new ReplacementPick(true, null) : new ReplacementPick(false, s.Clip);
                return true;
            }

            r = UnityEngine.Random.value;
            for (var i = 0; i < _cumulative.Length; i++)
            {
                if (r < _cumulative[i])
                {
                    slotIndex = i;
                    var s = _slots[i];
                    pick = s.IsVanilla ? new ReplacementPick(true, null) : new ReplacementPick(false, s.Clip);
                    return true;
                }
            }

            slotIndex = _slots.Length - 1;
            var last = _slots[_slots.Length - 1];
            pick = last.IsVanilla ? new ReplacementPick(true, null) : new ReplacementPick(false, last.Clip);
            return true;
        }

        private void LogPick(string vanillaClipNameHeard, string ruleLabel, ReplacementPick pick, float r, int slotIndex)
        {
            var outcome = pick.KeepVanilla ? "vanilla" : (pick.Clip != null ? pick.Clip.name : "(null)");
            if (_slots.Length == 1)
            {
                _logger.LogInfo(
                    $"[Tirage] vanilla lu='{vanillaClipNameHeard}' regle={ruleLabel} | 1 seul creneau → {outcome}");
                return;
            }

            var sb = new StringBuilder(256);
            sb.Append("[Tirage] vanilla lu='").Append(vanillaClipNameHeard).Append("' regle=").Append(ruleLabel);
            sb.Append(" | r=").Append(r.ToString("F4")).Append(" | creneaux cumules: ");
            var lo = 0f;
            for (var i = 0; i < _cumulative.Length; i++)
            {
                var hi = _cumulative[i];
                var s = _slots[i];
                var label = s.IsVanilla ? "vanilla" : (s.Clip != null ? s.Clip.name : "?");
                if (i > 0)
                    sb.Append("; ");
                sb.Append('[').Append(lo.ToString("F3")).Append(',').Append(hi.ToString("F3")).Append(")=").Append(label);
                lo = hi;
            }

            sb.Append(" | choix=slot#").Append(slotIndex).Append(" → ").Append(outcome);
            _logger.LogInfo(sb.ToString());
        }
    }

    private readonly struct ReplacementPick
    {
        public readonly bool KeepVanilla;
        public readonly AudioClip Clip;

        public ReplacementPick(bool keepVanilla, AudioClip clip)
        {
            KeepVanilla = keepVanilla;
            Clip = clip;
        }
    }

    private static readonly Dictionary<string, WeightedClipPicker> ClipsByVanillaName = new Dictionary<string, WeightedClipPicker>();
    private static readonly List<KeyValuePair<string, WeightedClipPicker>> ContainsRules = new List<KeyValuePair<string, WeightedClipPicker>>();
    private static readonly HashSet<AudioClip> ManagedClips = new HashSet<AudioClip>();
    private static readonly HashSet<string> UnknownLoggedOnce = new HashSet<string>();
    private static readonly HashSet<string> DiscoveredClipNames = new HashSet<string>();
    private static ManualLogSource _logger;
    private static string _customSoundsRootPath;
    private static string _discoveredOutputRootPath;

    /// <summary>
    /// Plusieurs prefixes Harmony peuvent traiter la meme lecture audio dans la meme frame ; sans cache,
    /// chaque appel retirerait au sort (vanilla vs custom). Cle : instance du clip vanilla Unity.
    /// </summary>
    private static int _resolveCacheFrame = -1;
    private static readonly Dictionary<int, CachedResolveEntry> _resolveCacheByClipId = new Dictionary<int, CachedResolveEntry>();
    private static readonly HashSet<int> _resolveCacheDebugOncePerClipThisFrame = new HashSet<int>();

    private struct CachedResolveEntry
    {
        public SoundReplacementResolve Kind;
        public AudioClip ReplacementClip;
    }

    /// <summary>Dernier nom de clip vanilla entendu sur cette source (apres un remplacement, <c>clip</c> reste souvent le custom).</summary>
    private static ConditionalWeakTable<AudioSource, string> _audioSourceToVanillaClipName = new ConditionalWeakTable<AudioSource, string>();

    /// <summary>Derniere reference Unity vue pour un nom exact (pour restaurer le vanilla au tirage).</summary>
    private static readonly Dictionary<string, AudioClip> VanillaClipRefByExactName = new Dictionary<string, AudioClip>(StringComparer.Ordinal);

    /// <summary>
    /// Un seul resultat de re-tirage par <see cref="AudioSource"/> tant que le jeu ne remet pas un clip vanilla
    /// (sinon <see cref="PlayLoop"/> / hooks repetés retiraient au sort a chaque frame → ~poids vanilla chaque fois).
    /// </summary>
    private static ConditionalWeakTable<AudioSource, ManagedRerollSticky> _managedRerollStickyBySource =
        new ConditionalWeakTable<AudioSource, ManagedRerollSticky>();

    private sealed class ManagedRerollSticky
    {
        public AudioClip ChosenClip;
    }

    public static bool IsAudioSourceInFilterScope(AudioSource source)
    {
        if (source == null)
            return false;
        var filter = ModConfig.AudioSourceHierarchyFilter.Value;
        if (string.IsNullOrWhiteSpace(filter))
            return true;
        var tr = source.transform;
        while (tr != null)
        {
            if (tr.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            tr = tr.parent;
        }

        return false;
    }

    /// <summary>Appele quand <paramref name="vanillaClip"/> est un asset vanilla (pas un de nos remplacements).</summary>
    public static void RememberVanillaPlaybackContext(AudioSource source, AudioClip vanillaClip)
    {
        if (source == null || vanillaClip == null || IsManagedClip(vanillaClip))
            return;
        var name = vanillaClip.name;
        if (string.IsNullOrEmpty(name))
            return;
        _audioSourceToVanillaClipName.Remove(source);
        _audioSourceToVanillaClipName.Add(source, name);
        VanillaClipRefByExactName[name] = vanillaClip;
        _managedRerollStickyBySource.Remove(source);
    }

    /// <summary>
    /// Le jeu laisse souvent notre <see cref="AudioClip"/> custom sur la source : sans ca, <see cref="IsManagedClip"/>
    /// court-circuite et le tirage vanilla ne peut plus se produire.
    /// </summary>
    public static bool TryRerollManagedClipOnSource(AudioSource source, ref AudioClip clip)
    {
        if (source == null || clip == null || !IsManagedClip(clip))
            return false;
        if (!IsAudioSourceInFilterScope(source))
            return false;
        if (!_audioSourceToVanillaClipName.TryGetValue(source, out var vanillaName) || string.IsNullOrEmpty(vanillaName))
            return false;
        if (!VanillaClipRefByExactName.TryGetValue(vanillaName, out var vanillaRef) || vanillaRef == null)
            return false;

        if (_managedRerollStickyBySource.TryGetValue(source, out var sticky) && sticky.ChosenClip != null)
        {
            if (ModConfig.LogWeightedSoundPicks.Value && _logger != null)
            {
                _logger.LogDebug(
                    $"[Tirage] sticky AudioSource id={source.GetInstanceID()} ref vanilla='{vanillaName}' → garde '{sticky.ChosenClip.name}' (pas de nouveau Random jusqu'a un clip vanilla sur cette source)");
            }

            clip = sticky.ChosenClip;
            return true;
        }

        var resolve = TryResolveReplacement(vanillaName, clip, out var replacementClip);
        AudioClip chosen;
        if (resolve == SoundReplacementResolve.KeepVanilla || resolve == SoundReplacementResolve.NoMatch)
            chosen = vanillaRef;
        else if (resolve == SoundReplacementResolve.Replace && replacementClip != null)
            chosen = replacementClip;
        else
            return false;

        clip = chosen;
        _managedRerollStickyBySource.Remove(source);
        _managedRerollStickyBySource.Add(source, new ManagedRerollSticky { ChosenClip = chosen });
        return true;
    }

    public static void Initialize(ManualLogSource logger)
    {
        _logger = logger;
        Reload();
    }

    public static bool IsManagedClip(AudioClip clip)
    {
        return clip != null && ManagedClips.Contains(clip);
    }

    /// <param name="vanillaClipAsset">Clip vanilla en cours (pour un seul tirage par frame / par asset). Null = pas de cache.</param>
    public static SoundReplacementResolve TryResolveReplacement(
        string vanillaClipName,
        AudioClip vanillaClipAsset,
        out AudioClip replacementClip)
    {
        replacementClip = null;
        if (string.IsNullOrWhiteSpace(vanillaClipName))
            return SoundReplacementResolve.NoMatch;

        if (vanillaClipAsset != null)
        {
            var frame = Time.frameCount;
            if (_resolveCacheFrame != frame)
            {
                _resolveCacheByClipId.Clear();
                _resolveCacheDebugOncePerClipThisFrame.Clear();
                _resolveCacheFrame = frame;
            }

            var id = vanillaClipAsset.GetInstanceID();
            if (_resolveCacheByClipId.TryGetValue(id, out var cached))
            {
                if (ModConfig.LogWeightedSoundPicks.Value && _logger != null &&
                    _resolveCacheDebugOncePerClipThisFrame.Add(id))
                {
                    var desc = cached.Kind switch
                    {
                        SoundReplacementResolve.KeepVanilla => "KeepVanilla",
                        SoundReplacementResolve.Replace => $"Replace → {(cached.ReplacementClip != null ? cached.ReplacementClip.name : "?")}",
                        _ => "NoMatch"
                    };
                    _logger.LogDebug(
                        $"[Tirage] cache meme frame (instance clip vanilla id={id}) → reutilise {desc} (plusieurs hooks ont appele TryResolveReplacement sans nouveau tirage)");
                }

                replacementClip = cached.ReplacementClip;
                return cached.Kind;
            }

            var kind = TryResolveReplacementCore(vanillaClipName, out replacementClip);
            _resolveCacheByClipId[id] = new CachedResolveEntry { Kind = kind, ReplacementClip = replacementClip };
            return kind;
        }

        return TryResolveReplacementCore(vanillaClipName, out replacementClip);
    }

    private static SoundReplacementResolve TryResolveReplacementCore(string vanillaClipName, out AudioClip replacementClip)
    {
        replacementClip = null;
        if (ClipsByVanillaName.TryGetValue(vanillaClipName, out var exactPicker))
            return ResolvePick(exactPicker.Pick(vanillaClipName, $"exact:{vanillaClipName}"), out replacementClip);

        for (var i = 0; i < ContainsRules.Count; i++)
        {
            var rule = ContainsRules[i];
            if (vanillaClipName.IndexOf(rule.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                return ResolvePick(rule.Value.Pick(vanillaClipName, $"contains:{rule.Key}"), out replacementClip);
        }

        return SoundReplacementResolve.NoMatch;
    }

    private static SoundReplacementResolve ResolvePick(ReplacementPick pick, out AudioClip replacementClip)
    {
        replacementClip = null;
        if (pick.KeepVanilla)
            return SoundReplacementResolve.KeepVanilla;
        replacementClip = pick.Clip;
        return replacementClip != null ? SoundReplacementResolve.Replace : SoundReplacementResolve.NoMatch;
    }

    public static bool ShouldLogUnknownClip(string vanillaClipName)
    {
        if (string.IsNullOrWhiteSpace(vanillaClipName))
            return false;

        return UnknownLoggedOnce.Add(vanillaClipName);
    }

    private static void Reload()
    {
        ClipsByVanillaName.Clear();
        ContainsRules.Clear();
        ManagedClips.Clear();
        UnknownLoggedOnce.Clear();
        DiscoveredClipNames.Clear();
        _resolveCacheFrame = -1;
        _resolveCacheByClipId.Clear();
        _resolveCacheDebugOncePerClipThisFrame.Clear();
        VanillaClipRefByExactName.Clear();
        _audioSourceToVanillaClipName = new ConditionalWeakTable<AudioSource, string>();
        _managedRerollStickyBySource = new ConditionalWeakTable<AudioSource, ManagedRerollSticky>();

        var root = Path.Combine(Plugin.AssemblyDirectory, "CustomSounds");
        _customSoundsRootPath = root;
        var configuredDiscovered = ModConfig.DiscoveredClipsOutputPath.Value?.Trim();
        _discoveredOutputRootPath = string.IsNullOrWhiteSpace(configuredDiscovered) ? root : configuredDiscovered;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(_discoveredOutputRootPath);
        LoadExistingDiscoveredClips();
        var mappingFilePath = Path.Combine(root, "replacements.json");

        if (!File.Exists(mappingFilePath))
        {
            WriteDefaultMappingTemplate(mappingFilePath);
            _logger.LogWarning($"Missing mapping file: {mappingFilePath}. Template created.");
            return;
        }

        List<ReplacementsJsonParser.Entry> entries;
        try
        {
            var json = File.ReadAllText(mappingFilePath, Encoding.UTF8);
            entries = ReplacementsJsonParser.Parse(json);
        }
        catch (Exception e)
        {
            _logger.LogError($"replacements.json invalide : {e.Message}");
            return;
        }

        if (entries == null || entries.Count == 0)
        {
            _logger.LogWarning($"No valid mapping entry found in {mappingFilePath}");
            return;
        }

        var byKey = new Dictionary<string, ReplacementsJsonParser.Entry>(StringComparer.Ordinal);
        foreach (var e in entries)
            byKey[e.MappingKey] = e;

        foreach (var kv in byKey)
            Register(root, kv.Value);
    }

    public static void TrackDiscoveredClip(string vanillaClipName)
    {
        if (string.IsNullOrWhiteSpace(vanillaClipName))
            return;

        if (!DiscoveredClipNames.Add(vanillaClipName))
            return;

        if (!ModConfig.WriteDiscoveredClipsHierarchyJson.Value)
            return;

        try
        {
            if (string.IsNullOrWhiteSpace(_discoveredOutputRootPath))
                return;

            Directory.CreateDirectory(_discoveredOutputRootPath);
            var fileName = ModConfig.DiscoveredClipsHierarchyFileName.Value;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "discovered_clips_hierarchy.json";

            var discoveredFilePath = Path.Combine(_discoveredOutputRootPath, fileName);
            SaveDiscoveredHierarchyJson(discoveredFilePath);
        }
        catch (System.Exception e)
        {
            _logger.LogWarning($"Failed to write discovered clip '{vanillaClipName}': {e.Message}");
        }
    }

    private static void Register(string root, ReplacementsJsonParser.Entry entry)
    {
        var mappingKey = entry.MappingKey;
        var ruleMode = "exact";
        var ruleValue = mappingKey;
        if (mappingKey.StartsWith("contains:", StringComparison.OrdinalIgnoreCase))
        {
            ruleMode = "contains";
            ruleValue = mappingKey.Substring("contains:".Length).Trim();
        }
        else if (mappingKey.StartsWith("exact:", StringComparison.OrdinalIgnoreCase))
        {
            ruleMode = "exact";
            ruleValue = mappingKey.Substring("exact:".Length).Trim();
        }

        if (string.IsNullOrWhiteSpace(ruleValue))
        {
            _logger.LogWarning($"Ignoring empty mapping key: '{mappingKey}'");
            return;
        }

        if (entry.Variants == null || entry.Variants.Count == 0)
        {
            _logger.LogWarning($"No variants for mapping key: '{mappingKey}'");
            return;
        }

        var rawWeights = new List<float>();
        var slots = new List<PickerSlot>();
        var descParts = new List<string>();
        foreach (var v in entry.Variants)
        {
            if (v.WeightPercent < 0f)
            {
                _logger.LogWarning($"Negative weight ignored under '{mappingKey}'");
                continue;
            }

            if (v.IsVanilla)
            {
                rawWeights.Add(v.WeightPercent);
                slots.Add(new PickerSlot(true, null));
                descParts.Add($"vanilla ({v.WeightPercent}%)");
                continue;
            }

            if (string.IsNullOrWhiteSpace(v.File))
                continue;

            var path = Path.Combine(root, v.File.Trim());
            if (!File.Exists(path))
            {
                _logger.LogWarning($"Missing sound file: {path} (cle '{mappingKey}')");
                continue;
            }

            var clip = ClipFileLoader.LoadFromPath(path);
            if (clip == null)
            {
                _logger.LogWarning($"Failed to load clip: {path}");
                continue;
            }

            clip.name = $"noble_{v.File}";
            rawWeights.Add(v.WeightPercent);
            slots.Add(new PickerSlot(false, clip));
            ManagedClips.Add(clip);
            descParts.Add($"{v.File} ({v.WeightPercent}%)");
        }

        if (slots.Count == 0)
        {
            _logger.LogWarning($"No loadable variants for '{mappingKey}'");
            return;
        }

        var sum = rawWeights.Sum();
        var normalized = new float[slots.Count];
        if (sum <= 0f)
        {
            _logger.LogWarning($"Sum of weights is 0 for '{mappingKey}', using equal shares.");
            var eq = 1f / slots.Count;
            for (var i = 0; i < slots.Count; i++)
                normalized[i] = eq;
        }
        else
        {
            if (Math.Abs(sum - 100f) > 0.01f)
                _logger.LogInfo($"Weights for '{mappingKey}' sum to {sum:0.##}% (not 100); normalizing.");
            var inv = 1f / sum;
            for (var i = 0; i < slots.Count; i++)
                normalized[i] = rawWeights[i] * inv;
        }

        var picker = new WeightedClipPicker(slots.ToArray(), normalized);
        if (ruleMode == "contains")
            ContainsRules.Add(new KeyValuePair<string, WeightedClipPicker>(ruleValue, picker));
        else
            ClipsByVanillaName[ruleValue] = picker;

        var filesDesc = string.Join(", ", descParts);
        _logger.LogInfo($"Loaded replacement [{ruleMode}] '{ruleValue}' <- {filesDesc}");
    }

    private static void WriteDefaultMappingTemplate(string mappingFilePath)
    {
        const string template = """
{
  "vanilla_clip_name_exact": "mon_son.ogg",
  "contains:footstep": [
    { "file": "pas_a.ogg", "weight": 50 },
    { "vanilla": true, "weight": 25 },
    { "file": "pas_b.ogg", "weight": 25 }
  ]
}
""";
        File.WriteAllText(mappingFilePath, template, Encoding.UTF8);
    }

    private static void LoadExistingDiscoveredClips()
    {
        try
        {
            if (!ModConfig.WriteDiscoveredClipsHierarchyJson.Value)
                return;

            var fileName = ModConfig.DiscoveredClipsHierarchyFileName.Value;
            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "discovered_clips_hierarchy.json";

            var discoveredFilePath = Path.Combine(_discoveredOutputRootPath, fileName);
            if (!File.Exists(discoveredFilePath))
                return;

            var json = File.ReadAllText(discoveredFilePath, Encoding.UTF8);
            LoadDiscoveredNamesFromHierarchyJson(json);
        }
        catch (System.Exception e)
        {
            _logger.LogWarning($"Failed to load discovered clips file: {e.Message}");
        }
    }

    /// <summary>Format actuel : <c>"name": "nom complet"</c> (plusieurs noms rares : separes par <c> | </c>). Legacy : <c>"__clips": ["..."]</c>.</summary>
    private static void LoadDiscoveredNamesFromHierarchyJson(string json)
    {
        foreach (Match m in Regex.Matches(json, "\"name\"\\s*:\\s*\"(?<v>(?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.IgnoreCase))
        {
            var raw = UnescapeJsonString(m.Groups["v"].Value);
            foreach (var piece in raw.Split(new[] { " | " }, StringSplitOptions.RemoveEmptyEntries))
            {
                var clipName = piece.Trim();
                if (!string.IsNullOrWhiteSpace(clipName))
                    DiscoveredClipNames.Add(clipName);
            }
        }

        if (json.IndexOf("\"__clips\"", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        foreach (Match block in Regex.Matches(json, "\"__clips\"\\s*:\\s*\\[(?<arr>[^\\]]*)\\]", RegexOptions.IgnoreCase))
        {
            var inner = block.Groups["arr"].Value;
            foreach (Match m in Regex.Matches(inner, "\"(?<v>(?:[^\"\\\\]|\\\\.)*)\""))
            {
                var clipName = UnescapeJsonString(m.Groups["v"].Value).Trim();
                if (!string.IsNullOrWhiteSpace(clipName))
                    DiscoveredClipNames.Add(clipName);
            }
        }
    }

    private static string JsonEscapeString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string UnescapeJsonString(string s)
    {
        if (string.IsNullOrEmpty(s))
            return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length)
            {
                sb.Append(s[i]);
                continue;
            }

            i++;
            switch (s[i])
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                default: sb.Append(s[i]); break;
            }
        }

        return sb.ToString();
    }

    private static void SaveDiscoveredHierarchyJson(string outputPath)
    {
        var root = new ClipTreeNode();
        foreach (var fullClipName in DiscoveredClipNames)
        {
            var tokens = GetSmartTokens(fullClipName);
            if (tokens.Count == 0)
                continue;

            var node = root;
            for (var i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (!node.Children.TryGetValue(token, out var child))
                {
                    child = new ClipTreeNode();
                    node.Children[token] = child;
                }

                node = child;
            }

            if (!node.Clips.Contains(fullClipName))
                node.Clips.Add(fullClipName);
        }

        var sb = new StringBuilder();
        WriteNodeJson(sb, root, 0);
        File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
    }

    private static List<string> GetSmartTokens(string clipName)
    {
        var tokens = new List<string>();
        var matches = Regex.Matches(clipName.ToLowerInvariant(), "[a-z0-9]+");
        foreach (Match match in matches)
        {
            var token = match.Value.Trim();
            if (!string.IsNullOrWhiteSpace(token))
                tokens.Add(token);
        }

        return tokens;
    }

    private static void WriteNodeJson(StringBuilder sb, ClipTreeNode node, int indent)
    {
        var indentStr = new string(' ', indent * 2);
        var childIndentStr = new string(' ', (indent + 1) * 2);
        sb.AppendLine("{");

        var items = new List<string>(node.Children.Keys);
        items.Sort(System.StringComparer.Ordinal);
        var first = true;

        for (var i = 0; i < items.Count; i++)
        {
            if (!first) sb.AppendLine(",");
            first = false;
            var key = items[i];
            sb.Append(childIndentStr).Append('\"').Append(key).Append("\": ");
            WriteNodeJson(sb, node.Children[key], indent + 1);
        }

        if (node.Clips.Count > 0)
        {
            if (!first) sb.AppendLine(",");
            first = false;
            node.Clips.Sort(System.StringComparer.Ordinal);
            var leafName = node.Clips.Count == 1
                ? node.Clips[0]
                : string.Join(" | ", node.Clips);
            sb.Append(childIndentStr).Append("\"name\": \"").Append(JsonEscapeString(leafName)).Append('\"');
        }

        if (!first) sb.AppendLine();
        sb.Append(indentStr).Append("}");
    }
}
