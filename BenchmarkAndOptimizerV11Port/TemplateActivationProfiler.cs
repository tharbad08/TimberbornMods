using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Observes the last ~300ms of large preview population without changing any
/// activation, template initialization or prefab lifecycle calls.
///
/// T3MP's reviewed PreparedEntityVisuals.Activate(root, active) wraps the
/// original GameObject.SetActive call and optionally calls Prepare first.
/// In regular gameplay T3MP's loading depth is zero, so Activate mostly times
/// native SetActive and Unity/other-mod Awake/OnEnable callbacks.
///
/// Timings here are inclusive and scoped exclusively to block-tool input
/// (not world-load activation). No transpilers, prefix suppression, workers,
/// activation reordering, or allocations proportional to preview count.
/// </summary>
internal static class TemplateActivationProfiler
{
    private static bool _activationInstalled;
    private static bool _prepareInstalled;
    private static long _activations;
    private static long _activationTicks, _maxActivationTicks;
    private static long _prepareCalls, _prepareTicks;
    private static long _lastReport;
    private static int _reportedEvents;

    public static void Patch(Harmony harmony)
    {
        var type = AccessTools.TypeByName("T3MP.Loading.PreparedEntityVisuals");
        if (type is null)
        {
            Runtime.Log("template activation stage profiler inactive: T3MP PreparedEntityVisuals not found; " +
                "template inner remainder remains unclassified");
            return;
        }

        _activationInstalled = Attach(harmony, type, "Activate", nameof(ActivationPrefix),
            nameof(ActivationFinalizer), 2);
        _prepareInstalled = Attach(harmony, type, "Prepare", nameof(PreparePrefix),
            nameof(PrepareFinalizer), 2);

        Runtime.Log($"template activation stage profiler: T3MP.Activate={_activationInstalled}, " +
            $"T3MP.Prepare={_prepareInstalled}; block-tool-only inclusive timing; " +
            "GameObject.SetActive / Unity Awake / OnEnable unmodified");
    }

    private static bool Attach(Harmony harmony, Type type, string methodName,
        string prefixName, string finalizerName, int argumentCount)
    {
        var methods = type.GetMethods(BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == methodName && m.GetParameters().Length == argumentCount)
            .ToArray();
        if (methods.Length != 1)
        {
            Runtime.Log($"template activation stage profiler: {type.FullName}.{methodName} " +
                $"has {methods.Length} matching methods; skip ambiguous hook");
            return false;
        }

        try
        {
            harmony.Patch(methods[0],
                prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(TemplateActivationProfiler), prefixName))
                    { priority = Priority.First },
                finalizer: new HarmonyMethod(AccessTools.Method(
                    typeof(TemplateActivationProfiler), finalizerName))
                    { priority = Priority.Last });
            return true;
        }
        catch (Exception ex)
        {
            Runtime.Log($"template activation stage profiler unavailable: {methodName} " +
                $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void ActivationPrefix(out long __state)
    {
        __state = _activationInstalled && HotInputDetailProfiler.BlockScopeActive
            ? Stopwatch.GetTimestamp()
            : 0;
    }

    private static Exception? ActivationFinalizer(Exception? __exception, long __state)
    {
        if (__state == 0) return __exception;
        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - __state);
        FreezeDetector.RecordInputDetail("Block.TemplateActivation.Activate", elapsed);
        FreezeDetector.RecordHotspotCount("Block.TemplateActivation.Calls");
        _activations++;
        _activationTicks += elapsed;
        _maxActivationTicks = Math.Max(_maxActivationTicks, elapsed);
        MaybeReport();
        return __exception;
    }

    private static void PreparePrefix(out long __state)
    {
        __state = _prepareInstalled && HotInputDetailProfiler.BlockScopeActive
            ? Stopwatch.GetTimestamp()
            : 0;
    }

    private static Exception? PrepareFinalizer(Exception? __exception, long __state)
    {
        if (__state == 0) return __exception;
        var elapsed = Math.Max(0, Stopwatch.GetTimestamp() - __state);
        FreezeDetector.RecordInputDetail("Block.TemplateActivation.Prepare", elapsed);
        _prepareCalls++;
        _prepareTicks += elapsed;
        return __exception;
    }

    private static void MaybeReport()
    {
        // A large brush has ~899 activation calls. Periodic aggregate samples
        // are enough to verify the hook in a normal non-freezing run without
        // logging for each individual preview entity.
        if (_activations - _reportedEvents < 899) return;
        _reportedEvents = (int)Math.Min(int.MaxValue, _activations);
        Runtime.Log(
            $"TEMPLATE.ACTIVATION: block-tool calls={_activations}, " +
            $"total={Ms(_activationTicks):F2}ms, " +
            $"avg={Ms(_activationTicks) / Math.Max(1, _activations):F3}ms, " +
            $"max={Ms(_maxActivationTicks):F3}ms, " +
            $"visualPrepare calls={_prepareCalls} total={Ms(_prepareTicks):F2}ms; " +
            "activation includes Unity GameObject.SetActive and all lifecycle work; " +
            "the difference between TemplateInstantiator and BaseInstantiator + " +
            "Activate is an approximate initializer/other residual");
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
