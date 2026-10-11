# v1.1.87 — UI-only simulation rendering mode

## User-facing control

The separate **UI-only simulation** panel in Benchmark & Optimizer
ModSettings has an "Enable UI-only simulation" checkbox, OFF by default.
It is session-only: leaving a game restores the camera and clears mode.
The existing pause-menu button still opens ModSettings with both controls.

The UI remains drawn. Gameplay simulation, entity lifecycle, timers,
production, pathfinding, all AI and all other game objects remain active.
We deliberately do not deactivate objects/MonoBehaviours or patch physics
and terrain systems. The UI-only switch alters ONLY the gameplay camera's
rendering-layer cullingMask. It first resolves the actual CameraService
camera, falling back to Camera.main if needed. It keeps the existing UI
layer if present, excludes other 3D layers, and restores the exact
previous cullingMask when OFF or the game scene unloads. It only restores
when no third-party mod has overwritten the changed mask.

**Limitations:** world-rendering cameras other than CameraService (if any),
offscreen render textures, render pipeline native work, water compute shaders,
object animation scripts and per-frame rendering setup may still operate.
This is a minimally invasive experiment for comparing rendering vs
simulation, not full render-engine suspension. UI Toolkit panels (including
the dev FPS panel) should remain visible because they are not part of
the camera world culling mask; needs in-game verification.

## FPS: both OFF and ON

The service is a regular IUpdatableSingleton and keeps a monotonic window:
every 15 wall-clock seconds it reads Unity.Time.frameCount, calculates average
observed FPS over the interval, and appends a UIONLY.FPS line to the normal
optimizer-v11.log with mode, frames, elapsed seconds and source. If
Time.frameCount is unavailable, it falls back to the service update count
(explicitly marked). Mode transitions reset the interval; compare subsequent
15-second full windows at the SAME save, camera, game speed and weather.
This never modifies Timberborn's built-in FramesPerSecondPanel or
FramesPerSecondCounter.

The optimizer permanently pins FramesPerSecondPanel,
FramesPerSecondCounter and UiOnlySimulationService to interval=1, even
when global optimizer default or previous settings request throttling.
This ensures the built-in FPS display continues to refresh, assuming it
was enabled in-game by the user.

## Risk and safety

- No disabling GameObjects or components, scene roots or model update
  services: prevents accidental halt of gameplay update callbacks.
- No modifications to Unity Scene loading/saves/soil/global tick budgets.
- Does NOT turn off animation CPU loops (logs show AnimatorRegistry ~2ms
  while PhysX/nav/GC can be far larger). Animations keep advancing
  offscreen so render and simulation can be compared.
- No persistent enable state; scene reload always restores and OFF.
- Existing v1.1.86 LOADSAVE and lighter normal-play profilers retained.
- If camera resolution or cullingMask reflection fails, checkbox
  remains OFF and explanation is logged.
- If another mod changes camera mask while enabled, it is never
  automatically overwritten; restoration respects the external change.
- For a fair benchmark compare FPS but ALSO observed game-hours per
  real minute; FPS alone might rise while simulation remains slow.

## Test

1. Load colony, verify FPS dev counter is visible (Settings > Show FPS).
2. Keep world visible and run ~30 seconds; inspect UIONLY.FPS NORMAL.
3. Open Benchmark & Optimizer > UI-only simulation, check the box.
4. Confirm the world disappears and existing overlays still work; run
   ~30 seconds, compare UIONLY.FPS UI_ONLY, gameplay progress.
5. Uncheck, verify camera world reappears and unmodified game controls work.
6. Test closing the game while ON; restart and verify OFF by default.
7. Send optimizer-v11.log and optimizer-v11-freezes.log.

If the built-in FPS panel disappears, immediately uncheck UI-only mode.
Do not add a permanent global renderer-disable or camera.enabled=false
patch without validating screen overlay camera topology and restoration.
