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
    protected override string ModId => "BenchmarkAndOptimizerV11";
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
    public int Priority => 0;

    public bool TryCreateElement(ModSetting modSetting, out IModSettingElement? element)
    {
        if (modSetting is not OptimizerUiSetting)
        {
            element = null;
            return false;
        }

        var panel = new OptimizerPanel();
        element = new ModSettingElement(panel, modSetting);
        return true;
    }
}

public sealed class OptimizerPanel : VisualElement
{
    private readonly VisualElement _list;
    private readonly Label _status;
    private readonly TextField _filter;
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
        // TimberUi is intentionally used only through helpers whose signatures contain
        // vanilla UIElements types. This avoids depending on TimberUi's private/concrete
        // NineSlice/GameSlider classes, which have changed between releases.
        this.SetPadding(8);

        AddPlainLabel("Benchmark & Optimizer — live controls");
        AddPlainLabel("1 = vanilla frequency. Higher values run the system once every N dispatcher calls. Changes apply immediately.");

        var enabled = new Toggle("Optimizer enabled");
        enabled.SetValueWithoutNotify(Runtime.Enabled);
        enabled.RegisterValueChangedCallback(ev => Runtime.SetEnabled(ev.newValue));
        Add(enabled);

        AddIntSlider(
            parent: this,
            label: "Default interval",
            min: 1,
            max: 50,
            value: Runtime.DefaultInterval,
            onChanged: Runtime.SetDefaultInterval,
            valueText: v => v == 1 ? "vanilla" : $"1/{v}");

        var topRow = this.AddRow();
        AddButton(topRow, "Reset all to 1", () =>
        {
            Runtime.ResetIntervals();
            Rebuild();
        });
        AddButton(topRow, "Refresh detected systems", Rebuild);

        AddPlainLabel("Benchmark");
        var benchRow = this.AddRow();
        AddIntSlider(
            parent: benchRow,
            label: "Duration (seconds)",
            min: 5,
            max: 120,
            value: _benchmarkSeconds,
            onChanged: v => _benchmarkSeconds = v,
            valueText: v => $"{v}s");
        AddButton(benchRow, "Start", () =>
        {
            Runtime.StartBenchmark(_benchmarkSeconds);
            UpdateStatus();
        });

        _status = AddPlainLabel("");

        AddPlainLabel("Filter systems");
        _filter = new TextField();
        _filter.value = "";
        _filter.RegisterValueChangedCallback(_ => Rebuild());
        Add(_filter);

        _list = this.AddScrollView();
        _list.SetMaxHeight(650);

        Rebuild();
    }

    private Label AddPlainLabel(string text)
    {
        var label = new Label(text);
        Add(label);
        return label;
    }

    private static void AddButton(VisualElement parent, string text, Action action)
    {
        var button = new Button(action) { text = text };
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
        var row = parent.AddRow();

        var title = new Label(label);
        row.Add(title);

        var slider = new SliderInt(min, max);
        slider.SetValueWithoutNotify(value);
        slider.RegisterValueChangedCallback(ev => onChanged(ev.newValue));
        slider.SetWidthPercent(65);
        row.Add(slider);

        var current = new Label(valueText(value));
        row.Add(current);

        slider.RegisterValueChangedCallback(ev => current.text = valueText(ev.newValue));
    }

    private void UpdateStatus()
    {
        _status.text = Runtime.IsBenchmarking
            ? $"Benchmark running — {Runtime.BenchmarkRemainingSeconds:F1}s remaining"
            : $"Detected systems: {Runtime.KnownTypes.Count}";
    }

    private void Rebuild()
    {
        _list.Clear();
        UpdateStatus();

        var filter = (_filter.value ?? "").Trim();
        var names = WellKnown
            .Concat(Runtime.KnownTypes)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => Array.IndexOf(WellKnown, n) < 0 ? 1 : 0)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (filter.Length > 0 &&
                name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            AddSystemRow(name);
        }
    }

    private void AddSystemRow(string typeName)
    {
        var current = Runtime.GetInterval(typeName);
        AddIntSlider(
            parent: _list,
            label: typeName,
            min: 1,
            max: 50,
            value: current,
            onChanged: v => Runtime.SetInterval(typeName, v),
            valueText: v => v == 1 ? "vanilla" : $"1/{v}");
    }
}
