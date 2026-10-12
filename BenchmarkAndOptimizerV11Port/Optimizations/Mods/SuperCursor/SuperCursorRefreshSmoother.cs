using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

internal static class SuperCursorRefreshSmoother
{
    private const double NormalRefreshMs = 50.0;
    private const double ModerateRefreshMs = 100.0;
    private const double SlowRefreshMs = 250.0;

    private sealed class State
    {
        public long NextAllowed;
    }

    private static readonly ConditionalWeakTable<object, State> States = new();
    private static bool _installed;

    public static void Patch(Harmony harmony)
    {
        var type = AccessTools.TypeByName("SuperCursor.Services.SuperCursorTool");
        var method = type?
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                candidate.Name == "ProcessInfo" &&
                candidate.ReturnType == typeof(void) &&
                candidate.GetParameters().Length == 0);

        if (method is null)
        {
            Runtime.Log("SuperCursor refresh smoother not installed: ProcessInfo not found");
            return;
        }

        harmony.Patch(
            method,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(SuperCursorRefreshSmoother), nameof(Prefix)))
            {
                priority = Priority.First
            },
            finalizer: new HarmonyMethod(
                AccessTools.Method(typeof(SuperCursorRefreshSmoother), nameof(Finalizer)))
            {
                priority = Priority.Last
            });

        _installed = true;
        Runtime.Log(
            "SuperCursor refresh smoother installed: info-only refresh capped at 20Hz, " +
            "with 10Hz/4Hz adaptive backoff after expensive refreshes; input/tool actions unchanged");
    }

    private static bool Prefix(object __instance, out long __state)
    {
        __state = 0;
        if (!_installed || Runtime.IsBenchmarking || __instance is null)
        {
            return true;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = States.GetOrCreateValue(__instance);
        if (now < state.NextAllowed)
        {
            return false;
        }

        __state = now;
        return true;
    }

    private static Exception? Finalizer(
        Exception? __exception,
        object __instance,
        long __state)
    {
        if (__state == 0 || __instance is null)
        {
            return __exception;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = now - __state;
        var elapsedMs =
            elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        FreezeDetector.RecordInputDetail("SuperCursor.ProcessInfo", elapsed);

        var refreshMs = elapsedMs >= 100.0
            ? SlowRefreshMs
            : elapsedMs >= 30.0
                ? ModerateRefreshMs
                : NormalRefreshMs;

        States.GetOrCreateValue(__instance).NextAllowed =
            now + (long)(refreshMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);

        return __exception;
    }
}
