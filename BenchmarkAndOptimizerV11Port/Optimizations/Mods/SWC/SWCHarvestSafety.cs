using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Optional SWC harvest/workplace guard. No dependency on any particular SWC binary.
/// Never alters saves, reservations, worker employment, or behavior stacks.
/// </summary>
internal static class SWCHarvestSafety
{
    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static HashSet<object>? _pending;
    private sealed class IdentityComparer : IEqualityComparer<object>
    {
        public new bool Equals(object? a, object? b) => ReferenceEquals(a, b);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private static MethodInfo? _controllerUpdate, _emptyResult;
    private static FieldInfo? _workerField, _yielderCache;
    private static MethodInfo? _workplaceGetter;
    private static bool _installed;

    internal static bool Install(Harmony harmony)
    {
        if (_installed) return true;
        var controller = AccessTools.TypeByName("TonWolfe.SimpleWorkshopControl.SimpleWorkshopControler");
        if (controller is null) return false; // SWC is optional
        // This older repack already installs exactly these safety hooks.
        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "SWC.HarvestSafety"))
            return false;

        var behavior = AccessTools.TypeByName("Timberborn.BehaviorSystem.BehaviorManager");
        var harvester = AccessTools.TypeByName("Timberborn.Fields.HarvestStarter");
        var workerType = AccessTools.TypeByName("Timberborn.WorkSystem.Worker");
        var resultType = AccessTools.TypeByName("Timberborn.YielderFinding.YielderSearchResult");
        if (behavior is null || harvester is null || workerType is null || resultType is null)
            return false;

        var tick = AccessTools.DeclaredMethod(behavior, "Tick", Type.EmptyTypes);
        var find = AccessTools.DeclaredMethod(harvester, "FindYielder", Type.EmptyTypes);
        var update = AccessTools.DeclaredMethod(controller, "UpdateBlockedState", Type.EmptyTypes);
        var worker = AccessTools.Field(harvester, "_worker");
        var cache = AccessTools.Field(harvester, "_yieldersCache");
        var workplace = AccessTools.PropertyGetter(workerType, "Workplace");
        var empty = AccessTools.DeclaredMethod(resultType, "CreateEmpty", Type.EmptyTypes);
        if (tick is null || find is null || update is null || worker is null ||
            cache is null || workplace is null || empty is null ||
            tick.ReturnType != typeof(void) || update.ReturnType != typeof(void) ||
            worker.FieldType != workerType || find.ReturnType != resultType ||
            empty.ReturnType != resultType || !typeof(IList).IsAssignableFrom(cache.FieldType) ||
            workplace.ReturnType.IsValueType) return false;

        // The post-GetYielders insertion must be unambiguous and outside exception blocks.
        // Future Timberborn IL changes should disable the guard, not risk corrupting a worker.
        try
        {
            var original = PatchProcessor.GetOriginalInstructions(find).ToList();
            if (original.Any(i => i.blocks.Count != 0) || InjectionPoint(original, worker, workplace) < 0)
                return false;
        }
        catch { return false; }

        _controllerUpdate = update;
        _workerField = worker;
        _yielderCache = cache;
        _workplaceGetter = workplace;
        _emptyResult = empty;

        try
        {
            var harvestPrefix = AccessTools.Method(typeof(SWCHarvestSafety), nameof(HarvestPrefix))!
                .MakeGenericMethod(resultType);
            harmony.Patch(find,
                prefix: new HarmonyMethod(harvestPrefix) { priority = Priority.First },
                transpiler: new HarmonyMethod(AccessTools.Method(typeof(SWCHarvestSafety), nameof(HarvestTranspiler)))
                { priority = Priority.Last });
            harmony.Patch(tick,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SWCHarvestSafety), nameof(BehaviorPrefix)))
                { priority = Priority.First },
                finalizer: new HarmonyMethod(AccessTools.Method(typeof(SWCHarvestSafety), nameof(BehaviorFinalizer)))
                { priority = Priority.Last });
            harmony.Patch(update,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(SWCHarvestSafety), nameof(DeferUpdate)))
                { priority = Priority.First });
            _installed = true;
            System.Console.WriteLine("[TB Optimizer] SWC harvest/workplace safety enabled");
            return true;
        }
        catch (Exception ex)
        {
            harmony.Unpatch(find, HarmonyPatchType.All, harmony.Id);
            harmony.Unpatch(tick, HarmonyPatchType.All, harmony.Id);
            harmony.Unpatch(update, HarmonyPatchType.All, harmony.Id);
            System.Console.WriteLine("[TB Optimizer] SWC safety unavailable; SWC performance patch not installed: " + ex.Message);
            return false;
        }
    }

    private static void BehaviorPrefix(ref bool __state)
    {
        ++_depth;
        __state = true;
    }

    private static Exception? BehaviorFinalizer(Exception? __exception, bool __state)
    {
        if (__state) EndScope(__exception);
        return __exception; // preserve original failures
    }

    private static void EndScope(Exception? failure)
    {
        if (_depth <= 0) throw new InvalidOperationException("Unbalanced behavior guard");
        if (--_depth != 0 || _pending is null || _pending.Count == 0) return;
        var updates = _pending.ToArray();
        _pending.Clear(); // even a throwing update cannot be replayed next tick
        if (failure is not null) return;
        foreach (var target in updates)
        {
            try { _controllerUpdate!.Invoke(target, null); }
            catch (TargetInvocationException ex)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException ?? ex).Throw();
                throw;
            }
        }
    }

    private static bool DeferUpdate(object __instance)
    {
        if (_depth == 0) return true;
        (_pending ??= new HashSet<object>(new IdentityComparer())).Add(__instance);
        return false;
    }

    private static bool WorkplaceValid(object starter, object? worker)
    {
        if (worker is not null && _workplaceGetter!.Invoke(worker, null) is not null)
            return true;
        if (_yielderCache!.GetValue(starter) is IList temporary)
            temporary.Clear();
        return false;
    }

    private static bool HarvestPrefix<T>(object __instance, ref T __result)
    {
        if (WorkplaceValid(__instance, _workerField!.GetValue(__instance))) return true;
        __result = (T)_emptyResult!.Invoke(null, null)!;
        return false;
    }

    private static int InjectionPoint(IReadOnlyList<CodeInstruction> code,
        FieldInfo worker, MethodInfo workplace)
    {
        var found = -1;
        for (var i = 2; i + 2 < code.Count; ++i)
        {
            if (code[i].opcode != OpCodes.Ldarg_0 ||
                !code[i + 1].LoadsField(worker) || !code[i + 2].Calls(workplace) ||
                code[i - 1].opcode != OpCodes.Pop ||
                code[i - 2].operand is not MethodInfo method || method.Name != "GetYielders")
                continue;
            if (found != -1) return -1;
            found = i;
        }
        return found;
    }

    private static IEnumerable<CodeInstruction> HarvestTranspiler(
        IEnumerable<CodeInstruction> instructions, ILGenerator generator)
    {
        var code = instructions.ToList();
        if (code.Any(i => i.blocks.Count != 0))
            throw new InvalidOperationException("Unexpected exception regions in harvest method");
        var at = InjectionPoint(code, _workerField!, _workplaceGetter!);
        if (at < 0) throw new InvalidOperationException("Unsupported HarvestStarter IL");
        var proceed = generator.DefineLabel();
        var start = new CodeInstruction(OpCodes.Ldarg_0);
        start.labels.AddRange(code[at].labels);
        code[at].labels.Clear();
        code[at].labels.Add(proceed);
        code.InsertRange(at, new[]
        {
            start,
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Ldfld, _workerField),
            new CodeInstruction(OpCodes.Call,
                AccessTools.Method(typeof(SWCHarvestSafety), nameof(WorkplaceValid))),
            new CodeInstruction(OpCodes.Brtrue, proceed),
            new CodeInstruction(OpCodes.Call, _emptyResult),
            new CodeInstruction(OpCodes.Ret)
        });
        return code;
    }
}
