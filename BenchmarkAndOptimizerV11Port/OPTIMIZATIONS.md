# TB Optimizer — retained optimizations and source map

> Timberborn 1.1; applies to the \`TB-Optimizer\` branch. This is a **source inventory**, not a claim that every feature has been independently benchmarked or play-tested after the branch separation. See [FAILED_EXPERIMENTS.md](FAILED_EXPERIMENTS.md) before proposing a new optimization.

**Organization:** each feature is in its own source file under \`Optimizations/Mods/\` or \`Optimizations/BaseGame/\`. Paths in the tables are relative to this project's directory. Click a path to see its implementation.

**Registration and settings:**

- [\`ModStarter.cs\`](ModStarter.cs) is the **only registration point**. It starts the Harmony patches corresponding to enabled feature keys; this build has **no global tick dispatcher throttling, CSV benchmark, or freeze profiler**.
- [\`OptimizationToggles.cs\`](OptimizationToggles.cs) declares all 15 switches, their short descriptions, and the on-disk \`tb-optimizer-options.json\` values. Missing keys default to **enabled**.
- [\`SettingsIntegration.cs\`](SettingsIntegration.cs) adds the TB Optimizer Mod Settings panel and one checkbox plus explanation for each feature.
- **Changing a checkbox requires a game restart**; toggles are evaluated when the mod starts, and disabling a checkbox does not automatically unpatch running Harmony methods.
- If a target method/type does not match the expected version, individual patches generally skip installing rather than guessing. **Build success only verifies compilation.** Read this inventory and confirm runtime behavior with each relevant mod.

## Third-party mods — active feature registrations

| Mod / key | File and targeted work | Behavior / important invariants |
| --- | --- | --- |
| **BFR** (\`BFR\`) | [\`BfrLocalizedChangeOptimizerPatcher.cs\`](Optimizations/Mods/BFR/BfrLocalizedChangeOptimizerPatcher.cs) | Patches \`Calloatti.BeaversForReal.BFRManager.ProcessLocalizedChange\`; partitions sparse coordinate updates into **8×8 buckets** instead of processing a large bounding area; retains native fallback and finalizer on failure. |
| **Extended Builder Reach** (\`EBR\`) | [\`ExtendedBuilderReachNavOptimizer.cs\`](Optimizations/Mods/ExtendedBuilderReach/ExtendedBuilderReachNavOptimizer.cs) | Intercepts \`NavMeshListenerEntityRegistry\` registration/unregistration/notification for \`ExtendedDemolishableAccessible\`. Tracks **8×8 spatial buckets**, resolves exact update bounds, filters candidate listeners, and calls the original EBR \`OnNavMeshUpdated\` callback. Large updates (≥1,024 candidates) are queued with pooled arrays; drains at most **256 candidates or 8 ms per frame**. Preserve listener activation, exact intersection checks, and FIFO; delayed delivery is intentional, not identical same-frame timing. |
| **Extended Builder Reach** (\`EBR\`, required companion) | [\`EbrDeferredDrainHook.cs\`](Optimizations/Mods/ExtendedBuilderReach/EbrDeferredDrainHook.cs) | Attaches to \`SingletonLifecycleService.UpdateSingletons\` to drain queued EBR notifications. \`ModStarter.cs\` **will not install EBR** if this frame hook cannot attach—without draining, listeners could be starved. |
| **Keystone** (\`FaunaBudget\`) | [\`FaunaSpawnBudgetPatcher.cs\`](Optimizations/Mods/Keystone/FaunaSpawnBudgetPatcher.cs) | Patches \`FaunaSpawnDrainer.UpdateSingleton/Spawn\`; budgets **one actual successful spawn per update** and uses the original pending/retry semantics for deferred requests. Avoid deleting pending fauna. |
| **Keystone** (\`FaunaCache\`) | [\`FaunaRecipeLookupCachePatcher.cs\`](Optimizations/Mods/Keystone/FaunaRecipeLookupCachePatcher.cs) | Caches successful \`FindRecipeForBlueprint(string)\` lookups per drainer using weak instance keys. First lookup, nulls, and incompatible signatures retain native behavior. |
| **Keystone** (\`FaunaPrewarm\`) | [\`KeystoneFaunaTemplatePrewarmer.cs\`](Optimizations/Mods/Keystone/KeystoneFaunaTemplatePrewarmer.cs) | Prewarms **KeystoneCow, KeystoneBull, KeystoneDeer** through the game's *own* \`TemplateInstantiator.CacheInstance\` after game startup / \`ShowPrimaryUI\`, **not** during early \`TemplateCollectionService.Load\`. Does not create a second prefab cache. |
| **SuperCursor** (\`SuperCursor\`) | [\`SuperCursorRefreshSmoother.cs\`](Optimizations/Mods/SuperCursor/SuperCursorRefreshSmoother.cs) | Limits \`SuperCursorTool.ProcessInfo\` (informational updates only) to **20 Hz** normally; adaptive **10/4 Hz** backoff if previous refreshes were slow. Tool input/click handling is outside this throttle. |
| **Bober's Laws of Motion / TimberPhysics** (\`PhysicsCatchup\`) | [\`TimberPhysicsCatchUpLimiterPatcher.cs\`](Optimizations/Mods/TimberPhysics/TimberPhysicsCatchUpLimiterPatcher.cs) | Limits physics catch-up to **four 0.02s substeps** with roughly **50 ms wall-clock work budget** for expensive physics steps. **Excess backlog is dropped** under overload: this intentionally trades simulation catch-up for responsiveness, not exact numerical equivalence. |
| **Bober's Laws of Motion / TimberPhysics** (\`PhysicsCollider\`) | [\`TimberPhysicsTerrainColliderMergerPatcher.cs\`](Optimizations/Mods/TimberPhysics/TimberPhysicsTerrainColliderMergerPatcher.cs) | Merges only conservative **equal floor/ceiling** terrain collider columns in **32×32 chunks**; tracks dirty chunks, batches rebuilds before simulation, and attempts transactional restoration of complete vanilla colliders on failure. Never leave partially merged + vanilla collider sets. |

**PR ownership:** copy each mod-specific patch, its entry point and safety constraints to the **corresponding mod repository**. Do not copy the TB Optimizer's unrelated gameplay patches into another mod. The EBR optimizer needs both EBR source files.

## Base game / common services — active feature registrations

| Key | File and targeted work | Behavior / important invariants |
| --- | --- | --- |
| \`PreviewNav\` | [\`PreviewNavMeshBatcher.cs\`](Optimizations/BaseGame/Previews/PreviewNavMeshBatcher.cs) | Replaces immediate \`NavMeshObject.AddToPreviewNavMesh/RemoveFromPreviewNavMesh\` with game-native enqueue methods so \`NavigationSynchronizer\` can apply combined changes. No global navmesh tick suppression. |
| \`PreviewPool\` | [\`UnusedPreviewPoolFastPath.cs\`](Optimizations/BaseGame/Previews/UnusedPreviewPoolFastPath.cs) | Avoids reading a \`Lazy<Preview[]>\` when hiding/removing **a brush whose previews have never materialized**. Preserves hooks/postfixes on \`HideAllPreviews\` and must use native behavior for an already-instantiated pool. No reduction of available brush previews. |
| \`PreviewSweep\` | [\`FullyBuildablePreviewFastPath.cs\`](Optimizations/BaseGame/Previews/FullyBuildablePreviewFastPath.cs) | Replaces the allocation-heavy \`PreviewPlacer.HideRemainingPreviews\` \`Except\`/HashSet sweep **only after checking ordered-subset invariants**; calls the original \`HideAndRemoveFromServices\` helper per invalid preview. No placement validation bypass. |
| \`BlueprintCache\` | [\`BlueprintSpecInstantiationCache.cs\`](Optimizations/BaseGame/Previews/BlueprintSpecInstantiationCache.cs) | Weak-key memoization of **immutable \`ComponentSpec\`** lookup results for \`Blueprint.GetSpec\` / \`BaseInstantiator.InstantiateComponent\`. Avoids caching live mutable components and backs out when the IL/signature or external Harmony ownership is incompatible. |
| \`BulkBuild\` | [\`BulkComponentInstantiationV11.cs\`](Optimizations/BaseGame/Previews/BulkComponentInstantiationV11.cs) | An audited, warmed-spec fast path for Timberborn 1.1's **four-argument** \`BaseInstantiator.InstantiateComponents\`. Preserves initializer ordering, native construction of live components, T3MP's allowed early prefix and SmartPower/other postfixes; fails closed if live IL or Harmony ownership is unexpected. Used also by large preview brushes, **not** a shortcut around AutoScaffold/EzTube or placement rules. |
| \`SoilReset\` | [\`SoilContaminationResetOptimizer.cs\`](Optimizations/BaseGame/Soil/SoilContaminationResetOptimizer.cs) | Specializes \`SoilContaminationService.Reset\`: transitions only contaminated objects, clears backing levels and resets the contamination map once after checking expected layout. **Normal contamination/moisture updates and planting controls remain at vanilla tick cadence.** |

## GC source present, but no active slice scheduling

| Key | File | Current verified wiring |
| --- | --- | --- |
| \`GcSmoother\` | [\`IncrementalGcSmoother.cs\`](Optimizations/BaseGame/GC/IncrementalGcSmoother.cs) | \`ModStarter.cs\` calls **\`Initialize()\` only**. The file contains a conditional \`Pulse(heapBytes)\` implementation with bounded incremental-GC requests above heap thresholds, but **nothing in this TB-Optimizer release calls \`Pulse\`**. Thus this switch currently **does not deliver an active incremental GC optimization**; if Unity reports incremental GC unavailable, it would decline to collect anyway. Do not describe it as proven to reduce Gen2 pauses. GC research belongs to [\`chatgpt/TBGC\`](https://github.com/tharbad08/TimberbornMods/tree/chatgpt/TBGC). |

## Boundaries / what TB Optimizer deliberately does *not* do

- Does **not** throttle \`TickableBucketService\`, \`SoilContaminationService\`, \`PlantingControler\`, \`PlantMonitor\`, navigation, water, speed, physics, or other authoritative schedulers. Historical tick interval/benchmark features are owned by [\`BenchmarkAndOptimizerV11Port-Only\`](https://github.com/tharbad08/TimberbornMods/tree/BenchmarkAndOptimizerV11Port-Only).
- Does **not** install the old freeze/CSV profiler or expensive per-entity/PlayerLoop/soil input diagnostics. \`ModStarter.cs\` contains **conditional no-op diagnostic shims** solely to keep migrated source self-contained.
- Does **not** patch save serialization/ZIP, loading phases or broad assembly discovery; research resides on [\`chatgpt/TBSave\`](https://github.com/tharbad08/TimberbornMods/tree/chatgpt/TBSave).
- Does **not** install the Bindito/\`SpecTypeCache\` dynamic assembly startup compatibility guard. The separate guard source is preserved on \`chatgpt/TBSave\`; it is a startup crash fix, not a save optimization.
- Does **not** skip \`Accessible.SetAccesses\`, move live EBR callbacks to workers, cap preview counts or bypass AutoScaffold/EzTube/T3MP/SmartPower. See the rejected approaches record.
- Does **not** try speculative render-camera or animation-only shortcuts.

## Before porting any fix upstream

1. Read [\`FAILED_EXPERIMENTS.md\`](FAILED_EXPERIMENTS.md). In particular, the older terrain-collider implementation could crash during demolition, and earlier tick throttling stalled planting; **do not revive the broken predecessors**.
2. Review current method signatures and other Harmony owners in the target repository. These patches used **Timberborn v1.1** runtime-specific guard checks.
3. Test the feature in isolation, with its required mod installed. Exercise small changes, large brushes, post-contamination planting, terrain edits/directional dynamite, and load/save as applicable.
4. Separate **compilation**, **installed patch**, **equivalent gameplay semantics**, and **measured FPS/game-speed gain**; no one implies the others.

**Historical evidence:** [original development notes and diagnostic sources](https://github.com/tharbad08/TimberbornMods/tree/chatgpt/BenchmarkAndOptimizerV11Port/BenchmarkAndOptimizerV11Port). The branch's legacy source contains experiments, including unsafe/dormant paths; do not ship it as a replacement for the maintained \`TB-Optimizer\` source.
