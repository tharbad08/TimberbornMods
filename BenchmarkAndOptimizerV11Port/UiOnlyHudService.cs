using System;
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
/// A real in-game HUD panel rather than a ModSettings-only control.
/// Uses the documented Timberborn.UILayoutSystem.UILayout.AddTopRight
/// integration, shared with TImprove's coordinates panel. Reflection
/// avoids adding any additional runtime DLLs to this standalone mod.
/// </summary>
public sealed class UiOnlyHudService(IContainer container)
    : IPostLoadableSingleton, IUpdatableSingleton
{
    private Toggle? _checkbox;
    private Label? _status;
    private bool _lastMode;

    public void PostLoad()
    {
        try
        {
            var layout = ResolveLayout(container);
            if (layout is null)
            {
                Runtime.Log("UIONLY.HUD unavailable: Timberborn UILayout not found in Game container");
                return;
            }

            // TimberUi's AddChild<T>(classes: ...) applies the game-provided
            // CSS to a NineSliceVisualElement, exactly like other 1.1 HUD mods.
            var staging = new VisualElement();
            var panel = staging.AddChild<NineSliceVisualElement>(
                name: "UiOnlySimulationHud",
                classes: new[] { "top-right-item", "square-large--brown" });
            panel.SetPadding(6f).SetWidth(204f);

            panel.AddGameLabel("UI-only simulation", bold: true)
                .SetMarginBottom(2f);
            _checkbox = panel.AddToggle("Hide 3D world",
                onValueChanged: OnCheckboxChanged,
                style: ToggleStyle.GamePanel);
            _lastMode = UiOnlySimulationService.Enabled;
            _checkbox.SetValueWithoutNotify(_lastMode);
            _status = panel.AddGameLabel(_lastMode
                ? "UI ONLY active" : "3D world visible");

            var method = layout.GetType().GetMethod("AddTopRight",
                BindingFlags.Public | BindingFlags.Instance,
                binder: null,
                types: new[] { typeof(VisualElement), typeof(int) },
                modifiers: null);
            if (method is null)
            {
                Runtime.Log("UIONLY.HUD unavailable: UILayout.AddTopRight(VisualElement,int) missing");
                return;
            }

            // Order 8 puts the compact panel with other top-right game HUD
            // panels without relying on pixel coordinates of the screenshot.
            method.Invoke(layout, new object[] { panel, 8 });
            Runtime.Log("UIONLY.HUD installed: top-right panel order=8, " +
                "checkbox=Hide 3D world, initialMode=" + (_lastMode ? "ON" : "OFF"));
        }
        catch (Exception ex)
        {
            Runtime.Log("UIONLY.HUD failed: " + ex.GetType().Name +
                ": " + (ex.InnerException?.Message ?? ex.Message) +
                "; existing ModSettings checkbox still available");
        }
    }

    public void UpdateSingleton()
    {
        // Cheap once-per-frame bool read. Only interact with UIElements
        // if the ModSettings control changed the underlying rendering mode.
        var actual = UiOnlySimulationService.Enabled;
        if (_checkbox is null || actual == _lastMode) return;
        _lastMode = actual;
        _checkbox.SetValueWithoutNotify(actual);
        if (_status is not null)
            _status.text = actual ? "UI ONLY active" : "3D world visible";
    }

    private void OnCheckboxChanged(bool desired)
    {
        bool applied = UiOnlySimulationService.TrySetEnabled(desired);
        bool actual = UiOnlySimulationService.Enabled;
        _lastMode = actual;
        _checkbox?.SetValueWithoutNotify(actual);
        if (_status is not null)
            _status.text = !applied
                ? "Unavailable (see log)"
                : actual ? "UI ONLY active" : "3D world visible";
        Runtime.Log("UIONLY.HUD toggle requested=" + desired +
            " applied=" + applied + " actual=" + actual);
    }

    private static object? ResolveLayout(IContainer container)
    {
        var layoutType = AccessTools.TypeByName("Timberborn.UILayoutSystem.UILayout");
        if (layoutType is null) return null;
        // Bindito's strongly typed GetInstance<T> is available through its
        // IContainer interface, even without compiling against UILayout.dll.
        var get = typeof(IContainer).GetMethods(BindingFlags.Public |
            BindingFlags.Instance)
            .SingleOrDefault(m => m.Name == "GetInstance" &&
                m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        return get?.MakeGenericMethod(layoutType).Invoke(container, null);
    }
}
