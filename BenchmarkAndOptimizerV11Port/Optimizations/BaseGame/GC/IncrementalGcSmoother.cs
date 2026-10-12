using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

internal static class IncrementalGcSmoother
{
    private const long StartHeapBytes = 5632L * 1024L * 1024L;
    private const long HighHeapBytes = 6144L * 1024L * 1024L;
    private const long VeryHighHeapBytes = 6656L * 1024L * 1024L;
    private const ulong NormalBudgetNanoseconds = 500_000UL;
    private const ulong HighBudgetNanoseconds = 1_000_000UL;
    private const ulong VeryHighBudgetNanoseconds = 2_000_000UL;

    private static Func<ulong, bool>? _collectIncremental;
    private static Func<bool>? _isIncremental;
    private static bool _disabled;
    private static bool _loggedActive;
    private static bool _loggedUnavailable;

    public static void Initialize()
    {
        try
        {
            var type = AccessTools.TypeByName("UnityEngine.Scripting.GarbageCollector");
            if (type is null)
            {
                LogUnavailable("Unity incremental GC type not found");
                return;
            }

            var collect = type.GetMethod(
                "CollectIncremental",
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(ulong) },
                modifiers: null);
            var incrementalProperty = type.GetProperty(
                "isIncremental",
                BindingFlags.Static | BindingFlags.Public);

            if (collect is null || collect.ReturnType != typeof(bool))
            {
                LogUnavailable("CollectIncremental(ulong) not found");
                return;
            }

            _collectIncremental =
                (Func<ulong, bool>)collect.CreateDelegate(typeof(Func<ulong, bool>));

            var getter = incrementalProperty?.GetGetMethod();
            if (getter is not null && getter.ReturnType == typeof(bool))
            {
                _isIncremental =
                    (Func<bool>)getter.CreateDelegate(typeof(Func<bool>));
            }

            Runtime.Log(
                "incremental GC smoother installed: no forced full collections; " +
                "when managed heap exceeds 5.5GiB, request bounded 0.5/1/2ms incremental slices");
        }
        catch (Exception ex)
        {
            LogUnavailable($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Pulse(long heapBytes)
    {
        if (_disabled || _collectIncremental is null || heapBytes < StartHeapBytes)
        {
            return;
        }

        try
        {
            if (_isIncremental is not null && !_isIncremental())
            {
                _disabled = true;
                LogUnavailable("Unity reports incremental GC disabled");
                return;
            }

            var budget = heapBytes >= VeryHighHeapBytes
                ? VeryHighBudgetNanoseconds
                : heapBytes >= HighHeapBytes
                    ? HighBudgetNanoseconds
                    : NormalBudgetNanoseconds;

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            _collectIncremental(budget);
            var elapsed =
                System.Diagnostics.Stopwatch.GetTimestamp() - started;
            var elapsedMs =
                elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            if (!_loggedActive)
            {
                _loggedActive = true;
                Runtime.Log(
                    $"incremental GC smoother active at {heapBytes / 1024.0 / 1024.0:F0}MiB heap; " +
                    $"requested budget={budget / 1_000_000.0:F1}ms");
            }

            if (elapsedMs >= 20.0)
            {
                Runtime.Log(
                    $"incremental GC slice exceeded soft budget: " +
                    $"requested={budget / 1_000_000.0:F1}ms actual={elapsedMs:F1}ms");
            }
        }
        catch (Exception ex)
        {
            _disabled = true;
            LogUnavailable($"runtime call failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogUnavailable(string reason)
    {
        if (_loggedUnavailable)
        {
            return;
        }

        _loggedUnavailable = true;
        Runtime.Log($"incremental GC smoother unavailable; vanilla GC retained: {reason}");
    }
}
