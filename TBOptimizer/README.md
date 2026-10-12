# TB Optimizer (Timberborn v1.1)

Independent gameplay-optimization mod, assembled from the measured fixes
developed in the old BenchmarkAndOptimizerV11Port project.

## Installation and identity

- Mod ID: `TBOptimizer`; assembly: `TBOptimizer.dll`.
- Install exactly one `TBOptimizer` mod directory, not the old
  `BenchmarkAndOptimizerV11` artifact simultaneously.
- `BenchmarkAndOptimizerV11Port` is now a **separate** CSV-profiler mod.
- `TBEBROptimizations` is now a **separate** EBR spatial-navigation mod;
  TB Optimizer does NOT install those EBR patches.

## Settings

The `TB Optimizer` section in ModSettings exposes one startup checkbox
per optimization, **all enabled by default**. Selections persist in
`tb-optimizer-options.json` inside the mod directory and take effect
after restarting the game.

Names are all shorter than 40 characters; descriptions are deliberately
one or two short lines. The advanced interval-list scrolls vertically
only, with long runtime type names wrapped to avoid horizontal overflow.

Optimization keys:
`BFR`, `PreviewNav`, `PreviewPool`, `PreviewSweep`,
`BlueprintCache`, `BulkBuild`, `FaunaBudget`, `FaunaCache`,
`FaunaPrewarm`, `SoilReset`, `SuperCursor`, `PhysicsCatchup`,
`PhysicsCollider`, `DynamicAssembly`, `GcSmoother`, `Intervals`.

## Areas

- Beavers For Real: sparse 8x8 local batching.
- Timberborn preview/navigation: notification batching, lazy preview-pool
  guard, allocation-free ordered preview sweep.
- Timberborn construction/blueprints: warmed immutable ComponentSpec cache
  and safe bulk initialization.
- Keystone: bounded fauna spawns, recipe cache, template prewarming.
- SoilContamination: reset cleanup without global tick throttling.
- SuperCursor: informational UI refresh backoff.
- TimberPhysics: bounded physics catchup, merged terrain colliders.
- Bindito / Blueprint startup: unfinished dynamic assembly guard.
- GC: conditional smoothing only if Unity enables incremental GC.
- Advanced optimizer interval controls: critical simulation tick protections.

## Safety

No saving serialization rewrites, global TickBuckets throttling, camera
mask experiment, or active freeze/profiling/CSV logging. TimberPhysics
catchup limiting can deliberately drop excessive backlogged physics
steps and is separately disableable.

The independent artifacts compiled in GitHub Actions. In-game mod
coexistence, styling and navigation behavior need user-side validation.
