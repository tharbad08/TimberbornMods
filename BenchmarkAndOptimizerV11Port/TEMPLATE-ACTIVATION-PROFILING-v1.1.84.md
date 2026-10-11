# Template activation vs initializer profiling (v1.1.84)

## Goal

The v1.1.83 log captured three ~899-preview brushes with average
TemplateInstantiator.Instantiate ~566ms and
BaseInstantiator.InstantiateInactive ~266ms, leaving roughly 300ms outside
BaseInstantiator. The 300ms is a subtraction of inclusive times, not proof
that a particular method is at fault.

## Source inspection

- Historical decompiled Timberborn TemplateInstantiator.Instantiate performs:
  GetCachedTemplate, BaseInstantiator.InstantiateInactive, loops through
  cached decorator initializer delegates, then calls GameObject.SetActive(true).
- Installed T3MP entity-visuals source explicitly rewrites the one SetActive call
  inside TemplateInstantiator.Instantiate to
  T3MP.Loading.PreparedEntityVisuals.Activate(root, active).
  Its Activate optionally runs Prepare during loading and then calls the
  original GameObject.SetActive. During normal gameplay _depth == 0, so the
  wrapper generally just forwards to SetActive. This is an inference based
  on T3MP source; the running binary's hook is validated by observed counters.
- Historical methods do not prove that current Timberborn 1.1 has identical
  behavior. This build does not replace or transpile any gameplay method.

## Instrumentation

- New Harmony prefix/finalizer around the installed T3MP.Activate method,
  counting elapsed ticks only during BlockObjectTool input.
- Separately times T3MP.Prepare if invoked in that scope.
- Existing block timings still include TemplateInstantiator.Instantiate,
  BaseInstantiator.InstantiateInactive, GetCachedTemplate and ComponentCache.Initialize.
- Freezes now can contain Block.TemplateActivation.Activate and
  Block.TemplateActivation.Prepare (inclusive).
- Every 899 instrumented activations (cumulative), the normal optimizer log
  emits TEMPLATE.ACTIVATION with total, average and max call times, and
  Prepare time. No per-preview log lines.
- The warm immutable-spec fast path now reports, in freeze frames,
  Block.BulkFastPathBatches, Block.BulkCachedImmutableSpecs and
  Block.BulkNativeLiveComponents. This distinguishes preview hits from the
  previous world-load-only threshold counters.

## Interpretation

For a single large-preview frame:
- template = Block.TemplateInstantiator.Instantiate
- base = Block.BaseInstantiator.InstantiateInactive
- activation = Block.TemplateActivation.Activate
- cached = Block.TemplateInstantiator.GetCachedTemplate
- approximate other/initializer = max(0, template - base - activation - cached)
The activation timer includes any T3MP Prepare plus Unity's native
SetActive and Awake/OnEnable effects; native engine activation and callbacks
are not separately intercepted. Method timing overhead can slightly distort
the difference. Do not sum ComponentCache.Initialize because it is nested in
BaseInstantiator and would double count.

## Safety

This is an **observational** change with one extra source-verified preview
count and two timing hooks. It cannot skip initializers, SetActive, native
Awake/OnEnable, validation, AutoScaffold, EzTube or SmartPower, and it does
not alter creation order, save serialization, Soil Contamination, GC, physics,
EBR budgeting or global ticks.

We deliberately did not attempt the unsafe option of deferring, pooling,
or bypassing activated preview GameObjects. If activation is confirmed as the
cost center, reducing it requires identifying specific lifecycle work that
can safely be avoided, or verifying a fully equivalent alternative; merely
staggering 899 previews could break placement visualization or validation.

## Single game run

Load the large colony; show a ~899-preview brush at least once, then hide,
re-show, rotate, and place several entries. Confirm the startup line
'template activation stage profiler: T3MP.Activate=True' and inspect both
optimizer-v11.log and optimizer-v11-freezes.log for
Block.TemplateActivation.Activate, TEMPLATE.ACTIVATION, and
Block.BulkFastPathBatches. Do not assume activation costs zero if the hook
is unavailable; the report will say so.
