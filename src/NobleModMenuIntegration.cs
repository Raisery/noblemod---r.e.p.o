using BepInEx.Logging;
using NobleMod.Replacers;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace NobleMod;

/// <summary>
/// Ajoute un bouton dans le menu Parametres du jeu via <see href="https://thunderstore.io/c/repo/p/nickklmao/MenuLib/">MenuLib</see>
/// (<c>MenuAPI.AddElementToSettingsMenu</c>). Reflexion uniquement : pas de reference de build a MenuLib.dll.
/// </summary>
internal static class NobleModMenuIntegration
{
    private static Assembly _menuLib;
    private static Type _menuApiType;
    private static MethodInfo _miCreateButton;
    private static MethodInfo _miCreateToggle;
    private static MethodInfo _miCreatePopup;
    private static Type _presetSideType;
    private static object _popup;
    private static object _toggleSpawn;
    private static object _toggleSoundPackConditions;
    private static object _toggleLogSoundPickEachMatch;
    private static readonly List<KeyValuePair<string, object>> _replacerToggles = new();
    private static bool _methodsResolved;

    private static void ClearNoblePopupCache()
    {
        _popup = null;
        _toggleSpawn = null;
        _toggleSoundPackConditions = null;
        _toggleLogSoundPickEachMatch = null;
        _replacerToggles.Clear();
    }

    /// <summary>
    /// Les refs sont en <c>object</c> : apres <c>Destroy</c>, le pointeur managed reste non-null.
    /// Seul <see cref="UnityEngine.Object"/> detecte correctement l'objet detruit.
    /// </summary>
    private static bool IsUnityObjAlive(object u) => u is UnityEngine.Object o && o != null;

    private static object GetRepoPopupMenuPage(object repoPopupPage)
    {
        if (repoPopupPage == null)
            return null;
        var f = repoPopupPage.GetType().GetField("menuPage", BindingFlags.Public | BindingFlags.Instance);
        return f?.GetValue(repoPopupPage);
    }

    internal static void TryRegister(ManualLogSource log)
    {
        try
        {
            _menuLib = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a =>
                string.Equals(a.GetName().Name, "MenuLib", StringComparison.Ordinal));
            if (_menuLib == null)
            {
                log.LogWarning(
                    "[NobleMod] MenuLib introuvable. Installez la dependance 'MenuLib' (Thunderstore, auteur nickklmao). " +
                    "Le bouton NobleMod dans Parametres ne sera pas ajoute.");
                return;
            }

            _menuApiType = _menuLib.GetType("MenuLib.MenuAPI", throwOnError: false);
            if (_menuApiType == null)
            {
                log.LogError("[NobleMod] Type MenuLib.MenuAPI introuvable.");
                return;
            }

            var add = _menuApiType.GetMethod("AddElementToSettingsMenu", BindingFlags.Public | BindingFlags.Static);
            if (add == null)
            {
                log.LogError("[NobleMod] MenuAPI.AddElementToSettingsMenu introuvable.");
                return;
            }

            var delegateType = add.GetParameters()[0].ParameterType;
            var handler = typeof(NobleModMenuIntegration).GetMethod(
                nameof(OnSettingsMenuBuilt),
                BindingFlags.NonPublic | BindingFlags.Static);
            var del = Delegate.CreateDelegate(delegateType, handler);
            add.Invoke(null, new object[] { del });
            log.LogInfo("[NobleMod] MenuLib: bouton ajoute au menu Parametres du jeu.");
        }
        catch (Exception e)
        {
            log.LogError($"[NobleMod] Integration MenuLib: {e}");
        }
    }

    private static void OnSettingsMenuBuilt(Transform parent)
    {
        try
        {
            ResolveMenuApiMethods();
            var controlsRow = FindSettingsLeftNavControlsRow(parent);
            if (controlsRow != null && controlsRow.parent != null)
            {
                var listParent = controlsRow.parent;
                var btn = _miCreateButton.Invoke(
                    null,
                    new object[]
                    {
                        "NOBLEMOD",
                        (Action)OpenNobleModPopup,
                        listParent,
                        Vector2.zero
                    }) as Component;
                PlaceNobleButtonUnderControlsNav(btn, controlsRow);
            }
            else
            {
                Plugin.Log?.LogWarning("[NobleMod] Entree CONTROLS (liste gauche) introuvable; placement repli sur le scroll.");
                var content = FindMenuScrollScroller(parent) ?? parent;
                var pos = ComputeAppendLocalPosition(content);
                var btn = _miCreateButton.Invoke(
                    null,
                    new object[]
                    {
                        "NobleMod — reglages du mod",
                        (Action)OpenNobleModPopup,
                        content,
                        pos
                    }) as Component;
                btn?.transform.SetAsLastSibling();
            }
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[NobleMod] OnSettingsMenuBuilt: {e}");
        }
    }

    /// <summary>
    /// Trouve la ligne du menu lateral (GAMEPLAY / … / CONTROLS), pas le panneau d’options clavier.
    /// </summary>
    private static Transform FindSettingsLeftNavControlsRow(Transform pageRoot)
    {
        Transform bestRow = null;
        var bestX = float.MaxValue;

        foreach (var tr in pageRoot.GetComponentsInChildren<Transform>(true))
        {
            var tmp = TryGetTmpTextComponent(tr);
            if (tmp == null)
                continue;
            var label = GetTmpText(tmp);
            if (string.IsNullOrEmpty(label))
                continue;
            var t = label.Trim();
            if (!t.Equals("CONTROLS", StringComparison.OrdinalIgnoreCase) &&
                !t.Equals("CONTRÔLES", StringComparison.OrdinalIgnoreCase))
                continue;

            var row = tr.parent;
            if (row == null)
                continue;
            var x = row.position.x;
            if (x < bestX)
            {
                bestX = x;
                bestRow = row;
            }
        }

        return bestRow;
    }

    private static Component TryGetTmpTextComponent(Transform tr)
    {
        var c = tr.GetComponent("TMPro.TextMeshProUGUI");
        if (c != null)
            return c;
        return tr.GetComponent("TextMeshProUGUI");
    }

    private static string GetTmpText(Component tmp)
    {
        var p = tmp.GetType().GetProperty("text");
        return p?.GetValue(tmp) as string;
    }

    /// <summary>
    /// Insere le bouton juste sous CONTROLS (meme parent, entre CONTROLS et BACK).
    /// </summary>
    private static void PlaceNobleButtonUnderControlsNav(Component btnComponent, Transform controlsRow)
    {
        var btnRt = btnComponent?.transform as RectTransform;
        var rowRt = controlsRow as RectTransform;
        var listParent = controlsRow?.parent;
        if (btnRt == null || rowRt == null || listParent == null)
            return;

        var k = controlsRow.GetSiblingIndex();
        RectTransform nextRowBeforeInsert = null;
        if (k + 1 < listParent.childCount)
            nextRowBeforeInsert = listParent.GetChild(k + 1) as RectTransform;

        btnRt.SetSiblingIndex(k + 1);

        btnRt.anchorMin = rowRt.anchorMin;
        btnRt.anchorMax = rowRt.anchorMax;
        btnRt.pivot = rowRt.pivot;
        btnRt.sizeDelta = rowRt.sizeDelta;

        float dy;
        if (k > 0 && listParent.GetChild(k - 1) is RectTransform prevRt)
            dy = rowRt.localPosition.y - prevRt.localPosition.y;
        else if (nextRowBeforeInsert != null)
            dy = (nextRowBeforeInsert.localPosition.y - rowRt.localPosition.y) * 0.5f;
        else
        {
            var h = rowRt.sizeDelta.y > 8f ? rowRt.sizeDelta.y : 40f;
            dy = -(h + 6f);
        }

        btnRt.localPosition = rowRt.localPosition + new Vector3(0f, dy, 0f);

        // Decaler BACK (et le meme pas) pour eviter que NOBLEMOD recouvre l’ancienne place de BACK.
        if (nextRowBeforeInsert != null && Mathf.Abs(dy) > 0.01f)
        {
            var targetY = btnRt.localPosition.y + dy;
            var backRt = nextRowBeforeInsert;
            backRt.localPosition = new Vector3(backRt.localPosition.x, targetY, backRt.localPosition.z);
        }
    }

    /// <summary>
    /// Place le bouton sous les lignes existantes du scroll, avec le meme decalage X que les boutons REPO (~250).
    /// </summary>
    private static Vector2 ComputeAppendLocalPosition(Transform content)
    {
        const float gapBelowExisting = 22f;
        const float defaultX = 250f;
        const float fallbackY = -40f;

        if (content == null)
            return new Vector2(defaultX, fallbackY);

        float minY = float.MaxValue;
        var any = false;
        for (var i = 0; i < content.childCount; i++)
        {
            var child = content.GetChild(i);
            if (child is not RectTransform rt)
                continue;
            if (IsChromeScrollChild(child))
                continue;
            var bottom = GetLocalBottomY(rt, content);
            if (bottom < minY)
            {
                minY = bottom;
                any = true;
            }
        }

        var y = any ? minY - gapBelowExisting : fallbackY;
        return new Vector2(defaultX, y);
    }

    /// <summary>En-tetes / poignees de scroll souvent dans les premiers enfants.</summary>
    private static bool IsChromeScrollChild(Transform child)
    {
        var n = child.name ?? string.Empty;
        if (n.IndexOf("scroll", StringComparison.OrdinalIgnoreCase) >= 0 &&
            n.IndexOf("bar", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return false;
    }

    private static float GetLocalBottomY(RectTransform rt, Transform contentParent)
    {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);
        var minLocalY = float.MaxValue;
        for (var i = 0; i < 4; i++)
        {
            var lp = contentParent.InverseTransformPoint(corners[i]);
            if (lp.y < minLocalY)
                minLocalY = lp.y;
        }

        return minLocalY;
    }

    private static void ResolveMenuApiMethods()
    {
        if (_methodsResolved)
            return;

        foreach (var m in _menuApiType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name == "CreateREPOButton")
            {
                var p = m.GetParameters();
                if (p.Length == 4 &&
                    p[0].ParameterType == typeof(string) &&
                    p[1].ParameterType == typeof(Action) &&
                    p[2].ParameterType == typeof(Transform) &&
                    p[3].ParameterType == typeof(Vector2))
                {
                    _miCreateButton = m;
                }
            }
            else if (m.Name == "CreateREPOToggle")
            {
                var p = m.GetParameters();
                if (p.Length == 7 &&
                    p[0].ParameterType == typeof(string) &&
                    p[1].ParameterType == typeof(Action<bool>) &&
                    p[2].ParameterType == typeof(Transform) &&
                    p[3].ParameterType == typeof(Vector2) &&
                    p[4].ParameterType == typeof(string) &&
                    p[5].ParameterType == typeof(string) &&
                    p[6].ParameterType == typeof(bool))
                {
                    _miCreateToggle = m;
                }
            }
            else if (m.Name == "CreateREPOPopupPage")
            {
                var p = m.GetParameters();
                // string header, PresetSide side, bool shouldCachePage, bool pageDimmer, float spacing = 0
                if (p.Length >= 5 &&
                    p[0].ParameterType == typeof(string) &&
                    p[1].ParameterType.IsEnum &&
                    p[2].ParameterType == typeof(bool) &&
                    p[3].ParameterType == typeof(bool) &&
                    p[4].ParameterType == typeof(float))
                {
                    _miCreatePopup = m;
                    _presetSideType = p[1].ParameterType;
                }
            }
        }

        if (_miCreateButton == null || _miCreateToggle == null || _miCreatePopup == null || _presetSideType == null)
            throw new InvalidOperationException("Signatures MenuAPI inattendues (mettre a jour NobleModMenuIntegration).");

        _methodsResolved = true;
    }

    private static Transform FindMenuScrollScroller(Transform pageRoot)
    {
        foreach (var mb in pageRoot.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || mb.GetType().Name != "MenuScrollBox")
                continue;
            var f = mb.GetType().GetField("scroller", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(mb) is Transform t)
                return t;
        }

        return null;
    }

    private static void OpenNobleModPopup()
    {
        try
        {
            EnsureNoblePopup();
            if (!IsUnityObjAlive(_popup))
            {
                Plugin.Log?.LogError("[NobleMod] Popup NobleMod introuvable apres EnsureNoblePopup.");
                return;
            }

            if (IsUnityObjAlive(_toggleSpawn))
            {
                var setState = _toggleSpawn.GetType().GetMethod("SetState", new[] { typeof(bool), typeof(bool) });
                setState?.Invoke(_toggleSpawn, new object[] { ModConfig.EnableSpawnOverrides.Value, false });
            }

            if (IsUnityObjAlive(_toggleSoundPackConditions))
            {
                var setState2 = _toggleSoundPackConditions.GetType().GetMethod("SetState", new[] { typeof(bool), typeof(bool) });
                setState2?.Invoke(_toggleSoundPackConditions, new object[] { ModConfig.EnableNobleModSoundPackConditions.Value, false });
            }

            if (IsUnityObjAlive(_toggleLogSoundPickEachMatch))
            {
                var setState3 = _toggleLogSoundPickEachMatch.GetType().GetMethod("SetState", new[] { typeof(bool), typeof(bool) });
                setState3?.Invoke(_toggleLogSoundPickEachMatch, new object[] { ModConfig.LogSoundPickEachMatch.Value, false });
            }

            // Resynchronisation des toggles replacers : si l'utilisateur a edite le .cfg a la main,
            // on reflete la valeur courante du ConfigEntry sur le toggle UI.
            foreach (var kv in _replacerToggles)
            {
                if (!IsUnityObjAlive(kv.Value))
                    continue;
                var setStateR = kv.Value.GetType().GetMethod("SetState", new[] { typeof(bool), typeof(bool) });
                if (setStateR == null)
                    continue;
                var entries = ReplacerToggleRegistry.SnapshotEntries();
                foreach (var e in entries)
                {
                    if (!string.Equals(e.FilePath, kv.Key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    setStateR.Invoke(kv.Value, new object[] { e.Entry.Value, false });
                    break;
                }
            }

            // false = remplace la page courante (Parametres) en Inactive : sinon le menu derriere reste
            // interactif (hover/clics) et vole les raycasts des toggles NobleMod.
            var open = _popup.GetType().GetMethod("OpenPage", new[] { typeof(bool) });
            open?.Invoke(_popup, new object[] { false });
        }
        catch (Exception e)
        {
            Plugin.Log?.LogError($"[NobleMod] OpenNobleModPopup: {e}");
        }
    }

    private static void EnsureNoblePopup()
    {
        // Les packs peuvent etre charges apres NobleMod.Awake ; on re-scanne avant d'utiliser le cache UI.
        ReplacerToggleRegistry.DiscoverAlreadyLoadedPacks();
        var replacerCount = ReplacerToggleRegistry.SnapshotEntries().Count;

        if (IsUnityObjAlive(_popup) && IsUnityObjAlive(GetRepoPopupMenuPage(_popup)))
        {
            // Popup creee alors qu'aucun replacer n'etait encore decouvert (reflection LoadedPacks echouait ou timing) :
            // on detruit le cache pour recreer les toggles dynamiques.
            if (replacerCount > 0 && _replacerToggles.Count == 0)
                ClearNoblePopupCache();
            else
                return;
        }

        ClearNoblePopupCache();

        ResolveMenuApiMethods();
        var left = Enum.Parse(_presetSideType, "Left");
        _popup = _miCreatePopup.Invoke(null, new object[] { "NobleMod", left, true, true, 0f });

        var popupType = _popup.GetType();
        // menuScrollBox est un champ public, pas une propriete (GetProperty retourne null).
        var scrollBoxField = popupType.GetField("menuScrollBox", BindingFlags.Public | BindingFlags.Instance);
        var menuScrollBox = scrollBoxField?.GetValue(_popup);
        if (menuScrollBox == null)
            throw new InvalidOperationException("REPOPopupPage.menuScrollBox null (Awake pas encore execute ?).");
        var scrollerField = menuScrollBox.GetType().GetField(
            "scroller",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var scroller = scrollerField?.GetValue(menuScrollBox) as Transform;
        if (scroller == null)
            throw new InvalidOperationException("MenuScrollBox.scroller null.");

        // Sans REPOScrollViewElement, UpdateElements() ignore nos controles : les positions
        // locales restent hors layout et le 2e toggle peut sortir du masque. AddElementToScrollView
        // enregistre chaque ligne comme les pages MenuLib officielles (pile depuis le haut).
        var addToScroll = popupType.GetMethod(
            "AddElementToScrollView",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(RectTransform), typeof(Vector2), typeof(float), typeof(float) },
            null);
        if (addToScroll == null)
            throw new InvalidOperationException("REPOPopupPage.AddElementToScrollView(RectTransform, ...) introuvable.");

        var toggleSpawnComp = _miCreateToggle.Invoke(
            null,
            new object[]
            {
                "Spawn override",
                new Action<bool>(v =>
                {
                    ModConfig.EnableSpawnOverrides.Value = v;
                    SaveCfg();
                }),
                scroller,
                Vector2.zero,
                "ON",
                "OFF",
                ModConfig.EnableSpawnOverrides.Value
            }) as Component;
        _toggleSpawn = toggleSpawnComp;
        RegisterPopupScrollElement(_popup, addToScroll, toggleSpawnComp, topPad: 14f, bottomPad: 0f);

        var toggleSoundComp = _miCreateToggle.Invoke(
            null,
            new object[]
            {
                "Conditions pack NobleMod (SoundAPI)",
                new Action<bool>(v =>
                {
                    ModConfig.EnableNobleModSoundPackConditions.Value = v;
                    SaveCfg();
                }),
                scroller,
                Vector2.zero,
                "ON",
                "OFF",
                ModConfig.EnableNobleModSoundPackConditions.Value
            }) as Component;
        _toggleSoundPackConditions = toggleSoundComp;
        RegisterPopupScrollElement(_popup, addToScroll, toggleSoundComp, topPad: 0f, bottomPad: 0f);

        var toggleLogSoundPickComp = _miCreateToggle.Invoke(
            null,
            new object[]
            {
                "Log chaque son selectionne (random_slot)",
                new Action<bool>(v =>
                {
                    ModConfig.LogSoundPickEachMatch.Value = v;
                    SaveCfg();
                }),
                scroller,
                Vector2.zero,
                "ON",
                "OFF",
                ModConfig.LogSoundPickEachMatch.Value
            }) as Component;
        _toggleLogSoundPickEachMatch = toggleLogSoundPickComp;
        RegisterPopupScrollElement(_popup, addToScroll, toggleLogSoundPickComp, topPad: 0f, bottomPad: 0f);

        // Toggles dynamiques : un par fichier JSON replacer du pack NobleMod (decouverts par SoundAPI).
        // Si SoundAPI n'a pas encore appele AddLoadedPack (cas tres precoce), la liste est vide et
        // la section est tout simplement absente jusqu'a la prochaine reconstruction de la popup.
        _replacerToggles.Clear();
        var replacerEntries = ReplacerToggleRegistry.SnapshotEntries();
        var first = true;
        foreach (var entry in replacerEntries)
        {
            var capturedEntry = entry;
            var label = $"Replacer : {entry.DisplayName}";
            var toggleComp = _miCreateToggle.Invoke(
                null,
                new object[]
                {
                    label,
                    new Action<bool>(v =>
                    {
                        capturedEntry.Entry.Value = v;
                        SaveCfg();
                        if (!v)
                        {
                            var stopped = ReplacerToggleRegistry.StopActiveSourcesFor(capturedEntry.FilePath);
                            if (stopped > 0)
                                Plugin.Log?.LogInfo($"[NobleMod] Replacer '{capturedEntry.DisplayName}' desactive : {stopped} source(s) audio coupee(s).");
                        }
                    }),
                    scroller,
                    Vector2.zero,
                    "ON",
                    "OFF",
                    entry.Entry.Value
                }) as Component;
            if (toggleComp == null)
                continue;
            _replacerToggles.Add(new KeyValuePair<string, object>(entry.FilePath, toggleComp));
            RegisterPopupScrollElement(_popup, addToScroll, toggleComp, topPad: first ? 14f : 0f, bottomPad: 0f);
            first = false;
        }

        var closeBtn = _miCreateButton.Invoke(
            null,
            new object[]
            {
                "Fermer",
                (Action)(() =>
                {
                    var close = _popup.GetType().GetMethod("ClosePage", new[] { typeof(bool) });
                    close?.Invoke(_popup, new object[] { false });
                }),
                scroller,
                Vector2.zero
            }) as Component;
        RegisterPopupScrollElement(_popup, addToScroll, closeBtn, topPad: 0f, bottomPad: 20f);

        var repoScrollView = popupType.GetField("scrollView", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_popup);
        var updateElements = repoScrollView?.GetType().GetMethod(
            "UpdateElements",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            Type.EmptyTypes,
            null);
        updateElements?.Invoke(repoScrollView, null);

        var setScroll = repoScrollView?.GetType().GetMethod(
            "SetScrollPosition",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            new[] { typeof(float) },
            null);
        setScroll?.Invoke(repoScrollView, new object[] { 0f });
    }

    private static void RegisterPopupScrollElement(
        object popupPage,
        MethodInfo addElementToScrollView,
        Component ctrl,
        float topPad,
        float bottomPad)
    {
        if (ctrl == null || addElementToScrollView == null)
            return;
        var rt = ctrl.transform as RectTransform;
        if (rt == null)
            return;
        addElementToScrollView.Invoke(popupPage, new object[] { rt, Vector2.zero, topPad, bottomPad });
    }

    private static void SaveCfg()
    {
        try
        {
            Plugin.Instance?.Config.Save();
        }
        catch (Exception e)
        {
            Plugin.Log?.LogWarning($"[NobleMod] Sauvegarde config: {e.Message}");
        }
    }
}
