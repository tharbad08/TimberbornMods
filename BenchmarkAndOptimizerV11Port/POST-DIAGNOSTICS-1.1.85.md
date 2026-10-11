# v1.1.85 — post-diagnostics normal-play build (2026-10-11)

User-reported FPS degradation during the diagnostic v1.1.84 run led to the
post-diagnostics build. Source and save behavior unchanged.

## Evidence obtained: no further high-frequency preview profiling required

In the first **899-preview** frame (2026-10-11 04:23:09):

| Scoped operation | Wall time |
| --- | ---: |
| Preview population | 548.3 ms |
| TemplateInstantiator.Instantiate | 539.8 ms |
| BaseInstantiator.InstantiateInactive | 253.7 ms |
| T3MP.Activate, inclusive of Unity SetActive/lifecycle | 277.2 ms |
| Approx. template other = 539.8 - 253.7 - 277.2 | 8.9 ms |

T3MP.Prepare ran **0** times. The bulk fast path executed **899**
times within the preview frame, reused **8,990** cached immutable specs,
and still constructed **80,011** live components through vanilla code.
No blocker in bulk construction was found.

Other important findings:

- At 04:24 multiple subsequent preview frames cost 100–186 ms to populate
  existing previews, and about 90–180 ms in ShowBuildable. These are
  **repeated per input frame**, not one-time initial creation.
- NavMesh/NavigationSynchronizer work added tens to ~200 ms in related
  preview/placement frames, and EBR's deferred queue grew to >70k entries.
- Placing many buildings cost ~595–1079 ms in
  ConstructionFactory/EntityService, overlapping Unity/preview work.
- A save callback took 5.34 s; the logged callback does not split save
  serialization and compression. Save implementation is deliberately unchanged.
- Gen2 GC freezes of 2.8 s and 4.6 s occurred in the run; heap pressure
  is a meaningful background factor. No new GC workaround.
- These data do **not** prove the profiling hooks alone caused the FPS drop.
  No controlled A/B run was conducted; user-reported degradation should
  nevertheless be acted on conservatively.

## Reduced overhead (actual code changes)

1. **Stop installing EbrWorkProfiler's Harmony prefix/finalizer probes** on
   GenerateAccessBounds, GenerateAccesses, SetAccesses, UpdateAccesses.
   Existing EBR optimizer listener delegates still call the profiler helper,
   but its disabled fast path immediately calls the original function without
   timing or aggregation; EBR candidate filtering and 256/8ms drain unchanged.
2. **Stop installing TemplateActivationProfiler's Harmony hooks** on
   T3MP.PreparedEntityVisuals.Activate and Prepare. Keep source for targeted
   re-enable in a diagnostic branch if ever needed.
3. Remove the three new per-batch preview `FreezeDetector.RecordHotspotCount`
   calls added in v1.1.84. The warmed-spec creation fast path remains active,
   including all live component instantiation and Harmony postfix behavior.
4. Retain existing freeze summaries and broader methods so meaningful performance
   regressions can still be investigated without restarting just for diagnostics.

No changes to save serialization, soil/contamination, global tick scheduling,
EBR callbacks, initialization, AutoScaffold, EzTube, SmartPower, preview
validation, or any other gameplay algorithm.

## Further optimization direction

The major remaining steady interaction costs are **ShowBuildable** and
**repositioning/validation of already-created previews on repeated input frames**,
not decorator init or coordinate intersection. Any caching needs exact input
and world/nav version invalidation plus third-party preview callback preservation.
No unsafe skip was introduced in this performance-restoration build.
