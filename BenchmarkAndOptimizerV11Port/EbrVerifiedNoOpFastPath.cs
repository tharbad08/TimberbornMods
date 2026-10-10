using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Strictly fail-closed no-op optimization. Never skip a live game setter on
/// matching coordinates alone. Only engage when its EXACT runtime IL proves
/// that SetAccesses solely assigns the two arguments into instance fields,
/// with no calls, branches, observers or other side effects. This guard will
/// remain disabled on more complex game versions by design.
/// </summary>
internal static class EbrVerifiedNoOpFastPath
{
    private static bool _enabled;
    private static FieldInfo? _accesses;
    private static FieldInfo? _destination;
    private static int _skipped;

    public static void Patch(Harmony harmony)
    {
        var ebr = AccessTools.TypeByName(
            "ExtendedBuilderReach.Components.ExtendedDemolishableAccessible");
        var accessible = AccessTools.Field(ebr, "accessible")?.FieldType;
        if (accessible is null)
        {
            Runtime.Log("EBR identical-access fast path inactive: EBR component not found");
            return;
        }

        var methods = accessible.GetMethods(BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic);
        MethodInfo? setter = null;
        foreach (var method in methods)
            if (method.Name == "SetAccesses" && method.GetParameters().Length == 2)
            {
                if (setter is not null)
                {
                    Runtime.Log("EBR identical-access fast path inactive: ambiguous SetAccesses overloads");
                    return;
                }
                setter = method;
            }

        if (setter is null || !IsTriviallyPureSetter(setter, out var a, out var b))
        {
            Runtime.Log("EBR identical-access fast path inactive: runtime setter has side effects " +
                "beyond two assignments (older Accessible source refreshes validators, " +
                "clears/adds accesses, and enables component). Preserve vanilla state and callbacks");
            return;
        }

        // Any other mod can depend on SetAccesses being called even if the
        // vanilla body is pure. In that case, do not patch or suppress it.
        var info = Harmony.GetPatchInfo(setter);
        if (info is not null && info.Owners.Any(owner =>
                owner != harmony.Id))
        {
            Runtime.Log("EBR identical-access fast path inactive: external SetAccesses hooks");
            return;
        }

        try
        {
            _accesses = a;
            _destination = b;
            harmony.Patch(setter, prefix: new HarmonyMethod(
                AccessTools.Method(typeof(EbrVerifiedNoOpFastPath), nameof(Prefix)))
            { priority = Priority.Last });
            _enabled = true;
            Runtime.Log("EBR identical-access fast path installed: verified pure setter; " +
                "only already-equal backing fields may bypass original assignments");
        }
        catch (Exception ex)
        {
            _enabled = false;
            Runtime.Log("EBR identical-access fast path inactive: " +
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool IsTriviallyPureSetter(MethodInfo setter,
        out FieldInfo a, out FieldInfo b)
    {
        a = b = null!;
        var p = setter.GetParameters();
        if (setter.IsStatic || setter.ReturnType != typeof(void) ||
            p.Length != 2 || setter.ContainsGenericParameters) return false;
        var il = setter.GetMethodBody()?.GetILAsByteArray();
        if (il is null) return false;

        // Exact shape:
        // ldarg.0 ldarg.1 stfld <token>; ldarg.0 ldarg.2 stfld <token>; ret.
        // No method calls, virtual dispatch, events, branch, or allocation.
        // Any additional opcode disables the fast path. The method's IL is
        // never modified, transpiled or guessed from its name.
        if (il.Length != 15 || il[0] != 0x02 || il[1] != 0x03 ||
            il[2] != 0x7D || il[7] != 0x02 || il[8] != 0x04 ||
            il[9] != 0x7D || il[14] != 0x2A) return false;
        try
        {
            var f1 = setter.Module.ResolveField(BitConverter.ToInt32(il, 3));
            var f2 = setter.Module.ResolveField(BitConverter.ToInt32(il, 10));
            if (f1 is not FieldInfo first || f2 is not FieldInfo second ||
                first.IsStatic || second.IsStatic ||
                first.DeclaringType != setter.DeclaringType ||
                second.DeclaringType != setter.DeclaringType ||
                first.FieldType != p[0].ParameterType ||
                second.FieldType != p[1].ParameterType)
                return false;
            a = first;
            b = second;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool Prefix(object __instance, object? __0, object? __1)
    {
        if (!_enabled || _accesses is null || _destination is null)
            return true;

        // Pure reference assignment + nullable-value assignment would leave
        // the same state. Compare ACTUAL live fields, not stale cached values.
        // Preserve all calls if either value differs.
        if (!ReferenceEquals(_accesses.GetValue(__instance), __0) ||
            !Equals(_destination.GetValue(__instance), __1))
            return true;

        if (++_skipped == 1000)
            Runtime.Log("EBR identical-access fast path: 1000 verified redundant assignments skipped");
        return false;
    }
}
