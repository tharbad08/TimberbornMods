# T3MP Personal Companion (experimental)

Separate addon for stock Workshop T3MP **1.2.5+**, Harmony, and the Benchmark & Optimizer mod.

## Intended behavior
- Unpatch **only** the T3MP Harmony prefix owned by `t3mp.speed.requested` on `SpeedManager.ChangeSpeedScale(float)`. Other speed mods retain control of the speed values.
- Suppress the *original T3MP meter OnGUI only*, preserving T3MP's native tick observer and measurement calculation.
- Draw only the first text line, `rSPD/iSPD ...`. No UPS, no overlay smooth-mode banner.
- Anchor beneath the existing **Global view** header; expanded menu is layered above the readout.
- Do not modify T3MP's performance patches, game ticking, save loading, the optimizer, soil contamination, or preview logic.

## Global view placement (v0.2)
The readout is a non-interactive UI Toolkit Label positioned beneath the **Global view** header in the top-right HUD. It uses the header's `worldBound`, not the expanded menu height. The label is inserted as the *first* element of its root and will be drawn behind other UI content, including expanded Global view rows. It does not resize or push down the HUD. Text is sampled from T3MP's existing meter, omitting UPS.

The anchor searches the visible English label **Global view**. If that exact label is absent (localization or an alternate view selector), the companion deliberately suppresses the old T3MP overlay but does not draw a misplaced replacement. A screenshot and log are needed for a new selector variant.

## Limitations and notes
This is an **experimental initial implementation**. The selector hierarchy and UI Toolkit render layering require validation with the user's installed mod set. If the selector label is missing or localized, the readout stays hidden rather than drawing at an unrelated fixed position. The original T3MP hide/show toggle is replaced by the companion readout. Shift+O smooth mode remains installed in T3MP and is *not* disabled by this addon.

The companion does **not** automatically resolve incompatibilities between unrelated optimization patches. It intentionally targets only the speed policy and GUI.

## Build
Use .NET SDK 8+ with NuGet access:
`dotnet build T3MPPersonalAddon/T3MPPersonalAddon.csproj -c Release`

Install into `Documents/Timberborn/Mods/T3MPPersonalAddon/version-1.1/` with `manifest.json` and `Scripts/T3MPPersonalAddon.dll`. Keep **stock** T3MP enabled and **disable our earlier modified T3MP DLL copy** (otherwise two T3MP instances may load).

GitHub Actions on the personal addon branch builds an installable ZIP if build dependencies resolve.

## Compatibility with future T3MP releases
The only hooks are the two T3MP internal names `T3MP.UI.SimulationRateMeterView.OnGUI` and `t3mp.speed.requested`. If these change, the addon fails explicitly rather than patching unrelated behavior. No T3MP binary changes are required.
