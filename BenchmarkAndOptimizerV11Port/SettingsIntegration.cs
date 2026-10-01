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
    private readonly VisualElement _wellKnown;
    private readonly VisualElement _others;
    private readonly Label _status;
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
        // Follow datvm's own TimberUi/ModSettings pattern:
        // use AddToggle/AddSliderInt/RegisterChange instead of raw UIElements callbacks.
        this.SetPadding(8);

        this.AddGameLabel("Benchmark & Optimizer — live controls", bold: true);
        this.AddGameLabel("1 = vanilla frequency. Higher values run that system once every N dispatcher calls. Changes apply immediately.")
            .SetMarginBottom(8);

        var enabled = this.AddToggle("Optimizer enabled", onValueChanged: Runtime.SetEnabled);
        enabled.value = Runtime.Enabled;

        this.AddSliderInt(
                label: "Default interval",
                values: new SliderValues<int>(1, 50, Runtime.DefaultInterval))
            .AddEndLabel(v => v == 1 ? "vanilla" : $"1/{v}")
            .RegisterChange(Runtime.SetDefaultInterval)
            .SetWidthPercent(100)
            .SetMarginBottom(8);

        var actions = this.AddRow();
        actions.AddMenuButton("Reset all to 1", onClick: () =>
        {
            Runtime.ResetIntervals();
            Rebuild();
        });
        actions.AddMenuButton("Refresh detected systems", onClick: Rebuild);
        actions.SetMarginBottom(8);

        this.AddGameLabel("Benchmark", bold: true);
        var benchmarkRow = this.AddRow();
        benchmarkRow.AddSliderInt(
                label: "Duration",
                values: new SliderValues<int>(5, 120, _benchmarkSeconds))
            .AddEndLabel(v => $"{v}s")
            .RegisterChange(v => _benchmarkSeconds = v)
            .SetWidthPercent(75);
        benchmarkRow.AddMenuButton("Start", onClick: () =>
        {
            Runtime.StartBenchmark(_benchmarkSeconds);
            UpdateStatus();
        });

        _status = this.AddGameLabel("");
        _status.SetMarginBottom(8);

        var scroll = this.AddScrollView();
        scroll.SetMaxHeight(650);

        _wellKnown = scroll.AddChild();
        _others = scroll.AddChild();

        Rebuild();
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

        _wellKnown.AddGameLabel("Well-known systems", bold: true).SetMarginBottom(5);
        _others.AddGameLabel("Other detected systems", bold: true).SetMargin(10, 0, 5, 0);

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
        var current = Runtime.GetInterval(typeName);

        parent.AddSliderInt(
                label: typeName,
                values: new SliderValues<int>(1, 50, current))
            .AddEndLabel(v => v == 1 ? "vanilla" : $"1/{v}")
            .RegisterChange(v => Runtime.SetInterval(typeName, v))
            .SetWidthPercent(100)
            .SetMarginBottom(6);
    }
}
