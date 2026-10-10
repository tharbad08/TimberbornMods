# T3MP Personal Companion (experimental)

Separate addon for stock Workshop T3MP **1.2.5+**, Harmony, and the Benchmark & Optimizer mod.

## Intended behavior
- Unpatch **only** the T3MP Harmony prefix owned by `t3mp.speed.requested` on `SpeedManager.ChangeSpeedScale(float)`. Other speed mods retain control of the speed values.
- Suppress the *original T3MP meter OnGUI only*, preserving T3MP's native tick observer and measurement calculation.
- Draw only the first text line, `rSPD/iSPD ...`. No UPS, no overlay smooth-mode banner.
- Attempt to anchor beneath the UI Toolkit speed panel via measured `worldBound`. Fallback is at the upper right, 155 pixels from the top.
- Do not modify T3MP's performance patches, game ticking, save loading, the optimizer, soil contamination, or preview logic.

## Limitations and notes
This is an **experimental initial implementation**. The in-game UI element may not be named with the expected Time/Speed/Buttons substring and UI Toolkit may not expose the game speed panel as UIDocument. In that case the fallback is used, not a guaranteed position below other mod UI. The original T3MP hide/show toggle is replaced by the companion readout. Shift+O smooth mode remains installed in T3MP and is *not* disabled by this addon.

The companion does **not** automatically resolve incompatibilities between unrelated optimization patches. It intentionally targets only the speed policy and GUI.

## Build
Use .NET SDK 8+ with NuGet access:
`dotnet build T3MPPersonalAddon/T3MPPersonalAddon.csproj -c Release`

Install into `Documents/Timberborn/Mods/T3MPPersonalAddon/version-1.1/` with `manifest.json` and `Scripts/T3MPPersonalAddon.dll`. Keep **stock** T3MP enabled and **disable our earlier modified T3MP DLL copy** (otherwise two T3MP instances may load).

GitHub Actions on the personal addon branch builds an installable ZIP if build dependencies resolve.

## Compatibility with future T3MP releases
The only hooks are the two T3MP internal names `T3MP.UI.SimulationRateMeterView.OnGUI` and `t3mp.speed.requested`. If these change, the addon fails explicitly rather than patching unrelated behavior. No T3MP binary changes are required.
