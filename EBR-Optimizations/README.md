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
