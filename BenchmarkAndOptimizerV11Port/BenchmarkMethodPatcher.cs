using System.Diagnostics;
using System.Reflection;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Observes actual tick/update implementations without replaying, suppressing, or
/// replacing Timberborn's dispatcher loops. Discovery and Harmony patch installation
/// are one-time metadata operations; the sampled hot path makes no reflected calls.
/// </summary>
static class BenchmarkMethodPatcher
{
    static readonly HashSet<MethodBase> PatchedMethods = [];

    static readonly string[] MethodNames =
    [
        "Tick",
        "UpdateSingleton",
        "LateUpdateSingleton",
        "StartAndTick"
    ];

    public static void Patch(Harmony harmony, IEnumerable<Type> discoveredTypes)
    {
        var prefix = new HarmonyMethod(
            AccessTools.Method(typeof(BenchmarkMethodPatcher), nameof(BeforeCall)))
        {
            priority = Priority.First
        };
        var postfix = new HarmonyMethod(
            AccessTools.Method(typeof(BenchmarkMethodPatcher), nameof(AfterCall)))
        {
            priority = Priority.Last
        };

        var patched = 0;
        foreach (var type in discoveredTypes)
        {
            if (type.IsAbstract || type.ContainsGenericParameters)
            {
                continue;
            }

            MethodInfo? method = null;
            foreach (var name in MethodNames)
            {
                method = AccessTools.Method(type, name, Type.EmptyTypes);
                if (method?.ReturnType == typeof(void) && !method.IsStatic && !method.ContainsGenericParameters)
                {
                    break;
                }

                method = null;
            }

            if (method is null || !PatchedMethods.Add(method))
            {
                continue;
            }

            try
            {
                // The method may be inherited by multiple concrete types.
                // Patch once and attribute each invocation to its runtime type.
                harmony.Patch(method, prefix: prefix, postfix: postfix);
                patched++;
            }
            catch (Exception ex)
            {
                PatchedMethods.Remove(method);
                Runtime.Log($"benchmark method skipped: {type.FullName}: {ex.Message}");
            }
        }

        Runtime.Log($"benchmark instrumentation installed on {patched} implementation method(s)");
    }

    static void BeforeCall(out long __state)
    {
        __state = Runtime.IsBenchmarking ? Stopwatch.GetTimestamp() : 0;
    }

    static void AfterCall(object __instance, long __state)
    {
        if (__state == 0 || !Runtime.IsBenchmarking || __instance is null)
        {
            return;
        }

        Runtime.RecordBenchmark(__instance.GetType(), Stopwatch.GetTimestamp() - __state);
    }

    public static void Unpatch(Harmony harmony)
    {
        foreach (var method in PatchedMethods)
        {
            harmony.Unpatch(method, HarmonyPatchType.Prefix, harmony.Id);
            harmony.Unpatch(method, HarmonyPatchType.Postfix, harmony.Id);
        }

        PatchedMethods.Clear();
    }
}
