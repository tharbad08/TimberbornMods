using Bindito.Core;
using ModSettings.CommonUI;
using ModSettings.Core;
using ModSettings.CoreUI;
using Timberborn.Modding;
using Timberborn.SettingsSystem;
using TimberUi.CommonUi;
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
    private readonly NineSliceTextField _filter;
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
        this.SetPadding(8);

        AddGameLabel("Benchmark & Optimizer — live controls", bold: true);
        AddGameLabel("1 = vanilla frequency. 2 = every second dispatcher call. Changes apply immediately.");

        var enabled = AddToggle("Optimizer enabled", onValueChanged: Runtime.SetEnabled);
        enabled.SetValueWithoutNotify(Runtime.Enabled);

        var defaultSlider = AddSliderInt(
                label: "Default interval",
                values: new SliderValues<int>(1, 50, Runtime.DefaultInterval))
            .AddEndLabel(v => $"1/{v}")
            .RegisterChange(Runtime.SetDefaultInterval);
        defaultSlider.SetWidthPercent(100);

        var topRow = AddRow();
        topRow.AddMenuButton("Reset all to 1", Runtime.ResetIntervals);
        topRow.AddMenuButton("Refresh detected systems", Rebuild);

        AddGameLabel("Benchmark", bold: true).SetMarginTop(8);
        var benchRow = AddRow();
        var bench = benchRow.AddSliderInt(
                label: "Duration (seconds)",
                values: new SliderValues<int>(5, 120, _benchmarkSeconds))
            .AddEndLabel(v => $"{v}s")
            .RegisterChange(v => _benchmarkSeconds = v);
        bench.SetWidthPercent(70);
        benchRow.AddMenuButton("Start", () => Runtime.StartBenchmark(_benchmarkSeconds));

        _status = AddGameLabel("");
        _filter = AddTextField(changeCallback: _ => Rebuild());
        _filter.value = "";
        AddGameLabel("Filter systems");

        _list = AddScrollView();
        _list.SetMaxHeight(650);

        Rebuild();
    }

    private void Rebuild()
    {
        _list.Clear();

        _status.text = Runtime.IsBenchmarking
            ? $"Benchmark running — {Runtime.BenchmarkRemainingSeconds:F1}s remaining"
            : $"Detected systems: {Runtime.KnownTypes.Count}";

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
        var slider = _list.AddSliderInt(
                label: typeName,
                values: new SliderValues<int>(1, 50, current))
            .AddEndLabel(v => v == 1 ? "vanilla" : $"1/{v}")
            .RegisterChange(v => Runtime.SetInterval(typeName, v));

        slider.SetWidthPercent(100);
    }
}
