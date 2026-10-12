using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Drains EBR's deferred nav-mesh notifications once per normal Unity frame.
/// Replaces the old call hidden inside the now-removed FreezeDetector profiler.
/// If a safe frame hook cannot be installed, the EBR optimization is not armed.
/// </summary>
internal static class EbrDeferredDrainHook
{
    internal static bool Install(Harmony harmony)
    {
        var t = AccessTools.TypeByName("Timberborn.SingletonSystem.SingletonLifecycleService");
        var m = t is null ? null : AccessTools.Method(t, "UpdateSingletons");
        var callback = AccessTools.Method(typeof(EbrDeferredDrainHook), nameof(AfterUpdateSingletons));
        if (m is null || callback is null) return false;
        try
        {
            harmony.Patch(m, postfix: new HarmonyMethod(callback));
            return true;
        }
        catch { return false; }
    }

    // Does not touch Timberborn's global tick buckets or ordering.
    public static void AfterUpdateSingletons() =>
        ExtendedBuilderReachNavOptimizer.DrainDeferred();
}
