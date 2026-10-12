using System;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// SWC's vanilla scanning work is spread across four ticks, preserving its own
/// full scanner on scan ticks and exact cooldown/availability bookkeeping between.
/// No SWC files, game services or persistent save fields are replaced.
/// Requires SWCHarvestSafety to be installed first.
/// </summary>
internal static class SWCScanOptimizer
{
    private const int HarvestInterval = 4;
    private const int PlantInterval = 4;
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static int _harvestPhase, _plantPhase;
    private static readonly ConditionalWeakTable<object, ScanState> HarvestStates = new();
    private static readonly ConditionalWeakTable<object, ScanState> PlantStates = new();
    private sealed class ScanState
    {
        public bool Started;
        public int Countdown;
        public bool HadEmptySpots;
    }
    private static HarvestApi? _harvest;
    private static PlantApi? _plant;

    private static FieldInfo Field(Type type, string name, Type expected)
    {
        var field = type.GetField(name, Instance);
        if (field is null || field.FieldType != expected)
            throw new MissingFieldException(type.FullName, name);
        return field;
    }
    private static FieldInfo Reference(Type type, string name)
    {
        var field = type.GetField(name, Instance);
        if (field is null || field.FieldType.IsValueType)
            throw new MissingFieldException(type.FullName, name);
        return field;
    }
    private static Func<object, T> Read<T>(FieldInfo field)
    {
        var obj = Expression.Parameter(typeof(object), "obj");
        return Expression.Lambda<Func<object, T>>(
            Expression.Field(Expression.Convert(obj, field.DeclaringType!), field), obj).Compile();
    }
    private static Action<object, T> Write<T>(FieldInfo field)
    {
        var obj = Expression.Parameter(typeof(object), "obj");
        var value = Expression.Parameter(typeof(T), "value");
        return Expression.Lambda<Action<object, T>>(
            Expression.Assign(Expression.Field(Expression.Convert(obj, field.DeclaringType!), field), value),
            obj, value).Compile();
    }
    private static Func<object, object?> ReadReference(FieldInfo field)
    {
        var obj = Expression.Parameter(typeof(object), "obj");
        return Expression.Lambda<Func<object, object?>>(
            Expression.Convert(Expression.Field(Expression.Convert(obj, field.DeclaringType!), field),
                typeof(object)), obj).Compile();
    }
    private static Func<object, float> Cooldown(Type type)
    {
        var property = type.GetProperty("cooldown", Instance);
        var getter = property?.GetGetMethod(true);
        if (property?.PropertyType != typeof(float) || getter is null)
            throw new MissingMemberException(type.FullName, "cooldown: float");
        var obj = Expression.Parameter(typeof(object), "obj");
        return Expression.Lambda<Func<object, float>>(
            Expression.Call(Expression.Convert(obj, type), getter), obj).Compile();
    }
    private static Func<object, float, int> HoursToTicks(Type timeType)
    {
        var method = timeType.GetMethods(Instance).SingleOrDefault(m =>
            m.Name == "ScaleTime_HoursToTicks" && m.ReturnType == typeof(int) &&
            m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(float));
        if (method is null)
            throw new MissingMethodException(timeType.FullName, "ScaleTime_HoursToTicks(float)");
        var obj = Expression.Parameter(typeof(object), "obj");
        var hours = Expression.Parameter(typeof(float), "hours");
        return Expression.Lambda<Func<object, float, int>>(
            Expression.Call(Expression.Convert(obj, timeType), method, hours), obj, hours).Compile();
    }
    private static Action<object> UpdateMethod(Type controller)
    {
        var method = AccessTools.DeclaredMethod(controller, "UpdateBlockedState", Type.EmptyTypes);
        if (method is null || method.ReturnType != typeof(void))
            throw new MissingMethodException(controller.FullName, "UpdateBlockedState()");
        var obj = Expression.Parameter(typeof(object), "obj");
        return Expression.Lambda<Action<object>>(
            Expression.Call(Expression.Convert(obj, controller), method), obj).Compile();
    }

    private sealed class HarvestApi
    {
        internal readonly Func<object, bool> Active, Fired;
        internal readonly Action<object, bool> SetFired;
        internal readonly Func<object, int> Ticks;
        internal readonly Action<object, int> SetTicks;
        internal readonly Func<object, object?> Yielders, Terrain, Time, Controller;
        internal readonly Func<object, float> Cooldown;
        internal readonly Func<object, float, int> Scale;
        internal readonly Action<object> Update;
        internal readonly FieldInfo ActiveField, TicksField, FiredField;
        internal HarvestApi(Type type)
        {
            ActiveField = Field(type, "active", typeof(bool));
            TicksField = Field(type, "ticks", typeof(int));
            FiredField = Field(type, "hasFired", typeof(bool));
            Active = Read<bool>(ActiveField);
            Ticks = Read<int>(TicksField);
            SetTicks = Write<int>(TicksField);
            Fired = Read<bool>(FiredField);
            SetFired = Write<bool>(FiredField);
            Yielders = ReadReference(Reference(type, "inRangeYielders"));
            Terrain = ReadReference(Reference(type, "buildingTerrainRange"));
            var time = Reference(type, "timeService");
            Time = ReadReference(time);
            Scale = HoursToTicks(time.FieldType);
            Cooldown = SWCScanOptimizer.Cooldown(type);
            var controller = Reference(type, "workshopController");
            Controller = ReadReference(controller);
            Update = UpdateMethod(controller.FieldType);
        }
    }

    private sealed class PlantApi
    {
        internal readonly Func<object, bool> Active, Available;
        internal readonly Action<object, bool> SetAvailable;
        internal readonly Func<object, int> Progress;
        internal readonly Action<object, int> SetProgress;
        internal readonly Func<object, object?> Coordinates, Time, Controller;
        internal readonly Func<object, float> Cooldown;
        internal readonly Func<object, float, int> Scale;
        internal readonly Action<object> Update;
        internal readonly FieldInfo ActiveField, ProgressField, AvailableField;
        internal PlantApi(Type type)
        {
            ActiveField = Field(type, "usePlantingControl", typeof(bool));
            ProgressField = Field(type, "cooldownProgress", typeof(int));
            AvailableField = Field(type, "spotAvailable", typeof(bool));
            Active = Read<bool>(ActiveField);
            Progress = Read<int>(ProgressField);
            SetProgress = Write<int>(ProgressField);
            Available = Read<bool>(AvailableField);
            SetAvailable = Write<bool>(AvailableField);
            Coordinates = ReadReference(Reference(type, "inRangePlantingCoordinates"));
            var time = Reference(type, "timeService");
            Time = ReadReference(time);
            Scale = HoursToTicks(time.FieldType);
            Cooldown = SWCScanOptimizer.Cooldown(type);
            var controller = Reference(type, "workshopController");
            Controller = ReadReference(controller);
            Update = UpdateMethod(controller.FieldType);
        }
    }

    // Read-only IL check: never skip a new SWC Tick body that no longer uses
    // the original cooldown, state and callback members.
    private static bool Matches(MethodInfo method, params FieldInfo[] required)
    {
        try
        {
            var body = method.GetMethodBody()?.GetILAsByteArray();
            var il = PatchProcessor.GetOriginalInstructions(method).ToList();
            return body is { Length: > 60 } && required.All(f => il.Any(c => c.LoadsField(f))) &&
                   il.Any(c => c.operand is MethodInfo m && m.Name == "UpdateBlockedState");
        }
        catch { return false; }
    }

    internal static bool Install(Harmony harmony)
    {
        var harvest = AccessTools.TypeByName("TonWolfe.SimpleWorkshopControl.PlantMonitor");
        var plant = AccessTools.TypeByName("TonWolfe.SimpleWorkshopControl.PlantingControler");
        if (harvest is null || plant is null) return false;
        // The earlier modified SWC DLL already contains this scheduling logic.
        if (AccessTools.DeclaredMethod(harvest, "PerfRefreshGoods") is not null ||
            AccessTools.DeclaredMethod(plant, "PerfFindEmptySpot") is not null) return false;

        MethodInfo? harvestTick = null, plantTick = null, activation = null, plantingChange = null;
        try
        {
            var ha = new HarvestApi(harvest);
            var pa = new PlantApi(plant);
            harvestTick = AccessTools.DeclaredMethod(harvest, "Tick", Type.EmptyTypes);
            plantTick = AccessTools.DeclaredMethod(plant, "Tick", Type.EmptyTypes);
            if (harvestTick is null || plantTick is null ||
                harvestTick.ReturnType != typeof(void) || plantTick.ReturnType != typeof(void) ||
                !Matches(harvestTick, ha.ActiveField, ha.TicksField, ha.FiredField) ||
                !Matches(plantTick, pa.ActiveField, pa.ProgressField, pa.AvailableField))
                return false;

            _harvest = ha;
            _plant = pa;
            harmony.Patch(harvestTick,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SWCScanOptimizer), nameof(HarvestPrefix))));
            harmony.Patch(plantTick,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SWCScanOptimizer), nameof(PlantPrefix))),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(SWCScanOptimizer), nameof(PlantPostfix))));
            activation = AccessTools.DeclaredMethod(harvest, "SetActive", new[] { typeof(bool) });
            if (activation is not null)
                harmony.Patch(activation, postfix:
                    new HarmonyMethod(AccessTools.Method(typeof(SWCScanOptimizer), nameof(ResetHarvest))));
            plantingChange = AccessTools.DeclaredMethod(plant, "SetUsePlantingControl", new[] { typeof(bool) });
            if (plantingChange is not null)
                harmony.Patch(plantingChange, postfix:
                    new HarmonyMethod(AccessTools.Method(typeof(SWCScanOptimizer), nameof(ResetPlant))));
            System.Console.WriteLine("[TB Optimizer] SWC harvest and planting scans staggered (every 4 ticks)");
            return true;
        }
        catch (Exception ex)
        {
            if (harvestTick is not null) harmony.Unpatch(harvestTick, HarmonyPatchType.All, harmony.Id);
            if (plantTick is not null) harmony.Unpatch(plantTick, HarmonyPatchType.All, harmony.Id);
            if (activation is not null) harmony.Unpatch(activation, HarmonyPatchType.All, harmony.Id);
            if (plantingChange is not null) harmony.Unpatch(plantingChange, HarmonyPatchType.All, harmony.Id);
            _harvest = null;
            _plant = null;
            System.Console.WriteLine("[TB Optimizer] SWC scan optimization skipped: " + ex.Message);
            return false;
        }
    }

    private static void ResetHarvest(object __instance) =>
        HarvestStates.GetOrCreateValue(__instance).Started = false;
    private static void ResetPlant(object __instance) =>
        PlantStates.GetOrCreateValue(__instance).Started = false;

    private static bool HarvestPrefix(object __instance)
    {
        var a = _harvest;
        if (a is null) return true;
        var state = HarvestStates.GetOrCreateValue(__instance);
        if (!a.Active(__instance) ||
            (a.Yielders(__instance) is null && a.Terrain(__instance) is null))
        {
            state.Started = false;
            return true;
        }
        if (!state.Started)
        {
            state.Started = true;
            state.Countdown = 1 + ((Interlocked.Increment(ref _harvestPhase) - 1) & 3);
            return true; // initial scan must run immediately
        }
        if (--state.Countdown <= 0)
        {
            state.Countdown = HarvestInterval;
            return true; // run complete original SWC scanning/validation
        }
        var ticks = a.Ticks(__instance) + 1;
        a.SetTicks(__instance, ticks);
        var threshold = a.Scale(a.Time(__instance)!, a.Cooldown(__instance));
        if (ticks > threshold && !a.Fired(__instance))
        {
            a.SetFired(__instance, true);
            var controller = a.Controller(__instance);
            if (controller is not null) a.Update(controller);
        }
        else if (ticks < threshold)
            a.SetFired(__instance, false);
        return false; // only expensive scan is skipped, not cooldown progression
    }

    private static bool PlantPrefix(object __instance, out bool __state)
    {
        __state = false;
        var a = _plant;
        if (a is null) return true;
        var state = PlantStates.GetOrCreateValue(__instance);
        if (!a.Active(__instance) || a.Coordinates(__instance) is null)
        {
            state.Started = false;
            return true;
        }
        if (!state.Started)
        {
            state.Started = true;
            state.Countdown = 1 + ((Interlocked.Increment(ref _plantPhase) - 1) & 3);
            __state = true;
            return true;
        }
        if (--state.Countdown <= 0)
        {
            state.Countdown = PlantInterval;
            __state = true;
            return true;
        }
        var progress = state.HadEmptySpots ? 0 : a.Progress(__instance) + 1;
        a.SetProgress(__instance, progress);
        var available = progress <= a.Scale(a.Time(__instance)!, a.Cooldown(__instance));
        if (available != a.Available(__instance))
        {
            a.SetAvailable(__instance, available);
            var controller = a.Controller(__instance);
            if (controller is not null) a.Update(controller);
        }
        return false;
    }

    private static void PlantPostfix(object __instance, bool __state)
    {
        if (__state && _plant is { } a)
            PlantStates.GetOrCreateValue(__instance).HadEmptySpots = a.Progress(__instance) == 0;
    }
}
