# EBR threading feasibility profiler — v1.1.80 (2026-10-10)

## Goal and scope

This build **does not move any Timberborn/Unity/EBR work to other threads**.
Instead it answers whether the current EBR main-thread cost is dominated by
independent numeric filtering or by generation/application of live access data.

### Instrumentation

- **Candidate selection:** wall time once per original EBR nav-update dispatch,
  including 8x8 spatial bucket and terrain/road-bounds selection. Does not
  include the candidate array rental or any callback work.
- **Intersection:** wall time in the existing exact bounds delegate,
  sampled first and then every 32 calls (both immediate and deferred paths).
- **Callback:** wall time in the original EBR `OnNavMeshUpdated` callback,
  sampled first and then every 32 calls (both dispatch paths). No callback
  is suppressed or reordered.
- **Inner callback stages:** Harmony timing hooks on the real method types
  resolved from EBR's `blockObjectAccessGenerator` and `accessible` fields,
  covering `GenerateAccessBounds(int,int)`,
  `GenerateAccesses(int,int)`, `Accessible.SetAccesses(...)` and
  `ExtendedDemolishableAccessible.UpdateAccesses()`.
  The probes only time calls inside a sampled EBR callback. If a type/method
  cannot be resolved, this is logged and the remaining probes still work.
- All probes retain exact original exceptions and return values.
- One aggregated `EBR.PROFILE:` entry is emitted in `optimizer-v11.log`
  per 15s of elapsed game runtime **only when relevant activity occurred**.
  No per-listener diagnostic logging.

### Reading EBR.PROFILE

- `lookup events / total / avg / max`: **all** EBR update dispatches.
- `intersection sampled=N/32`: N instrumented checks (roughly 1 in 32),
  with **sample-only** aggregate and per-sample mean.
- `callback sampled=N/32`: N instrumented EBR notifications with
  sample-only aggregate / mean / max.
- `rebuild sampled`: sampled callbacks that actually entered UpdateAccesses.
  A callback can return without rebuilding access data.
- `breakdown(sampled) bounds / generate / apply / other~`: stage times
  **only within sampled callbacks**; `other~` is callback minus the summed
  stages, clamped at zero. It may include repeated bounds checks, general
  overhead, or overlapping stage probes. All timings are wall-clock and
  may include GC pauses.
- Do **not** compare a full-population lookup total to sample-only callback
  total directly. Use per-call averages and rates, or estimate using the
  observed number of calls.

### Safety and compatibility

- All gameplay callbacks and exact bounds checks execute normally.
- Existing EBR 256 listener / 8ms deferred budget is unchanged.
- No save serialization, global tick scheduling, Soil Contamination changes,
  workers, caches, or reachability result changes.
- Timing hooks are observational; the cost of the profiler itself is not zero,
  especially on first startup during Harmony patch generation.
- The `UpdateAccesses` hook can count calls triggered inside EBR sampled
  callbacks only, not all possible non-navigation initialization calls.

### Suggested single-run validation

Load a game with many EBR listeners. Trigger a small path change, a large
navmesh change, building accessibility/demolition, and a period of regular
simulation. Run at speed x1 and optionally x3. Send both
`optimizer-v11.log` and `optimizer-v11-freezes.log`.
The logs will distinguish whether off-thread numeric filtering is a
material opportunity or access generation/application dominates.

### Limitations

- Generation / SetAccesses use game state; they must not be moved to workers
  merely because the numeric filtering is worker-safe.
- A sampled result is an estimate, especially with rare events.
- If probes report 0 installed for a stage, that stage is not attributed.
