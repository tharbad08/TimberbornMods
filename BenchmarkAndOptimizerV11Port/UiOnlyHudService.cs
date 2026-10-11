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
    private const BindingFlags All = BindingFlags.Instance | BindingFlags.Static |
                                     BindingFlags.Public | BindingFlags.NonPublic;
    private static Toggle? _checkbox;
    private static Label? _status;
    private static object? _attachedLayout;
    private static bool _lastEnabled;
    private static bool _warned;

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

            var panel = new NineSliceVisualElement();
            CopyClasses(oldRoot, panel);
            // The base classes are always required even if HeightShower's
            // GetClasses is unavailable in a new game version.
            panel.AddToClassList("top-right-item");
            panel.AddToClassList("square-large--green");
            CopyInlineStyle(oldRoot, panel, "flexDirection");
            CopyInlineStyle(oldRoot, panel, "flexWrap");
            CopyInlineStyle(oldRoot, panel, "justifyContent");
            panel.SetWidth(205f).SetPadding(5f);

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

            // Existing HeightShower uses order 8 and the working T3MP
            // readout uses order 9. We add the UI-only panel at order 10.
            append.Invoke(layout, new object[] { panel, 10 });

            _attachedLayout = layout;
            _checkbox = checkbox;
            _status = label;
            _lastEnabled = UiOnlySimulationService.Enabled;
            Runtime.Log("UIONLY.HUD installed after HeightShower: " +
                "NineSliceVisualElement, exact HeightShower classes, " +
                "UILayout.AddTopRight order=10; checkbox live");
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
