using System;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace ModSettings.Core
{
    [Flags]
    public enum ModSettingsContext
    {
        None = 0,
        MainMenu = 1,
        Game = 2,
        MapEditor = 4,
        All = MainMenu | Game | MapEditor
    }

    public class ModSettingDescriptor
    {
        public static ModSettingDescriptor CreateLocalized(string nameLocKey) => new ModSettingDescriptor();
        public ModSettingDescriptor SetLocalizedTooltip(string tooltipLocKey) => this;
    }

    public abstract class ModSetting
    {
        public abstract void Reset();
    }

    public class ModSetting<T> : ModSetting
    {
        public event EventHandler<T> ValueChanged;
        public T DefaultValue { get; }
        public T Value { get; private set; }

        public ModSetting(T defaultValue, ModSettingDescriptor descriptor)
        {
            DefaultValue = defaultValue;
        }

        public virtual void SetValue(T value)
        {
            Value = value;
            ValueChanged?.Invoke(this, value);
        }

        public override void Reset() => SetValue(DefaultValue);
    }

    public class ModSettingsOwnerRegistry { }

    public abstract class ModSettingsOwner
    {
        protected ModSettingsOwner(
            ISettings settings,
            ModSettingsOwnerRegistry modSettingsOwnerRegistry,
            ModRepository modRepository) { }

        public virtual string HeaderLocKey => null;
        public virtual ModSettingsContext ChangeableOn => ModSettingsContext.MainMenu;
        protected abstract string ModId { get; }
    }
}