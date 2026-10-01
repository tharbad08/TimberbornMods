using System.Reflection;
using Bindito.Core;
using ModSettings.CommonUI;
using ModSettings.Core;
using ModSettings.CoreUI;
using Timberborn.Modding;
using Timberborn.Options;
using Timberborn.OptionsGame;
using Timberborn.SettingsSystem;
using Timberborn.SingletonSystem;
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
            .MultiBindSingleton<IModSettingElementFactory, OptimizerSettingElementFactory>()
            .BindSingleton<OptimizerMenuService>();
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
    private string _filter = "";

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
        benchmarkRow.AddGameLabel($"Fixed duration: {Runtime.BenchmarkDurationSeconds} seconds");
        benchmarkRow.AddMenuButton("Start 120s benchmark", onClick: () =>
        {
            Runtime.StartBenchmark();
            UpdateStatus();
        });

        _status = this.AddGameLabel("");
        _status.SetMarginBottom(8);

        // Same live-filter pattern used by TImprove4Mods.
        this.AddTextField("Filter", keyword =>
        {
            _filter = keyword?.Trim() ?? "";
            Rebuild();
        })
        .SetWidthPercent(100)
        .SetMarginBottom(8);

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
            .Where(name => string.IsNullOrEmpty(_filter)
                || name.Contains(_filter, StringComparison.OrdinalIgnoreCase))
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


public sealed class OptimizerMenuService(
    IOptionsBox optionsBox,
    ModSettingsBox modSettingsBox,
    ModRepository repository
) : ILoadableSingleton
{
    private readonly GameOptionsBox _optionsBox = (GameOptionsBox)optionsBox;
    private Mod? _mod;

    public void Load()
    {
        _mod = ResolveMod(repository, "BenchmarkAndOptimizerV11");
        if (_mod is null)
        {
            Runtime.Log("warning: pause-menu button could not resolve BenchmarkAndOptimizerV11 in ModRepository");
            return;
        }

        var root = _optionsBox._root;
        var resumeButton = root.Q<VisualElement>("ResumeButton");

        if (resumeButton is null)
        {
            Runtime.Log("warning: pause-menu ResumeButton not found; optimizer menu entry not added");
            return;
        }

        var button = root.AddMenuButton(
            "Benchmark & Optimizer",
            onClick: () => modSettingsBox.Open(_mod),
            name: "BenchmarkAndOptimizerButton",
            stretched: true);

        button.InsertSelfAfter(resumeButton);
        Runtime.Log("pause-menu entry installed after ResumeButton");
    }

    private static Mod? ResolveMod(ModRepository repository, string id)
    {
        try
        {
            var enabledMods = repository.GetType()
                .GetProperty("EnabledMods", BindingFlags.Public | BindingFlags.Instance)
                ?.GetValue(repository) as System.Collections.IEnumerable;

            if (enabledMods is null)
            {
                return null;
            }

            foreach (var item in enabledMods)
            {
                if (item is not Mod mod)
                {
                    continue;
                }

                var manifest = item.GetType()
                    .GetProperty("Manifest", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(item);

                var manifestId = manifest?.GetType()
                    .GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(manifest) as string;

                if (manifestId == id)
                {
                    return mod;
                }
            }
        }
        catch (Exception ex)
        {
            Runtime.Log($"warning: failed to resolve optimizer mod for pause-menu button: {ex.GetType().Name}: {ex.Message}");
        }

        return null;
    }
}
