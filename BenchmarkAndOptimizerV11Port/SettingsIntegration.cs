using Bindito.Core;
using ModSettings.CommonUI;
using ModSettings.Core;
using ModSettings.CoreUI;
using Timberborn.CoreUI;
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
    public override void Configure() =>
        this.BindSingleton<OptimizerSettingsOwner>()
            .MultiBindSingleton<IModSettingElementFactory, OptimizerSettingElementFactory>()
            .BindSingleton<OptimizerSettingsBoxWidthService>();
}

[Context("Game")]
public sealed class GameConfig : Configurator
{
    public override void Configure() =>
        this.BindSingleton<OptimizerSettingsOwner>()
            .MultiBindSingleton<IModSettingElementFactory, OptimizerSettingElementFactory>()
            .BindSingleton<OptimizerSettingsBoxWidthService>();
}

public sealed class OptimizerSettingsOwner(
    ISettings settings,
    ModSettingsOwnerRegistry registry,
    ModRepository repository
) : ModSettingsOwner(settings, registry, repository)
{
    public override string ModId => "TBOptimizer";
    public override ModSettingsContext ChangeableOn => ModSettingsContext.All;
    public TBOptimizerUiSetting Optimizations { get; } = new();
}

public sealed class TBOptimizerUiSetting()
    : NonPersistentSetting(ModSettingDescriptor.Create("TB Optimizer"))
{
    public override void Reset() { }
}

public sealed class TBOptimizerPanel : VisualElement
{
    public TBOptimizerPanel()
    {
        this.SetPadding(8);
        this.AddGameLabel("TB Optimizer — optimizations", bold: true).SetMarginBottom(6);
        this.AddGameLabel("Changes take effect after restarting Timberborn.").SetMarginBottom(10);
        foreach (var item in OptimizationToggles.Options)
        {
            var group = this.AddChild().SetMarginBottom(11);
            var key = item.Key;
            var header = group.AddRow().AlignItems(Align.Center);
            header.AddGameLabel(item.Label).SetMarginRight(8f);
            var toggle = header.AddToggle("", onValueChanged: v => OptimizationToggles.Set(key, v));
            toggle.SetValueWithoutNotify(OptimizationToggles.Enabled(key));
            group.AddGameLabel(item.Description).SetMarginBottom(2);
        }
    }
}

public sealed class OptimizerSettingElementFactory : IModSettingElementFactory
{
    public int Priority { get; }
    public bool TryCreateElement(ModSetting setting, out IModSettingElement? element)
    {
        if (setting is TBOptimizerUiSetting)
        {
            element = new ModSettingElement(new TBOptimizerPanel(), setting);
            return true;
        }
        element = null;
        return false;
    }
}

public sealed class OptimizerSettingsBoxWidthService(IContainer container) : IPostLoadableSingleton
{
    public void PostLoad()
    {
        try
        {
            container.GetInstance<ModSettingsBox>().GetPanel().Q("Box")?.SetWidth(885f);
        }
        catch { /* Fallback to normal settings width. */ }
    }
}
