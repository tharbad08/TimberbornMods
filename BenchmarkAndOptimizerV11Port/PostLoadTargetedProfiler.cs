using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Bounded, optional diagnostic for a small explicit PostLoad allowlist.
/// No assembly-wide GetTypes enumeration, no patching arbitrary mod handlers.
/// Times implementations without changing their arguments or return values.
/// </summary>
internal static class PostLoadTargetedProfiler
{
    private static readonly Dictionary<MethodBase, string> Names = new();
    private static readonly Dictionary<string, (long Total, long Max, int Calls)> Stats = new();
    private static readonly object Sync = new();
    private static readonly string[] Types =
    {
        "Timberborn.WorldPersistence.WorldEntitiesLoader",
        "Timberborn.GameStartup.GameInitializer",
        // Existing optimizer already resolves this exact Timberborn 1.1 class.
        // T3MP also wraps its PostLoad; this is the highest-value 226s suspect.
        "Timberborn.Navigation.NavigationSynchronizer",
        "Timberborn.NavigationSystem.NavigationService",
        "Timberborn.PathSystem.PathService",
        "Timberborn.WaterSystem.WaterService",
        "Timberborn.WaterSystem.WaterSimulator",
        "Timberborn.GameDistricts.DistrictService",
        "Timberborn.EntitySystem.EntityService",
        "Timberborn.TerrainSystem.TerrainService",
        "Timberborn.SingletonSystem.SingletonLifecycleService"
    };

    public static void Patch(Harmony harmony)
    {
        int installed = 0;
        foreach (var name in Types)
        {
            var t = AccessTools.TypeByName(name);
            if (t is null) continue;
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name == "PostLoad" && m.GetParameters().Length == 0 &&
                    !m.IsAbstract && !m.ContainsGenericParameters).ToArray();
            if (methods.Length != 1) continue;
            try
            {
                harmony.Patch(methods[0],
                    prefix: new HarmonyMethod(AccessTools.Method(
                        typeof(PostLoadTargetedProfiler), nameof(Begin))),
                    postfix: new HarmonyMethod(AccessTools.Method(
                        typeof(PostLoadTargetedProfiler), nameof(End))));
                Names[methods[0]] = name;
                installed++;
                Runtime.Log("POSTLOAD.TARGET installed=" + name + ".PostLoad");
            }
            catch (Exception e)
            {
                Runtime.Log("POSTLOAD.TARGET skipped=" + name + " reason=" + e.GetType().Name);
            }
        }
        Runtime.Log("POSTLOAD.TARGET allowlist=" + Types.Length + " installed=" + installed +
            " no broad mod-method scan; inclusive times; phase timer retained");
    }

    private static void Begin(out long __state) => __state = Stopwatch.GetTimestamp();

    private static void End(MethodBase __originalMethod, long __state)
    {
        if (!Names.TryGetValue(__originalMethod, out var name)) return;
        var elapsed = Stopwatch.GetTimestamp() - __state;
        lock (Sync)
        {
            Stats.TryGetValue(name, out var current);
            Stats[name] = (current.Total + elapsed, Math.Max(current.Max, elapsed), current.Calls + 1);
        }
        if (elapsed > Stopwatch.Frequency / 5)
            Runtime.Log($"POSTLOAD.TARGET slow={name} elapsed={elapsed * 1000.0 / Stopwatch.Frequency:F1}ms");
    }

    public static void Report()
    {
        lock (Sync)
        {
            foreach (var pair in Stats.OrderByDescending(x => x.Value.Total).Take(12))
            {
                var x = pair.Value;
                Runtime.Log($"POSTLOAD.TARGET.SUMMARY {pair.Key} count={x.Calls} " +
                    $"inclusive={x.Total * 1000.0 / Stopwatch.Frequency:F1}ms " +
                    $"max={x.Max * 1000.0 / Stopwatch.Frequency:F1}ms");
            }
            Stats.Clear();
        }
    }
}
