using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Bindito.Core;
using HarmonyLib;
using Timberborn.CoreUI;
using Timberborn.SingletonSystem;
using TimberUi.CommonUi;
using UiBuilder;
using UnityEngine.UIElements;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Attach UI-only directly after HeightShowerPanel.Load, exactly as our
/// T3MPPersonalAddon.HeightShowerAdapter does. No DI GetInstance<UILayout>
/// lookup, no guessing the top-right root, and no duplicated detached panel.
/// HeightShower is already installed in the user's mod set. Its UILayout
/// instance is proven to be the one used by the existing working UI.
/// </summary>
public sealed class UiOnlyHudService : IUpdatableSingleton, IUnloadableSingleton
{
    private static IContainer? _container;
    public UiOnlyHudService(IContainer container) => _container = container;
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.Public | BindingFlags.NonPublic;
    private static Toggle? _checkbox;
    private static Label? _status;
    private static object? _attachedLayout;
    private static bool _lastEnabled;
    private static bool _warned;
    private static VisualElement? _panel;
    private static VisualElement? _overlayRoot;
    // HeightShowerPanel._root is the known working HUD landmark.
    // Use it instead of guessing the localized resource-counter names.
    private static VisualElement? _heightAnchor;
    private static VisualElement? _waterAnchor;
    private static int _waterLookupAttempts;
    private static bool _waterMissingLogged;
    private static long _nextAnchorCheck;
    private static bool _secondRowAttached;
    private const float PanelWidth = 226f;
    private const float PanelGap = 4f;

    public static void Install(Harmony harmony)
    {
        try
        {
            // T3MP Personal Companion scans the loaded assemblies by type
            // name+namespace. Do the same, but skip the emitted DMD/MonoMod
            // assemblies entirely to avoid startup GetTypes failures.
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic).ToArray();
            var candidateAssemblies = assemblies.Where(a =>
                (a.GetName().Name ?? "").Contains("HeightShower",
                    StringComparison.OrdinalIgnoreCase)).ToArray();

            var types = FindHeightPanels(candidateAssemblies);
            if (types.Count == 0)
                types = FindHeightPanels(assemblies);

            int hooks = 0;
            foreach (var type in types)
            {
                var method = AccessTools.Method(type, "Load", Type.EmptyTypes);
                if (method is null) continue;
                harmony.Patch(method, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(UiOnlyHudService),
                        nameof(AfterHeightLoad))));
                hooks++;
            }
            Runtime.Log($"UIONLY.HUD HeightShower hook installed={hooks}; " +
                "reuse HeightShowerPanel._gameLayout and _root; " +
                "same method as T3MP Personal Companion, order=10");
        }
        catch (Exception ex)
        {
            Runtime.Log("UIONLY.HUD HeightShower hook failed: " +
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static List<Type> FindHeightPanels(IEnumerable<Assembly> assemblies)
    {
        var found = new List<Type>();
        foreach (var a in assemblies)
        {
            Type[] candidates;
            try { candidates = a.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                candidates = ex.Types.Where(x => x is not null).Cast<Type>().ToArray();
            }
            catch { continue; }
            foreach (var t in candidates)
            {
                if (t.Name == "HeightShowerPanel" &&
                    (t.Namespace ?? "").Contains("HeightShower",
                        StringComparison.OrdinalIgnoreCase))
                    found.Add(t);
            }
        }
        return found;
    }

    public static void AfterHeightLoad(object __instance)
    {
        try
        {
            var type = __instance.GetType();
            var oldRoot = AccessTools.Field(type, "_root")?.GetValue(__instance)
                ?? throw new MissingFieldException(type.FullName, "_root");
            var layout = AccessTools.Field(type, "_gameLayout")?.GetValue(__instance)
                ?? throw new MissingFieldException(type.FullName, "_gameLayout");
            if (ReferenceEquals(_attachedLayout, layout) && _checkbox is not null)
                return;

            // Game-native nine-slice FRAME outside a green content panel.
            // Initialization is necessary for the textures and USS rules.
            var frame = new NineSliceVisualElement();
            frame.AddToClassList("top-right-item");
            frame.AddToClassList("entity-sub-panel");
            frame.AddToClassList("bg-sub-box--frame");
            frame.SetWidth(PanelWidth).SetPadding(2f);

            var panel = new NineSliceVisualElement();
            panel.AddToClassList("square-large--green");
            CopyClasses(oldRoot, panel);
            CopyInlineStyle(oldRoot, panel, "flexDirection");
            CopyInlineStyle(oldRoot, panel, "flexWrap");
            CopyInlineStyle(oldRoot, panel, "justifyContent");
            panel.SetWidth(PanelWidth - 4f).SetPadding(5f);
            frame.Add(panel);

            var row = panel.AddRow();
            var checkbox = row.AddToggle("UI-only simulation",
                onValueChanged: OnCheckboxChanged,
                style: ToggleStyle.GamePanel);
            var label = row.AddGameLabel(
                UiOnlySimulationService.Enabled ? "ON" : "OFF");

            var heightLabel = AccessTools.Field(type, "HeightLabel")
                ?.GetValue(__instance);
            if (heightLabel is not null)
                CopyClasses(heightLabel, label);

            checkbox.SetValueWithoutNotify(UiOnlySimulationService.Enabled);

            var visualElementType = typeof(VisualElement);
            var append = layout.GetType().GetMethods(All)
                .FirstOrDefault(method =>
                    method.Name == "AddTopRight" &&
                    method.GetParameters().Length == 2 &&
                    method.GetParameters()[0].ParameterType
                        .IsAssignableFrom(visualElementType) &&
                    method.GetParameters()[1].ParameterType == typeof(int))
                ?? throw new MissingMethodException("UILayout.AddTopRight(VisualElement,int)");

            // Initialize new nine-slice subtree before reparenting.
            InitializeFrame(frame);
            append.Invoke(layout, new object[] { frame, 10 });

            _panel = frame;
            _heightAnchor = oldRoot as VisualElement;
            _waterAnchor = null;
            _waterLookupAttempts = 0;
            _waterMissingLogged = false;
            _secondRowAttached = false;
            _nextAnchorCheck = 0;
            PositionSecondRow();

            _attachedLayout = layout;
            _checkbox = checkbox;
            _status = label;
            _lastEnabled = UiOnlySimulationService.Enabled;
            Runtime.Log("UIONLY.HUD installed after HeightShower: " +
                "NineSliceVisualElement; second-row overlay pending geometry; " +
                "UILayout.AddTopRight order=10 is temporary fallback");
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                Runtime.Log("UIONLY.HUD HeightShower attachment failed: " +
                    ex.GetBaseException().GetType().Name + ": " +
                    ex.GetBaseException().Message);
            }
        }
    }

    private static void OnCheckboxChanged(bool desired)
    {
        var ok = UiOnlySimulationService.TrySetEnabled(desired);
        var actual = UiOnlySimulationService.Enabled;
        _lastEnabled = actual;
        _checkbox?.SetValueWithoutNotify(actual);
        if (_status is not null)
            _status.text = ok ? (actual ? "ON" : "OFF") : "ERR";
        Runtime.Log("UIONLY.HUD toggle desired=" + desired +
            " applied=" + ok + " actual=" + actual);
    }

    public void UpdateSingleton()
    {
        // The ModSettings control can change the mode. Keep the HUD in
        // sync only on transitions; no per-frame UI writes.
        // Reposition only twice per second. No per-frame tree scans or polling
        // all HUD children; the saved HeightShower root is our only anchor.
        if (_panel is not null)
            PositionSecondRow();

        var value = UiOnlySimulationService.Enabled;
        if (_checkbox is null || _lastEnabled == value) return;
        _checkbox.SetValueWithoutNotify(value);
        if (_status is not null) _status.text = value ? "ON" : "OFF";
        _lastEnabled = value;
    }

    public void Unload()
    {
        // Game UI owns the panel and destroys it on scene unload. Only clear
        // references; never leave a retained old-scene VisualElement.
        _checkbox = null;
        _status = null;
        _attachedLayout = null;
        _lastEnabled = false;
        _panel = null;
        _heightAnchor = null;
        _waterAnchor = null;
        _container = null;
        _waterLookupAttempts = 0;
        _waterMissingLogged = false;
        _overlayRoot = null;
        _secondRowAttached = false;
        _nextAnchorCheck = 0;
    }


    // More TopBars adds resource groups; Water itself is a vanilla counter.
    // Locate it through the game's TopBarPanel._counters and TopBarCounterRow
    // _goodId/_root, never through a translated label or fragile UI node name.
    // The HeightShower reference remains a safe fallback if an API changes.
    private static void PositionSecondRow()
    {
        if (_panel is null || _heightAnchor is null) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextAnchorCheck) return;
        _nextAnchorCheck = now + System.Diagnostics.Stopwatch.Frequency / 2;

        try
        {
            if (_waterAnchor is null && _waterLookupAttempts < 24)
                TryResolveWaterCounter();

            var anchor = _waterAnchor ?? _heightAnchor;
            VisualElement document = anchor;
            while (document.parent is not null) document = document.parent;
            if (!ReadBounds(anchor, out var anchorX, out var anchorY,
                    out var anchorWidth, out var anchorHeight) ||
                !ReadBounds(document, out var docX, out var docY, out var docWidth, out _))
                return;

            // Water counter: align the new panel's LEFT with Water's left,
            // and its TOP flush against Water's bottom. It may run below
            // Badwater, as requested, with no flex-row spacing reserved.
            // Fallback: use the existing second-row HeightShower placement.
            bool usingWater = _waterAnchor is not null;
            float x, y;
            if (usingWater)
            {
                x = Math.Max(2f, anchorX - docX);
                y = anchorY + anchorHeight - docY;
            }
            else
            {
                y = anchorY - docY;
                float firstRowHeight = Math.Max(anchorHeight, 24f);
                if (y < firstRowHeight - 2f)
                    y += firstRowHeight + 2f;
                x = Math.Max(2f, anchorX - docX - PanelWidth - PanelGap);
            }

            if (x + PanelWidth > docWidth)
                x = Math.Max(2f, docWidth - PanelWidth - 2f);

            if (!_secondRowAttached || !ReferenceEquals(_overlayRoot, document))
            {
                // Preserve the native class; absolute reparenting already
                // removes the element from the original flex layout.
                _panel.style.position = Position.Absolute;
                document.Add(_panel); // Removes panel from limited top-right flex rows.
                _overlayRoot = document;
                _secondRowAttached = true;
                Runtime.Log("UIONLY.HUD overlay attached; zero layout spacing; " +
                    "position source=" + (usingWater ? "TopBarPanel.Water" : "HeightShower fallback"));
            }

            _panel.style.left = x;
            _panel.style.top = y;
            if (usingWater && !_waterMissingLogged)
            {
                _waterMissingLogged = true; // One-time position confirmation.
                Runtime.Log($"UIONLY.HUD anchored below vanilla Water: x={x:F0} y={y:F0}; " +
                    $"waterBounds={anchorX:F0},{anchorY:F0},{anchorWidth:F0},{anchorHeight:F0}; " +
                    "no gap; panel may extend under Badwater");
            }
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                Runtime.Log("UIONLY.HUD overlay positioning failed: " +
                    ex.GetBaseException().GetType().Name + ": " +
                    ex.GetBaseException().Message);
            }
        }
    }

    private static void TryResolveWaterCounter()
    {
        ++_waterLookupAttempts;
        if (_container is null) return;
        try
        {
            var type = AccessTools.TypeByName("Timberborn.TopBarSystem.TopBarPanel");
            if (type is null) return;
            var method = _container.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetInstance" &&
                    m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var instance = method?.MakeGenericMethod(type).Invoke(_container, null);
            if (instance is null) return;
            var counters = AccessTools.Field(type, "_counters")?.GetValue(instance) as IEnumerable;
            if (counters is null) return;

            var names = new List<string>(16);
            foreach (var counter in counters)
            {
                if (counter is null) continue;
                if (InspectCounter(counter, names)) return;
                var nested = AccessTools.Field(counter.GetType(), "_counterRows")
                    ?.GetValue(counter) as IEnumerable;
                if (nested is null) continue;
                foreach (var row in nested)
                    if (row is not null && InspectCounter(row, names)) return;
            }
            if (_waterLookupAttempts >= 24)
                Runtime.Log("UIONLY.HUD TopBarPanel water counter unresolved; " +
                    "counter resource IDs=" + string.Join(",", names.Take(20)) +
                    "; HeightShower fallback kept");
        }
        catch (Exception ex)
        {
            if (_waterLookupAttempts == 24)
                Runtime.Log("UIONLY.HUD TopBarPanel lookup unavailable: " +
                    ex.GetBaseException().GetType().Name + ": " +
                    ex.GetBaseException().Message);
        }
    }

    private static bool InspectCounter(object counter, List<string> foundIds)
    {
        var type = counter.GetType();
        var id = AccessTools.Field(type, "_goodId")?.GetValue(counter)?.ToString();
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (foundIds.Count < 20 && !foundIds.Contains(id)) foundIds.Add(id);

        // Water is the game's resource ID; explicitly exclude Badwater.
        if (!id.Equals("Water", StringComparison.OrdinalIgnoreCase) &&
            !id.EndsWith(".Water", StringComparison.OrdinalIgnoreCase)) return false;
        var visual = AccessTools.Field(type, "_root")?.GetValue(counter) as VisualElement;
        if (visual is null || !ReadBounds(visual, out _, out _, out var width, out var height) ||
            width < 10 || height < 10) return false;

        _waterAnchor = visual;
        Runtime.Log("UIONLY.HUD vanilla resource counter located " +
            type.FullName + " goodId=" + id + " (More TopBars groups ignored)");
        return true;
    }

    private static bool ReadBounds(VisualElement e, out float x, out float y,
        out float width, out float height)
    {
        x = y = width = height = 0f;
        var bounds = e.GetType().GetProperty("worldBound", All)?.GetValue(e);
        if (bounds is null) return false;
        try
        {
            var t = bounds.GetType();
            float Get(string name) => Convert.ToSingle(t.GetProperty(name, All)?.GetValue(bounds)
                ?? t.GetField(name, All)?.GetValue(bounds) ?? 0f);
            x = Get("xMin");
            y = Get("yMin");
            width = Get("width");
            height = Get("height");
            return width > 0f && height > 0f;
        }
        catch { return false; }
    }

    private static void InitializeFrame(VisualElement frame)
    {
        try
        {
            var type = AccessTools.TypeByName("Timberborn.CoreUI.VisualElementInitializer")
                ?? AccessTools.TypeByName("VisualElementInitializer");
            if (type is null || _container is null)
            {
                Runtime.Log("UIONLY.HUD visual initializer unavailable; using USS classes");
                return;
            }
            var get = _container.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetInstance" &&
                    m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            var instance = get?.MakeGenericMethod(type).Invoke(_container, null);
            var method = type.GetMethods(All).FirstOrDefault(m =>
                m.Name == "InitializeVisualElement" &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(VisualElement)));
            if (instance is null || method is null)
            {
                Runtime.Log("UIONLY.HUD initializer method unavailable; using USS classes");
                return;
            }
            method.Invoke(instance, new object[] { frame });
            Runtime.Log("UIONLY.HUD frame+green fill initialized with game visual initializer");
        }
        catch (Exception ex)
        {
            Runtime.Log("UIONLY.HUD frame initializer warning: " +
                ex.GetBaseException().GetType().Name + ": " + ex.GetBaseException().Message);
        }
    }

    private static void CopyClasses(object source, VisualElement target)
    {
        var method = source.GetType().GetMethod(
            "GetClasses", All, null, Type.EmptyTypes, null);
        if (method?.Invoke(source, null) is not IEnumerable classes) return;
        foreach (var value in classes)
            if (value is string className)
                target.AddToClassList(className);
    }

    private static void CopyInlineStyle(object source, VisualElement target,
        string name)
    {
        try
        {
            var from = source.GetType().GetProperty("style", All)?.GetValue(source);
            var to = target.GetType().GetProperty("style", All)?.GetValue(target);
            if (from is null || to is null) return;
            var style = from.GetType().GetInterfaces()
                .FirstOrDefault(i => i.Name == "IStyle" &&
                    i.GetProperty(name) is not null);
            var prop = style?.GetProperty(name);
            if (prop is not null)
                prop.SetValue(to, prop.GetValue(from));
        }
        catch { /* Cosmetic styles are optional. */ }
    }
}
