# EBR Optimizations

Target: Extended Builder Reach (`ExtendedBuilderReach.Components.ExtendedDemolishableAccessible`) and its navigation-listener work.

Existing optimizer techniques to isolate:
- 8x8 spatial bins for candidate filtering and faster coordinate access.
- Reused buffers to avoid allocating thousands of candidates.
- Deferred large nav updates with 256 listeners / 8ms per-frame budget.
- Maintain callback order/validation and never blindly suppress `SetAccesses`.
- Strict `SetAccesses` no-op guard: engage only when runtime IL proves a pure pair of assignments and no competing Harmony owners; otherwise fail closed.
- Profile-driven tests saw large updates selecting a subset of >20k listeners, but some global updates still legitimately touched all listeners.

This branch currently retains a snapshot of the prior optimizer. Separate mod compilation, identifier, and exact patch movement require a dependency audit before independent installation. Do NOT enable two mods patching the same EBR methods simultaneously.

## Final extraction design

The independent `TBEBROptimizations.dll` has its own Harmony patch owner.
It installs only EBR spatial listener filtering, its strict no-op guard,
and `EbrDeferredDrainHook`. Deferred nav updates used to drain from the
benchmark mod's freeze-detector frame callback. Once moved out of the
benchmark, they would have stalled indefinitely without a replacement.
The new hook runs after vanilla
`SingletonLifecycleService.UpdateSingletons` on each normal frame and
calls `ExtendedBuilderReachNavOptimizer.DrainDeferred()`.
If the hook cannot be installed, the EBR optimization does **not**
install (fail-closed behavior). It never throttles global tick buckets.

The old quiet benchmark branch no longer installs EBR patches; the TB
Optimizer branch deletes the EBR optimizer class and source files.
Run one EBR Optimizations instance at a time.

Build verification proves compilation, not in-game dispatch correctness;
an in-game smoke test should verify that deferred candidates drain and
navigation remains responsive during terrain demolitions.
