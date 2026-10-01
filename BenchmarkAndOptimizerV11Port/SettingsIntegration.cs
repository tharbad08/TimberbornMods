using System.Reflection;
using Bindito.Core;
using ModSettings.CommonUI;
using ModSettings.Core;
using ModSettings.CoreUI;
using Timberborn.Modding;
using Timberborn.SettingsSystem;
using UnityEngine.UIElements;

namespace BenchmarkAndOptimizerV11;

[Context("MainMenu")]
public sealed class MainMenuConfig : Configurator
{
    public override void Configure()
    {
        this.BindSingleton<OptimizerSettingsOwner>()
            .MultiBindSingleton<IModSettingElementFactory, OptimizerSettingElementFactory>();
    }
}

[Context("Game")]
public sealed class GameConfig : Configurator
{
    public override void Configure()
    {
        this.BindSingleton<OptimizerSettingsOwner>()
            .MultiBindSingleton<IModSettingElementFactory, OptimizerSettingElementFactory>();
    }
}

public sealed class OptimizerSettingsOwner(
    ISettings settings,
    ModSettingsOwnerRegistry registry,
    ModRepository repository
) : ModSettingsOwner(settings, registry, repository)
{
    public override string ModId => "BenchmarkAndOptimizerV11";
    public override ModSettingsContext ChangeableOn => ModSettingsContext.All;

    public OptimizerUiSetting Optimizer { get; } = new();
}

public sealed class OptimizerUiSetting()
    : NonPersistentSetting(ModSettingDescriptor.Create("Live optimizer"))
{
    public override void Reset() => Runtime.ResetIntervals();
}

public sealed class OptimizerSettingElementFactory : IModSettingElementFactory
{
    public int Priority { get; }

    public bool TryCreateElement(ModSetting modSetting, out IModSettingElement? element)
    {
        if (modSetting is not OptimizerUiSetting)
        {
            element = null;
            return false;
        }

        element = new ModSettingElement(new OptimizerPanel(), modSetting);
        return true;
    }
}

public sealed class OptimizerPanel : VisualElement
{
    private readonly VisualElement _wellKnown = new();
    private readonly VisualElement _others = new();
    private readonly Label _status = new();
    private int _benchmarkSeconds = 30;

    private static readonly string[] WellKnown =
    {
        "BehaviorManager",
        "NavMeshObserver",
        "ConstructionSite",
        "ResourceCountingService"
    };

    public OptimizerPanel()
    {
        // ModSettings owns the panel. Functional widgets use only Unity UIElements.
        // TimberUi is still used for layout/styling, but through a compatibility
        // reflection shim so concrete TimberUi type renames cannot TypeLoad-crash us.
        TimberUiCompat.Padding(this, 8);

        AddLabel(this, "Benchmark & Optimizer — live controls", bottom: 4);
        AddLabel(
            this,
            "1 = vanilla frequency. Higher values run that system once every N dispatcher calls. Changes apply immediately.",
            bottom: 8);

        var enabled = new Toggle { text = "Optimizer enabled" };
        enabled.SetValueWithoutNotify(Runtime.Enabled);
        enabled.RegisterCallback<ChangeEvent<bool>>(ev => Runtime.SetEnabled(ev.newValue));
        Add(enabled);

        AddIntSlider(
            this,
            "Default interval",
            1,
            50,
            Runtime.DefaultInterval,
            Runtime.SetDefaultInterval,
            v => v == 1 ? "vanilla" : $"1/{v}");

        var actions = NewRow(this);
        AddButton(actions, "Reset all to 1", () =>
        {
            Runtime.ResetIntervals();
            Rebuild();
        });
        AddButton(actions, "Refresh detected systems", Rebuild);
        TimberUiCompat.MarginBottom(actions, 8);

        AddLabel(this, "Benchmark", bottom: 4);
        var benchmarkRow = NewRow(this);
        AddIntSlider(
            benchmarkRow,
            "Duration",
            5,
            120,
            _benchmarkSeconds,
            v => _benchmarkSeconds = v,
            v => $"{v}s");
        AddButton(benchmarkRow, "Start", () =>
        {
            Runtime.StartBenchmark(_benchmarkSeconds);
            UpdateStatus();
        });
        TimberUiCompat.MarginBottom(benchmarkRow, 8);

        Add(_status);
        TimberUiCompat.MarginBottom(_status, 8);

        var scroll = new ScrollView();
        Add(scroll);
        TimberUiCompat.MaxHeight(scroll, 650);

        scroll.Add(_wellKnown);
        scroll.Add(_others);

        Rebuild();
    }

    private static Label AddLabel(VisualElement parent, string text, float bottom = 0)
    {
        var label = new Label(text);
        parent.Add(label);
        if (bottom > 0)
        {
            TimberUiCompat.MarginBottom(label, bottom);
        }
        return label;
    }

    private static VisualElement NewRow(VisualElement parent)
    {
        var row = new VisualElement();
        parent.Add(row);
        TimberUiCompat.AsRow(row);
        return row;
    }

    private static void AddButton(VisualElement parent, string text, Action onClick)
    {
        var button = new Button { text = text };
        button.clicked += onClick;
        parent.Add(button);
    }

    private static void AddIntSlider(
        VisualElement parent,
        string label,
        int min,
        int max,
        int value,
        Action<int> onChanged,
        Func<int, string> valueText)
    {
        var row = NewRow(parent);

        row.Add(new Label(label));

        var slider = new SliderInt
        {
            lowValue = min,
            highValue = max
        };
        slider.SetValueWithoutNotify(value);
        TimberUiCompat.WidthPercent(slider, 65);
        row.Add(slider);

        var current = new Label(valueText(value));
        row.Add(current);

        slider.RegisterCallback<ChangeEvent<int>>(ev =>
        {
            onChanged(ev.newValue);
            current.text = valueText(ev.newValue);
        });

        TimberUiCompat.MarginBottom(row, 6);
    }

    private void UpdateStatus()
    {
        _status.text = Runtime.IsBenchmarking
            ? $"Benchmark running — {Runtime.BenchmarkRemainingSeconds:F1}s remaining"
            : $"Detected systems: {Runtime.KnownTypes.Count}";
    }

    private void Rebuild()
    {
        UpdateStatus();
        _wellKnown.Clear();
        _others.Clear();

        AddLabel(_wellKnown, "Well-known systems", bottom: 5);
        AddLabel(_others, "Other detected systems", bottom: 5);

        var names = WellKnown
            .Concat(Runtime.KnownTypes)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => Array.IndexOf(WellKnown, n) < 0 ? 1 : 0)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            AddSystemRow(
                Array.IndexOf(WellKnown, name) >= 0 ? _wellKnown : _others,
                name);
        }
    }

    private static void AddSystemRow(VisualElement parent, string typeName)
    {
        AddIntSlider(
            parent,
            typeName,
            1,
            50,
            Runtime.GetInterval(typeName),
            v => Runtime.SetInterval(typeName, v),
            v => v == 1 ? "vanilla" : $"1/{v}");
    }
}

internal static class TimberUiCompat
{
    private static readonly Type? ExtensionsType =
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "TimberUi")
            ?.GetType("UnityEngine.UIElements.UiBuilderExtensions");

    public static void Padding(VisualElement element, float value) =>
        Invoke("SetPadding", element, value);

    public static void MarginBottom(VisualElement element, float value) =>
        Invoke("SetMarginBottom", element, value);

    public static void AsRow(VisualElement element) =>
        Invoke("SetAsRow", element);

    public static void WidthPercent(VisualElement element, float value) =>
        Invoke("SetWidthPercent", element, value);

    public static void MaxHeight(VisualElement element, float value) =>
        Invoke("SetMaxHeight", element, value);

    private static void Invoke(string name, VisualElement element, params object[] args)
    {
        if (ExtensionsType is null)
        {
            return;
        }

        try
        {
            var wantedCount = args.Length + 1;
            var candidates = ExtensionsType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == name && m.GetParameters().Length == wantedCount);

            foreach (var candidate in candidates)
            {
                var method = candidate;
                if (method.IsGenericMethodDefinition)
                {
                    var genericArgs = method.GetGenericArguments();
                    if (genericArgs.Length != 1)
                    {
                        continue;
                    }
                    method = method.MakeGenericMethod(element.GetType());
                }

                var parameters = method.GetParameters();
                if (!parameters[0].ParameterType.IsAssignableFrom(element.GetType()))
                {
                    continue;
                }

                var invokeArgs = new object[wantedCount];
                invokeArgs[0] = element;
                Array.Copy(args, 0, invokeArgs, 1, args.Length);
                method.Invoke(null, invokeArgs);
                return;
            }
        }
        catch
        {
            // Styling must never make the settings page unusable.
        }
    }
}
