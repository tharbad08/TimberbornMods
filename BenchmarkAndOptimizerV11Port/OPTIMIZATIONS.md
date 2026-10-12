# TB Optimizer — retained optimizations and PR source map

All active gameplay optimizers live in individual files. Every patch is optional in the TB Optimizer settings and requires a restart. The optimizer preserves protected simulation tick cadence.

## Third-party mod optimizations

| Mod | Settings key | PR-oriented source | Behavior |
| --- | --- | --- | --- |
| Extended Builder Reach (EBR) | EBR | `Optimizations/Mods/ExtendedBuilderReach/` | Local spatial listener buckets, exact bounds checks, deferred large notification batches (256 candidates or 8 ms per frame), pooled candidate arrays and cached coordinate access; preserve original callbacks and ordering |
| BFR | BFR | `Optimizations/Mods/BFR/BfrLocalizedChangeOptimizerPatcher.cs` | Localized sparse map/navigation changes in 8x8 regions rather than repeatedly scanning a large changed area |
| Keystone | FaunaBudget | `Optimizations/Mods/Keystone/FaunaSpawnBudgetPatcher.cs` | One actual terrestrial fauna creation per update with backlog retained |
| Keystone | FaunaCache | `Optimizations/Mods/Keystone/FaunaRecipeLookupCachePatcher.cs` | Cache repeated fauna blueprint/recipe lookup |
| Keystone | FaunaPrewarm | `Optimizations/Mods/Keystone/KeystoneFaunaTemplatePrewarmer.cs` | Prewarm Cow, Bull and Deer templates after game initialization using the game's original cache |
| SuperCursor | SuperCursor | `Optimizations/Mods/SuperCursor/SuperCursorRefreshSmoother.cs` | Smooth frequent informational UI refreshes without delaying player actions |
| Bober's Laws of Motion (TimberPhysics) | PhysicsCatchup | `Optimizations/Mods/TimberPhysics/TimberPhysicsCatchUpLimiterPatcher.cs` | Bound expensive physics catch-up to four fixed substeps, with ~50 ms expensive-step budget and overload debt discarded |
| Bober's Laws of Motion (TimberPhysics) | PhysicsCollider | `Optimizations/Mods/TimberPhysics/TimberPhysicsTerrainColliderMergerPatcher.cs` | Exact-geometry conservative terrain collider merges in 32x32 dirty chunks and batched rebuilds |

## Base game / shared infrastructure

| Feature | Setting | PR-oriented source | Behavior |
| --- | --- | --- | --- |
| Preview navigation | PreviewNav | `Optimizations/BaseGame/Previews/PreviewNavMeshBatcher.cs` | Batch preview add/remove navigation notifications |
| Preview fast sweep | PreviewSweep | `Optimizations/BaseGame/Previews/FullyBuildablePreviewFastPath.cs` | Reduce repeated LINQ/HashSet work when clearing fully-buildable preview collections |
| Unused preview fast path | PreviewPool | `Optimizations/BaseGame/Previews/UnusedPreviewPoolFastPath.cs` | Skip materializing a preview brush that has never appeared |
| Blueprint component caching | BlueprintCache | `Optimizations/BaseGame/Previews/BlueprintSpecInstantiationCache.cs` | Cache immutable blueprint component specifications |
| Safe bulk construction | BulkBuild | `Optimizations/BaseGame/Previews/BulkComponentInstantiationV11.cs` | Accelerate repeated validated four-argument bulk instantiation, retaining game-native live components and other mods' hooks |
| Soil contamination reset | SoilReset | `Optimizations/BaseGame/Soil/SoilContaminationResetOptimizer.cs` | Faster contamination clearing/reset; normal moisture and contamination updates are not throttled |
| Dynamic assembly startup guard | DynamicAssembly | `Optimizations/BaseGame/Startup/BinditoConfiguratorDynamicAssemblyGuard.cs` | Skip incomplete Harmony-generated dynamic assemblies in verified type-discovery startup paths |
| Incremental GC compatibility | GcSmoother | `Optimizations/BaseGame/GC/IncrementalGcSmoother.cs` | Conditional only; does nothing when Unity reports incremental GC unavailable |
| Advanced interval controls | Intervals | `ModStarter.cs` and `SettingsIntegration.cs` | Optional intervals for eligible methods; default vanilla 1; physics, navigation, water, work, planting/soil-authority and speed controllers protected |

## Deliberately excluded from gameplay optimizations

- No skipped global game ticks or disabled scheduling of authoritative simulation systems.
- No 4th-tick soil contamination service: planting/work control requires normal cadence.
- No EBR `Accessible.SetAccesses` bypass: setter is not proven side-effect-free and performs validator/access refresh.
- No historical EBR value-type IL shortcut that threw `InvalidProgramException`.
- No speculative multithreaded EBR callback execution.
- No save-serialization rewrite or compression-offloading experiment. Save research remains on `chatgpt/TBSave`.
- No attempted forced/incremental GC when Unity has incremental GC disabled. GC research remains on `chatgpt/TBGC`.
- No heavy input, PlayerLoop, soil inner-loop or fauna timing probes in the release patch registration.
- No speculative preview/placement shortcut that bypasses validation or AutoScaffold/EzTube hooks.
- No terrain-recovery global tick debt intervention; its class remains dormant.
- No benchmark/CSV profiler in the TB Optimizer release; the separate branch `chatgpt/BenchmarkAndOptimizerV11Port` retains the benchmarking research.

**Validation:** CI compilation is required after each modification; GitHub Actions artifacts are build outputs, not proof of in-game behavior. The code reorganizations intentionally preserve individual patch implementation bodies.
