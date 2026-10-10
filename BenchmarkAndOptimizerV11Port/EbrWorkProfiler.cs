using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Diagnostic-only EBR stage profiling. Timers never skip, reorder, modify or
/// dispatch an EBR callback. Core lookup timing is per nav update; the expensive
/// individual checks/callbacks are sampled to limit Stopwatch perturbation.
/// All aggregated counters are main-thread only; ThreadStatic guards ensure
/// unrelated calls to shared BlockObjectAccessGenerator/Accessible methods
/// are not misattributed to EBR.
/// </summary>
internal static class EbrWorkProfiler
{
    private const int SampleMask = 31; // Sample first and then every 32nd call.
    private const int ReportSeconds = 15;

    [ThreadStatic] private static bool _sampledCallbackActive;
    private static bool _enabled;
    private static int _intersectionIndex, _notificationIndex;

    // Exact timing for candidate lookup; sample-only timing for per-listener work.
    private static long _lookupTicks, _intersectTicks, _callbackTicks;
    private static long _boundsTicks, _accessTicks, _setTicks, _rebuildTicks;
    private static long _maxLookupTicks, _maxCallbackTicks, _maxRebuildTicks;
    private static long _reportedAt;
    private static int _lookupEvents, _lookupCandidates, _maxLookupCandidates;
    private static int _sampledIntersections, _sampledCallbacks, _sampledRebuilds;
    private static int _boundsCalls, _accessCalls, _setCalls;
    private static int _callbackFailures;

    public static void Patch(Harmony harmony)
    {
        var ebrType = AccessTools.TypeByName(
            "ExtendedBuilderReach.Components.ExtendedDemolishableAccessible");
        if (ebrType is null)
        {
            Runtime.Log("EBR stage profiler inactive: Extended Builder Reach is not installed");
            return;
        }

        // Use field types from the installed EBR assembly, not assumed Timberborn
        // namespaces or method signatures. Unavailable probes degrade gracefully.
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var generator = AccessTools.Field(ebrType, "blockObjectAccessGenerator")?.FieldType;
        var accessible = AccessTools.Field(ebrType, "accessible")?.FieldType;
        var counts = new int[4];

        counts[0] = InstallMethods(harmony, generator, "GenerateAccessBounds", 2);
        counts[1] = InstallMethods(harmony, generator, "GenerateAccesses", 2);
        // v1.1.81: The actual game's SetAccesses signature need not have
        // one parameter. Probe every non-generic overload on the resolved
        // Accessible type, then report the exact runtime signatures patched.
        counts[2] = InstallMethods(harmony, accessible, "SetAccesses", null);
        counts[3] = InstallMethods(harmony, ebrType, "UpdateAccesses", 0);
        _enabled = true;
        _reportedAt = Stopwatch.GetTimestamp();
        Runtime.Log(
            "EBR diagnostic sampler active: every 32nd intersection/callback; " +
            $"stage hooks bounds={counts[0]}, accesses={counts[1]}, apply={counts[2]}, rebuild={counts[3]}; " +
            $"aggregate EBR.PROFILE report every {ReportSeconds}s of runtime activity; " +
            "stage timings are inclusive and callback-only; " +
            "rebuild inclusive timing separates unexplained update cost from notification overhead; " +
            "no worker threads or game-state changes");
    }

    private static int InstallMethods(Harmony harmony, Type? type, string name, int? parameters)
    {
        if (type is null)
        {
            Runtime.Log($"EBR diagnostic probe missing type for {name}");
            return 0;
        }

        var methods = type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(m => m.Name == name && !m.IsAbstract && !m.ContainsGenericParameters &&
                        (!parameters.HasValue || m.GetParameters().Length == parameters.Value))
            .Distinct()
            .ToArray();

        var installed = 0;
        foreach (var method in methods)
        {
            try
            {
                harmony.Patch(method,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(EbrWorkProfiler), nameof(StagePrefix)))
                    { priority = Priority.First },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(EbrWorkProfiler), nameof(StageFinalizer)))
                    { priority = Priority.Last });
                installed++;
                if (name == "SetAccesses")
                {
                    Runtime.Log(
                        $"EBR SetAccesses profiler attached: " +
                        $"{method.DeclaringType?.FullName}.{method.Name}" +
                        $"({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.FullName ?? p.ParameterType.Name))})");
                }
            }
            catch (Exception ex)
            {
                Runtime.Log(
                    $"EBR stage probe skipped {type.Name}.{name}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        if (installed == 0)
        {
            var target = parameters.HasValue ? $"/{parameters.Value}" : "/any-overload";
            var candidateSignatures = name == "SetAccesses"
                ? string.Join("; ", type.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name.Contains("Access", StringComparison.Ordinal))
                    .Select(m => m.Name + "/" + m.GetParameters().Length)
                    .Distinct())
                : "";
            Runtime.Log($"EBR diagnostic probe unavailable: {type.FullName}.{name}{target}" +
                (name == "SetAccesses" ? $"; available access methods=[{candidateSignatures}]" : ""));
        }
        return installed;
    }

    private static void StagePrefix(out long __state)
    {
        // Fast guard: unrelated Timberborn access updates are not measured.
        __state = _sampledCallbackActive ? Stopwatch.GetTimestamp() : 0;
    }

    private static Exception? StageFinalizer(
        Exception? __exception, MethodBase __originalMethod, long __state)
    {
        if (__state == 0) return __exception;

        var ticks = Math.Max(0, Stopwatch.GetTimestamp() - __state);
        switch (__originalMethod.Name)
        {
            case "GenerateAccessBounds":
                _boundsTicks += ticks;
                _boundsCalls++;
                break;
            case "GenerateAccesses":
                _accessTicks += ticks;
                _accessCalls++;
                break;
            case "SetAccesses":
                _setTicks += ticks;
                _setCalls++;
                break;
            case "UpdateAccesses":
                _sampledRebuilds++;
                _rebuildTicks += ticks;
                _maxRebuildTicks = Math.Max(_maxRebuildTicks, ticks);
                break;
        }

        // The exact original exception is propagated. This finalizer never
        // replaces an exception or suppresses the underlying method.
        return __exception;
    }

    public static void RecordSelection(long elapsedTicks, int spatialCandidates,
        int registered, bool usedSpatial)
    {
        if (!_enabled) return;
        _lookupEvents++;
        _lookupTicks += Math.Max(0, elapsedTicks);
        _maxLookupTicks = Math.Max(_maxLookupTicks, elapsedTicks);
        var count = usedSpatial ? spatialCandidates : registered;
        _lookupCandidates += count;
        _maxLookupCandidates = Math.Max(_maxLookupCandidates, count);
    }

    public static bool Intersects(object listener, object update,
        Func<object, object, bool> original)
    {
        if (!_enabled || ((_intersectionIndex++ & SampleMask) != 0))
            return original(listener, update);

        var started = Stopwatch.GetTimestamp();
        try
        {
            return original(listener, update);
        }
        finally
        {
            _sampledIntersections++;
            _intersectTicks += Math.Max(0, Stopwatch.GetTimestamp() - started);
        }
    }

    public static void Notify(object listener, object update,
        Action<object, object> original)
    {
        if (!_enabled || ((_notificationIndex++ & SampleMask) != 0))
        {
            original(listener, update);
            return;
        }

        var previous = _sampledCallbackActive;
        var started = Stopwatch.GetTimestamp();
        _sampledCallbackActive = true;
        try
        {
            original(listener, update);
        }
        catch
        {
            _callbackFailures++;
            throw;
        }
        finally
        {
            _sampledCallbackActive = previous;
            var ticks = Math.Max(0, Stopwatch.GetTimestamp() - started);
            _sampledCallbacks++;
            _callbackTicks += ticks;
            _maxCallbackTicks = Math.Max(_maxCallbackTicks, ticks);
        }
    }

    public static void Pulse()
    {
        if (!_enabled) return;
        var now = Stopwatch.GetTimestamp();
        if (now - _reportedAt < ReportSeconds * Stopwatch.Frequency)
            return;

        _reportedAt = now;
        if (_lookupEvents != 0 || _sampledIntersections != 0 || _sampledCallbacks != 0)
        {
            // A sampled callback includes the sub-stages. The 'other' column
            // estimates callback overhead/repeated bounds checks and is not
            // independently measured. Sub-stage totals only cover sampled calls.
            var inner = _boundsTicks + _accessTicks + _setTicks;
            // These are separately inclusive scopes. Individual method timings
            // incur profiler overhead, so treat residuals as approximations.
            var rebuildOther = Math.Max(0, _rebuildTicks - inner);
            var callbackOther = Math.Max(0, _callbackTicks - _rebuildTicks);
            Runtime.Log(
                "EBR.PROFILE: " +
                $"lookup events={_lookupEvents} total={Ms(_lookupTicks):F2}ms " +
                $"avg={Avg(_lookupTicks, _lookupEvents):F3}ms " +
                $"max={Ms(_maxLookupTicks):F2}ms candidates={_lookupCandidates} " +
                $"maxCandidates={_maxLookupCandidates}; " +
                $"intersection sampled={_sampledIntersections}/32 " +
                $"total={Ms(_intersectTicks):F2}ms avg={Avg(_intersectTicks, _sampledIntersections):F4}ms; " +
                $"callback sampled={_sampledCallbacks}/32 " +
                $"total={Ms(_callbackTicks):F2}ms " +
                $"avg={Avg(_callbackTicks, _sampledCallbacks):F3}ms " +
                $"max={Ms(_maxCallbackTicks):F2}ms failures={_callbackFailures}; " +
                $"rebuild sampled={_sampledRebuilds} " +
                $"total={Ms(_rebuildTicks):F2}ms max={Ms(_maxRebuildTicks):F2}ms; " +
                $"breakdown(sampled) bounds={Ms(_boundsTicks):F2}ms/{_boundsCalls} " +
                $"generate={Ms(_accessTicks):F2}ms/{_accessCalls} " +
                $"apply={Ms(_setTicks):F2}ms/{_setCalls} " +
                $"rebuildOther~={Ms(rebuildOther):F2}ms " +
                $"callbackOther~={Ms(callbackOther):F2}ms; " +
                "stages inclusive; GC pauses may distort wall times");
        }

        _lookupTicks = _intersectTicks = _callbackTicks = 0;
        _boundsTicks = _accessTicks = _setTicks = _rebuildTicks = 0;
        _maxLookupTicks = _maxCallbackTicks = _maxRebuildTicks = 0;
        _lookupEvents = _lookupCandidates = _maxLookupCandidates = 0;
        _sampledIntersections = _sampledCallbacks = _sampledRebuilds = 0;
        _boundsCalls = _accessCalls = _setCalls = _callbackFailures = 0;
    }

    private static double Ms(long ticks) =>
        ticks * 1000.0 / Stopwatch.Frequency;

    private static double Avg(long ticks, int calls) =>
        calls == 0 ? 0 : Ms(ticks) / calls;
}
