using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

// TB Optimizer intentionally contains only gameplay-preserving feature patches.
// Interval throttling, benchmarking, CSV and freeze profiling are maintained
// exclusively by the BenchmarkAndOptimizerV11Port branch.
public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        OptimizationToggles.Initialize(modEnvironment.ModPath);
        var harmony = new Harmony("tharbad08.TBOptimizer");
        if (OptimizationToggles.Enabled("BFR")) BfrLocalizedChangeOptimizerPatcher.Patch(harmony);
        // Only install the EBR hook when EBR itself is loaded and enabled.
        // The upstream EBR optimization, when present, owns its own dispatcher.
        if (OptimizationToggles.Enabled("EBR") &&
            AccessTools.TypeByName("ExtendedBuilderReach.Components.ExtendedDemolishableAccessible") is not null &&
            AccessTools.TypeByName("ExtendedBuilderReach.Optimizations.ExtendedBuilderReachNavOptimizer") is null &&
            EbrDeferredDrainHook.Install(harmony))
        {
            ExtendedBuilderReachNavOptimizer.Patch(harmony);
        }
        if (OptimizationToggles.Enabled("PreviewNav")) PreviewNavMeshBatcher.Patch(harmony);
        if (OptimizationToggles.Enabled("PreviewPool")) UnusedPreviewPoolFastPath.Patch(harmony);
        if (OptimizationToggles.Enabled("PreviewSweep")) FullyBuildablePreviewFastPath.Patch(harmony);
        if (OptimizationToggles.Enabled("BlueprintCache")) BlueprintSpecInstantiationCache.Patch(harmony);
        if (OptimizationToggles.Enabled("BulkBuild")) BulkComponentInstantiationFastPath.Patch(harmony);
        if (OptimizationToggles.Enabled("FaunaBudget")) FaunaSpawnBudgetPatcher.Patch(harmony);
        if (OptimizationToggles.Enabled("FaunaCache")) FaunaRecipeLookupCachePatcher.Patch(harmony);
        if (OptimizationToggles.Enabled("FaunaPrewarm")) KeystoneFaunaTemplatePrewarmer.Patch(harmony);
        if (OptimizationToggles.Enabled("SoilReset")) SoilContaminationResetOptimizer.Patch(harmony);
        if (OptimizationToggles.Enabled("SuperCursor")) SuperCursorRefreshSmoother.Patch(harmony);
        if (OptimizationToggles.Enabled("PhysicsCatchup")) TimberPhysicsCatchUpLimiterPatcher.Patch(harmony);
        if (OptimizationToggles.Enabled("PhysicsCollider")) TimberPhysicsTerrainColliderMergerPatcher.Patch(harmony);
        if (OptimizationToggles.Enabled("GcSmoother")) IncrementalGcSmoother.Initialize();
    }
}

// Legacy optimizer modules still reference these harmless no-op diagnostic
// entry points. Nothing measures frames or creates diagnostic log files.
internal static class Runtime
{
    public static bool IsBenchmarking => false;
    public static bool Enabled => true;

    [System.Diagnostics.Conditional("OPTIMIZER_DIAGNOSTICS")]
    public static void Log(string message) { }
}

internal static class FreezeDetector
{
    [System.Diagnostics.Conditional("OPTIMIZER_DIAGNOSTICS")]
    public static void RecordBfrDetail(string name, long ticks) { }
    [System.Diagnostics.Conditional("OPTIMIZER_DIAGNOSTICS")]
    public static void RecordSoilDetail(string name, long ticks) { }
    [System.Diagnostics.Conditional("OPTIMIZER_DIAGNOSTICS")]
    public static void RecordNavigationDetail(string name, long ticks) { }
    [System.Diagnostics.Conditional("OPTIMIZER_DIAGNOSTICS")]
    public static void RecordInputDetail(string name, long ticks) { }
    [System.Diagnostics.Conditional("OPTIMIZER_DIAGNOSTICS")]
    public static void RecordTargetDetail(string name, long ticks) { }
    [System.Diagnostics.Conditional("OPTIMIZER_DIAGNOSTICS")]
    public static void RecordSystem(string name, long ticks) { }
}
