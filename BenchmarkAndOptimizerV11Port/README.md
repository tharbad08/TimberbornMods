# Benchmark & Optimizer — Timberborn v1.1 port

This port preserves the original per-system frequency control while avoiding U7-only private layout dependencies where possible.

- `optimizer-v11.json` is generated on first launch.
- Interval `1` = vanilla frequency.
- Interval `20` = run once, then skip the next 19 dispatcher calls.
- The config is reloaded live (default every 2 seconds).
- `optimizer-v11-discovered.txt` lists observed tick/update types to make configuration easier.
- Set `BenchmarkSeconds` above 0 to write a CSV benchmark for that many real seconds after game load.
- Compatibility failures fall back to vanilla dispatcher behavior instead of intentionally suppressing game updates.

The runtime package requires the normal Timberborn Harmony mod.

- Bober's Laws of Motion / TimberPhysics: caps physics catch-up to 4 fixed substeps per update. Normal 0.02 s physics cadence is unchanged when the game keeps up; excess backlog is dropped to prevent runaway catch-up stalls.

- TimberPhysics terrain experiment: replaces per-column terrain BoxColliders with conservative greedy merges inside 16x16 chunks. Only cells with exactly matching floor/ceiling intervals are merged; terrain edits rebuild only the affected chunk.

- v1.1.27: hardens the TimberPhysics terrain-merger reflection resolver for the actual v1.1.1.0 DLL layout and logs the exact unresolved member if installation still fails.

- v1.1.28: adds an always-on freeze detector. Frames >=250 ms are logged to optimizer-v11-freezes.log with dispatcher timings, TimberPhysics time, GC deltas, unattributed time, and benchmark top systems when available.

- v1.1.29: fixes TimberPhysics terrain column Floor/Ceiling resolution when those members are exposed through interfaces or explicit interface implementations.

- v1.1.30: terrain merger now resolves ColumnTerrainMap.GetColumn(int) explicitly and logs the runtime terrain-column type/members if Floor/Ceiling still cannot be resolved.

- v1.1.31: clears optimizer/freeze logs at each game process start; shows inherited/nested throttle ownership in the UI and log; safely patches shared inherited tick methods once while keeping per-concrete-type intervals; unwraps by-ref TerrainColumn returns so terrain collider merging can resolve Floor/Ceiling.

- v1.1.32: normalize inherited Harmony targets to the actually declared base method; safely dereference by-ref ColumnTerrainMap.GetColumn returns; disable terrain merger after the first fatal build failure to avoid per-cell warning floods and restore vanilla colliders.

- v1.1.33: makes TimberPhysics terrain-collider runtime fallback transactional. A failed chunk rebuild/removal now restores the complete vanilla collider set instead of mixing vanilla and merged chunks; stale vanilla dictionary entries and partial merged colliders are cleaned up to prevent duplicate-key crashes during terrain edits such as directional dynamite.

- v1.1.34: same runtime-member rebinding terrain fix that was briefly mislabeled as a second v1.1.33 build; version corrected to keep artifacts unambiguous.

- v1.1.35: adds always-on low-overhead ITickableSingleton timing for freeze attribution. Slow/frozen frame logs now include the top TickSingleton offenders even when the full benchmark is not running.

- v1.1.36: adds frame phase-gap timing for unattributed stalls and a targeted SoilContaminationService deep profiler with per-callee timing and GC deltas.

- v1.1.37: removes intrusive SoilContamination inner-method Harmony hooks that caused frequent stutter; keeps phase-gap diagnostics, TickSingleton attribution, and lightweight outer Soil Tick GC tracking.

- v1.1.38: adds low-overhead always-on attribution for UpdateSingleton/LateUpdateSingleton implementations and outer callers of TickableEntity.TickTickableComponents, keeping phase-gap and TickSingleton diagnostics without hot inner-method hooks.

- v1.1.39: batches TickableEntity timing once per frame instead of locking per entity, and records only individual entity ticks >=20ms with their tickable component type set for targeted freeze attribution.

- v1.1.40: batches merged TimberPhysics terrain-collider rebuilds by dirty 16x16 chunk and flushes them once immediately before the next physics step, preventing repeated same-chunk rebuilds during dynamite/terrain-edit bursts.

- v1.1.41: bundles sparse BFR localized-update optimization, low-frequency deep component sampling for slow TickableEntity instances, and NavigationSynchronizer sub-method attribution. Keeps the v1.1.40 batched terrain collider rebuilds.

- v1.1.42: fixes the slow-entity component sampler so it is installed during normal play, and adds coarse timing for singleton/entity navmesh listener registries behind NavigationSynchronizer.NotifyAllNavmeshChanges. Keeps BFR sparse-update optimization and terrain batching unchanged.

- v1.1.43: bundles targeted Keystone Tick allocation/GC attribution, adaptive per-entity navmesh-listener profiling for expensive NotifyAll passes, and lightweight major Unity PlayerLoop phase markers. Diagnostic-only; no new throttling or simulation behavior changes.

- v1.1.44: optimizes Extended Builder Reach navmesh notifications by separating ExtendedDemolishableAccessible listeners from the generic registry and fast-filtering by BoundingBox before preserving EBR's normal callback; removes the resolved Keystone allocation profiler; adds child-level PreLateUpdate PlayerLoop timing and targeted InputService/FaunaSpawnDrainer internal timing. No new cadence throttles.

- v1.1.45: fixes the EBR fast-dispatch value-type IL bug so BoundingBox filtering can activate; adds per-input-processor timing, managed MonoBehaviour LateUpdate attribution, FaunaSpawnDrainer inner Spawn timing around EntityService.Instantiate/configuration/registry add, and SoilMoisture outer-Tick allocation/GC attribution. Removes the unresolved InputUpdater probe. No new cadence throttles.

- v1.1.46: performance-focused cleanup. Removes resolved per-entity/input/LateUpdate/fauna-inner/SoilMoisture/PlayerLoop profilers; replaces EBR full-list bounds scans with 8x8 spatial buckets keyed by actual terrain coordinates (road updates keep conservative fallback); limits Keystone fauna to one actual spawn instantiation per update while re-queuing deferred deficit; and adds a 2-second post-terrain recovery limiter that caps TickableBucketService work at 8 buckets per call and drains deferred bucket debt at +2/update without dropping ticks.


- v1.1.47: targets the remaining freeze classes from the 2026-10-04 run in one restart. Adds a semantic-preserving SoilContamination reset fast-path (only contaminated objects are transitioned, the backing array is cleared, then the material map is reset once), outer Soil allocation/GC plus UpdateContaminationLevels timing, a Keystone Fauna blueprint-to-recipe cache, high-level Input/Fauna timing, and major-only Unity PlayerLoop phase markers for unattributed stalls. PreLateUpdate child wrapping and per-input-processor hooks remain disabled.


- v1.1.48: bundled follow-up from the 2026-10-04 17:00 run. Fixes terrain-recovery debt starvation by allowing debt-only TickBuckets calls, using backlog-pressure recovery budgets (8/16/24), and bounded post-recovery repayment budgets (16/32/64/96). Adds exact input-processor attribution, PreLateUpdate child timing, Fauna Spawn sub-step timing, EbbAndFlowManager inner-method timing, and whole-frame allocation/heap reporting. Soil normal-update semantics are intentionally unchanged because each multi-second Soil event in the source run coincided with a full GC while Soil itself allocated 0 KiB.


- v1.1.49: fixes startup failure in the v1.1.48 EbbAndFlow diagnostic Harmony finalizers by using Harmony's required __exception parameter name. No simulation logic changes beyond v1.1.48.


- v1.1.50: focused follow-up from the long 2026-10-04 run. Keeps adaptive terrain-debt repayment; adds bounded Unity incremental-GC slices only above 7GiB managed heap (no forced full GC), a UI-only SuperCursor info refresh smoother, focused BlockObject/LevelVisibility/SuperCursor inner timing, deeper fauna EntityService/TemplateInstantiator/BaseInstantiator timing, and an 8GiB-gated Timberborn ILateUpdatableComponent profiler to identify ScriptRunBehaviourLateUpdate freezes. Broad input, EbbAndFlow, old fauna-instantiation and broad MonoBehaviour profilers are disabled.


- v1.1.51: long-run follow-up. Replaces immediate BlockObject preview-navmesh apply/remove with queued batching through NavigationSynchronizer; adds continuous-terrain-edit anti-starvation so deferred tick debt can ramp to 32/48/64/96 instead of growing without bound; removes the unavailable incremental-GC helper and non-actionable armed late-component profiler; adds sampled per-singleton allocation diagnostics above 6GiB heap; adds LevelVisibility event-handler timing and deeper Keystone fauna cache-miss profiling through OptimizedPrefabInstantiator/PrefabOptimizationChain/BlueprintPrefabConverter.


- v1.1.52: validation follow-up. Keeps preview-navmesh batching, adds per-preview-service member timing to isolate remaining RemoveServices spikes, adds BuildingPlacer/ConstructionFactory/BlockObjectFactory/EntityService/TemplateInstantiator placement timing, tags Keystone fauna cache-miss profiling with the blueprint name when available, changes terrain recovery to a debt-pressure 32/48/64/96 ramp above 4096 deferred buckets, replaces the unsupported per-thread allocation sampler with sampled managed-heap deltas across singleton and PlayerLoop scopes, and records TimberPhysics StepAll versus PhysX simulation time while retaining the 4-substep cap.


- v1.1.53: v1.1.52 validation follow-up. Retains preview-navmesh batching and terrain debt-pressure recovery. Expands freeze-log input detail from 8 to 20 entries so preview-service and placement-factory subprobes are not hidden by the outer BlockObjectTool call stack. TimberPhysics keeps up to four 0.02s substeps when cheap but stops catch-up once the current update has spent about 50ms inside Physics.Simulate, then drops remaining backlog as before. Managed-heap sampling now adds nested SoilContamination.UpdateLevels and SoilMoisture.UpdateLevels scopes to separate their inner update work from outer resize/background allocation effects.


- v1.1.54: diagnostic follow-up to v1.1.53. Keeps the same optimizer behavior while adding exact current-thread allocation and TerrainMaterialMap queue-growth diagnostics for SoilContaminationService.UpdateContaminationLevels and SoilMoistureService.UpdateMoistureLevels; fixes the missing SoilMoisture profiler installation; times actual IInputProcessor/IPriorityInputProcessor implementations during InputService dispatch to identify opaque input stalls; and adds nested EbbAndFlowManager.Tick timing.


- v1.1.55: follow-up to the long v1.1.54 run. Exact thread-allocation probes proved SoilContaminationService and SoilMoistureService allocate 0 KiB even during the recurring full-GC pauses, so soil behavior is unchanged. Adds exact allocation attribution across Tick/Update/LateUpdate singletons plus PlayerLoop segments to identify the real allocator. Deepens BlockObject preview first-use profiling into TemplateInstantiator, PrefabOptimizationChain, BlueprintPrefabConverter and BaseInstantiator. Increases the exact-shape TimberPhysics terrain merge chunk from 16x16 to 32x32 to reduce collider fragmentation while preserving floor/ceiling geometry.
