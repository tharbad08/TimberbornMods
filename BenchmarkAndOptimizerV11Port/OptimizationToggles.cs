using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Persistent switches evaluated BEFORE Harmony patches are installed.
/// Changes made in settings take effect on next full game start.
/// </summary>
internal static class OptimizationToggles
{
    internal sealed record Entry(string Key, string Label, string Description);
    internal static readonly Entry[] Options =
    {
        new("BFR", "BFR local navigation batching", "Groups sparse terrain changes in 8x8 areas.\nAvoids unnecessary large-area scans."),
        new("EBR", "EBR spatial navigation updates", "Filters unaffected builders by terrain bounds.\nDefers large notification batches safely."),
        new("PreviewNav", "Preview navigation batching", "Coalesces preview add/remove notifications.\nPreserves validation and navigation results."),
        new("PreviewPool", "Unused preview pool fast path", "Avoids creating previews for a brush\nthat was never displayed."),
        new("PreviewSweep", "Efficient preview hide sweep", "Removes repeated LINQ/HashSet allocations\nwhile keeping preview validation intact."),
        new("BlueprintCache", "Blueprint ComponentSpec cache", "Caches immutable blueprint component lookups\nused by construction and preview creation."),
        new("BulkBuild", "Faster bulk component creation", "Uses verified warmed ComponentSpecs.\nKeeps normal component ordering and hooks."),
        new("FaunaBudget", "Keystone fauna spawn budget", "Limits expensive actual spawns to one\nper update without dropping queued spawns."),
        new("FaunaCache", "Keystone fauna recipe cache", "Avoids repeated recipe searches\nwhen spawning fauna."),
        new("FaunaPrewarm", "Keystone fauna prewarming", "Prewarms Cow, Bull and Deer templates\nafter game loading."),
        new("SoilReset", "Soil contamination reset", "Optimizes contamination cleanup work.\nDoes not throttle soil simulation ticks."),
        new("SuperCursor", "SuperCursor update smoothing", "Reduces frequent informational refreshes.\nDoes not delay player tool actions."),
        new("SWC", "SWC scans + harvest safety", "Spreads harvest/planting scans over 4 ticks.\nProtects workplace changes during harvesting."),
        new("PhysicsCatchup", "TimberPhysics catch-up limiter", "Limits expensive accumulated physics substeps.\nMay drop excess catch-up under overload."),
        new("PhysicsCollider", "TimberPhysics collider merging", "Combines terrain colliders in 32x32 chunks.\nUpdates changed chunks in batches."),
        new("GcSmoother", "Incremental GC smoothing", "Applies only when Unity supports incremental GC.\nOtherwise retains vanilla GC."),
    };
    private static readonly Dictionary<string, bool> Values = new(StringComparer.Ordinal);
    private static string _path = "";

    internal static void Initialize(string root)
    {
        _path = Path.Combine(root, "tb-optimizer-options.json");
        Values.Clear();
        try
        {
            if (File.Exists(_path))
            {
                var data = JsonConvert.DeserializeObject<Dictionary<string,bool>>(File.ReadAllText(_path));
                if (data != null) foreach (var item in data) Values[item.Key] = item.Value;
            }
        }
        catch { /* Corrupt settings fail open to established optimizations. */ }
    }

    internal static bool Enabled(string key) => !Values.TryGetValue(key, out var value) || value;
    internal static void Set(string key, bool value)
    {
        Values[key] = value;
        try { if (_path.Length > 0) File.WriteAllText(_path,
            JsonConvert.SerializeObject(Values, Formatting.Indented)); }
        catch { /* Never throw from an options callback. */ }
    }
}
