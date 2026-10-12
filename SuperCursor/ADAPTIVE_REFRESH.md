# SuperCursor adaptive information refresh (Timberborn 1.1)

This change incorporates the SuperCursor-specific performance investigation from the separate **TB Optimizer** project directly into the maintained SuperCursor source. It is intentionally limited to `SuperCursor/Services/SuperCursorTool.V1.cs`; the Update 7 variant and other mods are unchanged.

## Behavior

- Holding the configured SuperCursor key still processes the normal cursor input on *every* game input update.
- Tooltip **position and visibility** update on every input update, even when the text has not been recomputed.
- Coordinate picking, object raycasting, describers and string formatting run at most every **50 ms** in normal conditions (20 Hz).
- If the last content refresh takes **30 ms or longer**, use a **100 ms** delay (10 Hz). If it takes **100 ms or longer**, use **250 ms** (4 Hz).
- The interval is counted from *the end* of refresh work to avoid immediate expensive catch-up calls.
- Releasing the hotkey, hovering UI, or exiting the cursor tool clears the deadline, so the tooltip refreshes immediately on reactivation.
- Describers, text, coordinate semantics and keyboard bindings remain unchanged; information may be up to 50/100/250 ms old while displayed.

## Compatibility and integration

This optimization does **not** require Benchmark & Optimizer, Harmony or TB Optimizer. It is implemented in SuperCursor itself.

**Do not also enable TB Optimizer's separate `SuperCursor` checkbox** when testing this native implementation: the existing external patch intercepts `ProcessInfo` and may skip its per-frame label movement. Disable that toggle and restart the game before A/B testing.

## Review and validation checklist

- [x] Compared the SuperCursor directory against upstream `datvm/TimberbornMods` master; the fork's existing SuperCursor tree is identical.
- [x] The PR branch starts from upstream master to avoid importing unrelated fork commits.
- [x] Scoped change to the v1.1 variant (no alteration to the Update 7 implementation).
- [x] Keeps every-frame input, tooltip movement, visibility and native describer ordering.
- [x] No new dependencies or mod settings.
- [ ] Compile against the actual Timberborn 1.1/publicized assemblies (no game assemblies or .NET SDK available in this environment).
- [ ] Verify behavior in-game: stationary/moving cursor, rapidly changing coordinates and objects, tool exits, mouse-over-UI, key release and re-press, editor/game contexts.
- [ ] Benchmark before/after with the *external SuperCursor patch disabled*, at the same save/camera/game speed; measure both FPS **and** actual game speed.
- [ ] Inspect unusual slow refreshes and decide whether the 100/250 ms backoff is tolerable.

## Tradeoff

At high game load, tooltip **text may lag** behind the mouse by up to approximately 250 ms; the tooltip itself should continue to follow the mouse. This intentionally reduces redundant raycasts and text allocations. It is a potential improvement, not a measured FPS claim.
