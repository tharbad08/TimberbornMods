using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Bindito.Core;
using HarmonyLib;
using ModSettings.Core;
using Timberborn.ModManagerScene;
using Timberborn.Modding;
using Timberborn.SettingsSystem;

namespace Shay.RainbornAcidChanceCap
{
    public sealed class ModStarter : IModStarter
    {
        private const string HarmonyId = "Shay.RainbornAcidChanceCap";

        public void StartMod(IModEnvironment modEnvironment)
        {
            new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    internal sealed class ClampedIntModSetting : ModSetting<int>
    {
        private readonly int _min;
        private readonly int _max;

        public ClampedIntModSetting(int defaultValue, int min, int max, ModSettingDescriptor descriptor)
            : base(defaultValue, descriptor)
        {
            _min = min;
            _max = max;
        }

        public override void SetValue(int value)
        {
            if (value < _min) value = _min;
            if (value > _max) value = _max;
            base.SetValue(value);
        }
    }

    internal sealed class AcidChanceCapSettings : ModSettingsOwner
    {
        internal const int OriginalRainbornCap = 50;
        internal static int CurrentPercent { get; private set; } = OriginalRainbornCap;

        public ModSetting<int> MaxChancePercent { get; } = new ClampedIntModSetting(
            OriginalRainbornCap,
            0,
            100,
            ModSettingDescriptor
                .CreateLocalized("Shay.RainbornAcidChanceCap.Settings.MaxChance")
                .SetLocalizedTooltip("Shay.RainbornAcidChanceCap.Settings.MaxChance.Tooltip"));

        public AcidChanceCapSettings(
            ISettings settings,
            ModSettingsOwnerRegistry modSettingsOwnerRegistry,
            ModRepository modRepository)
            : base(settings, modSettingsOwnerRegistry, modRepository)
        {
            MaxChancePercent.ValueChanged += (_, value) => CurrentPercent = value;
        }

        public override string HeaderLocKey => "Shay.RainbornAcidChanceCap.Settings.Header";
        public override ModSettingsContext ChangeableOn => ModSettingsContext.All;
        protected override string ModId => "Shay.RainbornAcidChanceCap";

        internal static float CurrentFraction()
        {
            var value = CurrentPercent;
            if (value < 0) value = 0;
            if (value > 100) value = 100;
            return value / 100f;
        }
    }

    [Context("MainMenu")]
    [Context("Game")]
    internal sealed class SettingsConfigurator : IConfigurator
    {
        public void Configure(IContainerDefinition containerDefinition)
        {
            containerDefinition.Bind<AcidChanceCapSettings>().AsSingleton();
        }
    }

    [HarmonyPatch]
    internal static class RainSchedulerRollAcidPatch
    {
        private static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("AcademicsRainborn.RainScheduler")
                       ?? throw new TypeLoadException("Academic's Rainborn type AcademicsRainborn.RainScheduler was not found.");
            return AccessTools.Method(type, "RollAcid")
                   ?? throw new MissingMethodException(type.FullName, "RollAcid");
        }

        private static readonly MethodInfo CapGetter = AccessTools.Method(
            typeof(AcidChanceCapSettings),
            nameof(AcidChanceCapSettings.CurrentFraction));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            var matches = 0;

            for (var i = 0; i < list.Count; i++)
            {
                if (!IsHardCodedHalf(list[i]))
                    continue;

                matches++;
                var replacement = new CodeInstruction(OpCodes.Call, CapGetter);
                replacement.labels.AddRange(list[i].labels);
                replacement.blocks.AddRange(list[i].blocks);
                list[i] = replacement;
            }

            if (matches != 2)
            {
                throw new InvalidOperationException(
                    $"Rainborn Acid Chance Cap expected exactly two hard-coded 0.5 constants in RainScheduler.RollAcid, but found {matches}. " +
                    "Rainborn may have changed; refusing to patch ambiguously.");
            }

            return list;
        }

        private static bool IsHardCodedHalf(CodeInstruction instruction)
        {
            if (instruction.opcode == OpCodes.Ldc_R4 && instruction.operand is float f)
                return Math.Abs(f - 0.5f) < 0.000001f;

            if (instruction.opcode == OpCodes.Ldc_R8 && instruction.operand is double d)
                return Math.Abs(d - 0.5d) < 0.000000001d;

            return false;
        }
    }
}