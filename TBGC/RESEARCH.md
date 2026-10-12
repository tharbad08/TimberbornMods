# TBGC — garbage collection and allocation research archive

## Observations from optimizer logs
- Very large managed heaps: earlier snapshots roughly 7.9 GB to 12 GB; heap dropped substantially around observed Gen2 collection.
- Multi-second pauses coincided with Gen2 collections and player-loop phases. Sample freeze 2746.9 ms, heapBeforeFrame ~12072 MB, heapAfterFrame ~7734 MB.
- Unity reported incremental GC disabled on the tested runtime, so the existing incremental-GC smoother generally deferred to vanilla behavior rather than enabling unsupported behavior.
- Saving and preview/bulk construction contributed allocation spikes; not all observed memory growth is a leak.
- Earlier instrumentation included managed-heap sampler, per-thread allocation tracking, freeze correlation, and GC generation counters; retired for quiet release.

## Ideas / not implemented
- Reduce allocations at known source hot loops, rather than forcing GC timing.
- Measure retained references after repeated loads before labeling persistent heap growth a leak.
- Examine large temporary serialization buffers and object pools; beware high RAM cost.
- A broad manual collection or moving Unity allocations across threads is not a validated fix.

## Status
Research archive; no active GC behavior changes are introduced on this branch. Keep these notes separate from performance claims requiring new controlled logs.

## Quantitative observations

In one later 3x-speed UI-only rendering experiment on October 12:
- A Gen2 transition accompanied a ~2,746.9 ms frozen frame.
- Reported managed heap before frame: ~12,072.4 MB; after frame:
  ~7,734.1 MB. GC delta [gen0:1, gen1:1, gen2:1].
- The long unattributed frame gap was ~2,683.6 ms, whereas measured
  `PhysicsSimulator` was only ~55.6 ms in that sample.
- The Gen2 transition occurred in
  `PreLateUpdate.ScriptRunBehaviourLateUpdate` according to
  PlayerLoop observations.
- Another save-callback frame ran ~8,870.8 ms, including an
  ~8,375.5 ms `GameSaverUnityAdapter` callback. Its reported heap changed
  ~11,852.8 MB → ~9,044.7 MB with a Gen2 collection.
- Earlier log sequences showed heap growth roughly from 7.9 GB to 12 GB
  during several minutes, including some tests with the game paused.

These figures describe individual samples and **cannot by themselves
establish a memory leak**. The snapshots use live managed-heap estimates,
not full accounting of native Unity memory or VRAM.

## Instrumentation we tried
- `ManagedHeapSampler` and `AllocationTracker` to estimate managed
  heap growth and sampled per-thread allocation metrics.
- Gen0/Gen1/Gen2 `GC.CollectionCount` differences around slow frames.
- PlayerLoop phase and lifecycle adapter timing to correlate pauses with
  GC/serialization.
- One-off allocation and heap snapshots during high-heap save callbacks.
- `IncrementalGcSmoother` with narrow bounded cooperation: no-op / vanilla
  fallback when Unity incremental GC was unavailable.
- Rate-limited heap/GC logs (10-second minimum) and suppression of
  low-value repeated fallback "allocation spike" claims.

The 1.1.101 quiet release disabled the active profiler instrumentation.
TB Optimizer retained its configurable GC smoother, but if Unity reports
incremental collection unavailable, **no GC strategy change occurs**.

## Ideas worth testing in a standalone TBGC project

1. Profile allocation sources in heavy preview construction (899 preview
   pool case), template activation, navigation dispatch, and save creation;
   optimize object churn rather than calling collection more frequently.
2. Verify whether repeated world loads create retained references;
   inspect weak/strong caches and mod static collections with a managed
   snapshot tool. No leak should be claimed until retention is measured.
3. Cache only immutable / correctly invalidated data. Prefer pooling for
   short-lived helper arrays when bounded. Do not pool large gameplay
   entities without lifetime correctness proofs.
4. Distinguish managed heap from Unity native memory, terrain colliders,
   graphics buffers and third-party mod allocations. Compare GC spikes
   to total process RSS.
5. Profile `GC.GetAllocatedBytesForCurrentThread` availability and baseline
   overhead on the same Unity Mono runtime; earlier fallback to
   `GC.GetTotalMemory(false)` is a heap estimate, not per-call allocation.
6. Test staged serialization on its own research branch; a detached
   save snapshot could *increase* peak RAM substantially.

## Safety / reject list
- Do not force full `GC.Collect()` each tick or frame.
- Do not assume incremental GC can be enabled via a mod when Unity's
  runtime explicitly reports it disabled.
- Never move Unity object creation/destruction or scene traversal to
  worker threads just to reduce apparent GC pressure.
- Do not simultaneously alter global tick scheduling, soil-contamination
  reset semantics, and GC pacing; these confounded earlier analysis.
- Keep GC investigations separate from verified EBR/BFR/physics fixes.

## Source logs (names, not uploaded raw binaries)
- `optimizer-v11(20261011-195527).log`
- `optimizer-v11 (2).log`
- `optimizer-v11(20261011-225839).log`
- Matching `optimizer-v11-freezes` logs from the October 7–12 testing.
- `Player` logs whenever a Unity crash or post-load initialization
  error changed the environment.

Raw logs remain attached to the ChatGPT Timberborn project, not
committed to this research-only branch.
