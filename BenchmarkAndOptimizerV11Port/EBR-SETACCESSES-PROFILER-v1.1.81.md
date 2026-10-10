# EBR SetAccesses profiler fix — v1.1.81

## Evidence

The 2026-10-11 user test of v1.1.80 reported:

```
EBR diagnostic probe unavailable: Timberborn.Navigation.Accessible.SetAccesses/1
EBR diagnostic sampler active: ... bounds=1, accesses=1, apply=0, rebuild=1
```

The earlier code incorrectly required `Accessible.SetAccesses` to have exactly one parameter. This is an instrumentation error, not evidence that `SetAccesses` is absent or that the original EBR callback failed.

## Changes

- Discover and patch **all non-generic instance overloads named SetAccesses**, without assuming parameter count, on the `Accessible` type obtained directly from EBR's `accessible` field. This avoids hard-coding a version-specific signature and keeps unrelated methods untouched.
- Log each **successfully patched runtime signature** as `EBR SetAccesses profiler attached:`.
- If no compatible method can be patched, log the failure and available access-related method names/argument counts to diagnose the actual installed assembly in a single game startup.
- Keep existing 1-in-32 notification samples, and include `SetAccesses` wall time only within sampled EBR callbacks.
- Include inclusive `UpdateAccesses` time for those same callbacks and split unmeasured time into `rebuildOther~` and `callbackOther~`. `rebuildOther~` includes any work in `UpdateAccesses` beyond timed bounds, access generation, and access application, including `DisableComponent` and instrumentation overhead.
- Do **not** equate previous "other 85%" with SetAccesses: it was unmeasured work, not a proven cause.

## Safety

This change adds diagnostic-only Harmony prefix/finalizer hooks. The original method and its exception/return behavior are preserved; no accessibility data, notifications, listener count/budgets, workers, GC, save paths, Soil Contamination, tick scheduling, preview validation, or other mods are modified.

## Single-run validation

1. Confirm in optimizer-v11.log that `EBR SetAccesses profiler attached:` is printed on startup and the summary says `apply=1` or more.
2. Let the colony run through a large nav update and normal changes.
3. Check one or more `EBR.PROFILE` lines for `apply=...ms/N`, `rebuildOther~`, and `callbackOther~`. `apply` call counts must be nonzero if SetAccesses is actually invoked during sampled EBR callbacks.
4. Share optimizer-v11.log and optimizer-v11-freezes.log for interpretation.

If `apply=0` persists despite a successful hook, inspect whether EBR invokes a different method, an extension, or bypasses the hooked method in that game build before drawing timing conclusions.
