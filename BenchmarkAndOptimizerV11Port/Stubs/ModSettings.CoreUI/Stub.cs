using ModSettings.Core;
using UnityEngine.UIElements;
namespace ModSettings.CoreUI
{
    public interface IModSettingElement { VisualElement Root { get; } ModSetting ModSetting { get; } bool ShouldBlockInput { get; } }
    public interface IModSettingElementFactory { int Priority { get; } bool TryCreateElement(ModSetting modSetting, out IModSettingElement? element); }
}
