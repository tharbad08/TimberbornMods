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
