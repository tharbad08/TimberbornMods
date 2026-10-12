# SWC runtime integration (optional)

TB Optimizer integrates with **Simple Workshop Control** without replacing, repacking, or version-pinning its Workshop DLL. SWC is **not** a RequiredMod: absence of its expected types simply skips both patches. The one `SWC` toggle (on by default) controls both harvest safety and scan scheduling; restart after changing it.

## Architecture / files

- [`ModStarter.cs`](ModStarter.cs): calls `SWCHarvestSafety.Install` first; calls the scan optimizer **only on successful safety installation**. This avoids running the scan scheduler without the re-entrancy guard.
- [`SWCHarvestSafety.cs`](Optimizations/Mods/SWC/SWCHarvestSafety.cs): patches `BehaviorManager.Tick` scopes and defers `SimpleWorkshopControler.UpdateBlockedState` until the outer behavior tick finishes; coalesces controller updates; cancels deferred work when the tick throws; checks `HarvestStarter.FindYielder` workplace both before search and after `GetYielders`, before any reservations. Neither workers nor game/save state are manually repaired.
- [`SWCScanOptimizer.cs`](Optimizations/Mods/SWC/SWCScanOptimizer.cs): runs **unmodified SWC harvest/planting Tick methods** at staggered 4-tick intervals; preserves cooldown, callbacks and state updates on intervening ticks. Tracks per-instance state with `ConditionalWeakTable` and uses cached field access delegates. Staggering avoids simultaneous scan bursts; this is NOT a rewrite of SWC's internal scan algorithms.
- [`OptimizationToggles.cs`](OptimizationToggles.cs): the single `SWC` startup checkbox.

## Compatibility rules

The feature uses reflection against currently installed SWC assemblies. It checks expected class names, no-argument method signatures, field types and important Tick IL field/callback references before patching. SWC is free to update independently. **This cannot guarantee that future SWC versions remain compatible:** when an expected signature/IL differs, the affected feature skips rather than guessing; restore original behavior by disabling the SWC toggle if necessary. The safety guard rejects changed `FindYielder` IL/exception regions. The older modified SWC performance build or the separate `SWC.HarvestSafety` DLL must not be installed simultaneously for comparative testing; these are detected to avoid duplicate changes. Install the ordinary current SWC Workshop mod alongside TB Optimizer.

## Original 1.1.36.2 patch coverage

Source: user-provided `SimpleWorkshopControl_1.1.36.2_HarvestSafety` zip. Its **performance code is in a modified SWC DLL**, plus a companion safety DLL. The safety module has been ported into TB Optimizer. The performance port covers the **scan scheduling and cooldown-preserving fast path**, but has **not yet reproduced** the modified DLL's internal reusable harvest-goods buffer / allocation-free harvest traversal, planting Forester caching, and planting soil/coordinate inner-loop optimizations. These would require safe, separately validated Harmony transpilers or a full compatible method replacement; it would be incorrect to claim byte-for-byte or runtime-equivalent performance to 1.1.36.2.

## Validation status and tests

- Code changes and settings are committed; game runtime smoke tests remain pending.
- **Not compiled or tested in the game as of this commit.** A passing GitHub Actions compilation is not proof of correct behavior on future SWC versions.
- Test without the patched 1.1.36.2 DLL and without separate `SWC.HarvestSafety.dll`. Confirm ordinary SWC loads, then verify workplace pause/resume during harvesting, in-range harvesting, farms/foresters, contaminated soil recovery, active/inactive controls, multiple buildings, save/load, and repeated game reloads. Compare FPS *and actual x1/x3 game speed* at identical game state and camera.
- For any unsupported SWC method/field/IL, refuse the optimization and let normal SWC execute. Avoid global scheduler/tick throttling.
