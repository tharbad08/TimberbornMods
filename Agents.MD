# ModDesc.txt

When asked to write a `ModDesc.txt`, write a Steam Workshop description in Steam BBCode (not Markdown). Match the recent mods (Crane, Fourpillars, ConstructionSiteHauler, BuildingRenovations): player-facing, practical, and compact.

Voice: second person, what the player gets. No marketing fluff. Light humor only in `[i]` flavor lines (new buildings). Bold names, UI labels, and caveats with `[b]`.

BBCode: `[b]`, `[i]`, `[h1]`/`[h2]`, `[img]url[/img]`, `[url=...]text[/url]`, `[list][*] item[/list]`, `[table]`/`[tr]`/`[th]`/`[td]`, `[spoiler]` only when needed. Place a screenshot soon after the intro and after major sections (Steam UGC URL when available).

Typical order (omit unused sections):

1. 1–2 sentences of what the mod does. Optional `[i]` flavor line.
2. Screenshot.
3. `[h1]`/`[h2]` for buildings, features, settings, how-to, tips, FAQs, extending.
4. If configurable: `Main Menu -> Mods -> Settings button next to the mod name`. Say when defaults leave vanilla unchanged.
5. Stats as dashes (`Unlock`, `Cost`, `Workers`, `Range`, …). Related mods as Steam `[url]`.
6. Footer exactly:

```
===
Mod source code and donation: https://github.com/datvm/TimberbornMods (feel free to use the code for anything).
```

# Versioning

Do not bump a mod's `manifest.json` version, `ChangeLog.txt` version heading, or `IModUpdateNotifier2` version / loc key unless the user explicitly asks to bump the version. Fold new notes into the current version entry.

# Code Style

Always use code block even for single conditional or loop statements. For example:
```cs
if (condition) { return; }

// NOT:
if (condition) return;
```

Always use latest C# features like pattern matching, switch expressions, and collection initializers. For example:
```cs
List<int> numbers = [];
MyType variable = new();

// NOT:
List<int> numbers = new();
MyType variable = new MyType();
```

Prefer no access modifier for lowest visibility. Do not use `_` to prefix fields. For example:
```cs
int field;
class MyClass {}

// NOT:
private int _field;
internal class MyClass {}
```

Info-only types (DTOs, checks, list entries, bags of data with no behavior) should be `readonly record struct`, or `record` if they need reference identity. For example:
```cs
readonly record struct Shot(Vector3Int Dest, GameObject Box, float Started);

// NOT:
sealed class Shot
{
    public Vector3Int Dest { get; }
    public GameObject Box { get; }
    public float Started { get; }
}
```

When a `BaseComponent` needs more than one or two injected services, put those dependencies on a `[BindSingleton]` service instead and inject only that service into the component. Put calculation / orchestration methods on the service. For simple shared deps like `ILoc`, expose a readonly field:

```cs
[BindSingleton]
public class DischargePipeService(IWaterService waterService, IThreadSafeWaterMap waterMap, ILoc t)
{
    public readonly ILoc t = t;
    // …
}

[AddTemplateModule2(typeof(DischargePipeSpec))]
public class DischargePipe(DischargePipeService service) : BaseComponent // …
```

// NOT: inject IWaterService + IThreadSafeWaterMap + ILoc directly into the component.

# Singleton lifecycle

`ILoadableSingleton.Load` and `IPostLoadableSingleton.PostLoad` run in **dependency order**. If B's constructor injects A, `A.Load` always runs before `B.Load` (same for `PostLoad`). Do not add `EnsureLoaded` / lazy-init sequences to force that order.

# Tests

Keep test method names short and descriptive. Prefer names like `InitializeWithCorrectCharacterType`, `IgnoreWrongCharacterType`, or `ComputeSegments` over long Given/When/Then-style names.

Prefer focused tests with clear arrange/act/assert spacing. Use helpers or factories for repeated setup when Unity or Timberborn types require reflection or publicized DLL setup.

# Tools

Timberborn game source should be read from `ModdingAssetUpdater/out`, which contains decompiled game DLLs. When investigating game behavior, search and read that folder instead of decompiling assemblies on demand.

More exported Timberborn game data is stored at `D:\Personal\Mods\Timberborn\V1Data\ExportedProject\Assets\Resources`. For example, the `Buildings` folder there contains game building blueprints that may be useful when comparing mod blueprints to vanilla behavior.

If `ModdingAssetUpdater/out` does not exist or is empty, ask the user to decompile the game source there first.

Leave `BackwardCompatibleTemplateNames` as `[]`. Do not add old template names unless the user explicitly asks. We are developing; save migration is not needed unless requested.

Game assemblies are publicized for mod projects. It is okay to reference publicized Timberborn types and members directly in code and Harmony patches instead of avoiding `internal` or private members with reflection-only approaches.

Timberborn grid coordinates use **X/Y for the horizontal plane and Z for vertical**. Unity is the opposite: **Y is up**, with X/Z as the horizontal plane. When converting grid coordinates or directions into Unity/world vectors, check `CoordinateSystem` and its namespace code first (`GridToWorld`, `GridToWorldCentered`, and the reverse). It likely already has the conversion needed, so avoid adding local helpers like `ToUnityDirection` unless the built-in conversion does not fit.

## Timbermesh models (read / edit)

When you need to **read or edit** a `.timbermesh` model (node hierarchy, materials, meshes, doors/props, etc.), use the `tmesh2glb` tool from `Timbermesh2Blender` — do **not** try to parse the binary by hand.

Working directory for dumps/packs: **`D:\Temp\TimbermeshModel`**.

```powershell
# From repo root (or run the built tool / installed global tool `tmesh2glb`)
dotnet run --project Timbermesh2Blender -- dump -i <model-or-folder> -o D:\Temp\TimbermeshModel -f
# Edit the produced *.timbermesh.json
dotnet run --project Timbermesh2Blender -- pack -i D:\Temp\TimbermeshModel\<name>.timbermesh.json -o D:\Temp\TimbermeshModel -f
```

Notes:

- Default dump keeps vertex attributes as **base64** (lossless). Use `--decode-vertices` only when you need numeric position/normal/uv arrays.
- Prefer structural edits in JSON (delete/rename/reparent nodes, swap material names). Use **glb** export + Blender for real mesh sculpting.
- After packing, you can preview with `tmesh2glb glb -i ... -r <Resources> -o D:\Temp\TimbermeshModel -f`.
- Game Resources path: `D:\Personal\Mods\Timberborn\V1Data\ExportedProject\Assets\Resources`.
- **Folktails first.** Unless the user explicitly asks for Iron Teeth (or both factions), only dump / edit / pack **Folktails** models. Do the Iron Teeth pass later, after the Folktails model is approved.

## Buildings (Folktails first)

Unless the user explicitly asks for Iron Teeth (or both factions), only create and edit **Folktails** buildings. Do not create `<Building>.IronTeeth.blueprint.json` or an Iron Teeth collection until they say they are ready for the IT pass.

When adding a building, copy a placeholder icon **into that building's own folder** (for example `Buildings/Water/Pipes/NewPipe/NewPipeIcon.png`) and point `LabeledEntitySpec.Icon` at that local file. Do not reuse another building's icon path.


# Cross-project Timberborn modding lessons (tharbad08)

These rules capture failures and successful patterns across TB Optimizer, Extended Builder Reach, TB Doctor, Folktails+ add-ons, Keystone/MoM compatibility, All Weather Drill, weather integration, and other Timberborn 1.1 work. They supplement—not supersede—the maintainer-specific code style, tools, and release rules above. For a contribution to someone else's repository, read and follow **that repository's own agent guide** (notably datvm's `Agents.MD`) and fit its conventions. Do not copy unrelated architecture or refactor a maintainer's code merely to match our projects.

## Non-negotiable outcomes

- **Correctness and in-game compatibility over compile success.** Name the exact game/mod versions and what has actually been verified: source inspected, CI compiled, ZIP inspected, game started, settings opened, save loaded, or gameplay tested. Never equate those states.
- **Use the author name `tharbad08` only** in our mods' manifest author fields, assembly/file metadata where applicable, README credits, commit identity we control, PR prose, screenshots, generated ZIPs, and examples. Do not embed the user's legal name or private machine paths. Preserve other contributors' *genuine* upstream attribution; do not falsify history.
- When a ready-to-use mod is requested, deliver **one verified, installable ZIP**, not merely source, workflow instructions, or a DLL. Keep only one version of the mod installed when testing. If GitHub Actions is used, verify the run succeeded and the artifact actually exists before supplying it. Never promise runtime testing performed only by CI.
- Make the smallest change that solves the stated problem. Keep unrelated optimizations, migrations, diagnostics, and mod integrations separate. Preserve independently switchable features and the existing user's explicit OFF choices.

## Source of truth: real APIs, not invented stubs

1. **Look for an existing solution first**: Timberborn services (Bindito, coordinates, weather, notifications, jobs, save/load), TimberUi, Mod Settings, Harmony, the dependency mod's public APIs, and already-implemented code. Do not recreate UI, registries, scheduling, serialization, asset conversion, or mod-owned scans without proving the native path is insufficient.
2. For Timberborn 1.1, **compile against the actual game DLLs and all applicable dependency mod DLLs** whenever possible; also examine the decompiled game source in `ModdingAssetUpdater/out` in repositories that provide it. A handwritten stub can let a build pass while the game throws `MissingMethodException` or `TypeLoadException` on startup. Treat stubs as compile-only fallbacks and identify them explicitly in build reports.
3. Check exact **CLR signatures**, not just names: assembly identity, namespace, public/internal visibility, static/instance, return type, overloaded parameters, parameterless vs optional constructors, properties vs fields, interface/by-ref methods, generic type, and version. Verify against the DLL actually loaded by the user's game. Use Harmony's `AccessTools` for guarded introspection; never fabricate a signature to silence compiler errors.
4. **Real incidents:** incorrect stub `IStyle.set_opacity(float)` crashed TB Optimizer's settings even though CI passed; the runtime Unity UI API did not expose that setter. `ReadonlyTextModSetting.TextSettings` did not have the invented parameterless constructor in the user's actual runtime; check its real three-parameter constructor and types. `ModRepository.EnabledMods` was incorrectly modeled as a collection rather than its actual enumerable shape. `ModManifest.Id` and `Bindito.Configurator.Configure` also required checking true declarations. Similar assumptions can break type loading before a settings window appears.
5. Collect **all relevant DLLs** for crash work: game Managed assemblies; Harmony, Mod Settings, TimberUi; installed optional Workshop/local mods; nested `version-*`/`Scripts` directories; and DLLs referenced by the crashing mod. Support multiple Steam library locations and Linux/Proton paths. Retain relative paths and distinct DLLs, inventory them with SHA-256, and avoid basename collisions. A ZIP of game DLLs alone cannot prove a third-party DLL's shape. Put configurable paths at the top of a Linux collector script when the user requests that.
6. Follow the existing native coordinate conversion service: **Timberborn grid X/Y are horizontal and Z is elevation**; **Unity Y is up**. Inspect `CoordinateSystem` and use its grid/world helpers before writing conversions.
7. Prefer lifecycle interfaces (`ILoadableSingleton`, `IPostLoadableSingleton`, `IUpdatableSingleton`) and Bindito registration/injection where they suit the task, but verify actual invocation timing and ordering. An `UpdateSingletons` callback count is **not automatically a rendered-frame counter**; don't replace a carefully timed Harmony callback based on nomenclature alone.

## Mod Settings and UI (learned the hard way)

- Prefer the **native Mod Settings framework** for boolean settings, tooltips, enable conditions, persistence, Reset to Default, headings, and scroll behavior. `ModSettings.Core` owns settings; `ModSettings.CommonUI` owns standard setting element factories; `ModSettings.Common` includes `ReadonlyTextModSetting`. Use the exact real assembly references.
- For TB Optimizer-style grouped settings, render **Base game** and **Other mods** as distinct native heading rows instead of repeating `Base game |` or `Other mods |` in every label. Keep labels short (aim **40 characters max**) and explanations concise (one or two lines or native tooltips), with text wrapping and no horizontal scrollbar.
- **Never globally resize the shared `ModSettingsBox`.** Its default is approximately 590 px in the inspected framework. A previous custom 885 px override widened settings windows for every mod. Use the framework's existing outer vertical scroll, not a second nested scroll view. Align native checkboxes instead of recreating toggle layout by hand.
- Avoid custom `UnityEngine.UIElements.IStyle` setters unless they are necessary and their exact live API has been checked. In particular, do not resurrect a handwritten `float opacity` stub or `style.opacity = 0.78f` assumption.
- Respect Reset to Default: it must reset the real persisted setting, update the displayed state, and not unexpectedly override another preference source. Don't add a separate custom JSON store if Mod Settings/`ISettings` already provides persistence. When migrating legacy JSON to native settings, **read old explicit OFF values before initialization**, migrate once, preserve opt-outs across restart, and verify the startup read path before removing the old store. Never claim migration complete because both stores happen to update in one screen.
- Test Mod Settings in both main menu and loaded game: opening, scroll, headings, long labels, absent/disabled dependencies, toggle, reset, close/reopen, full restart, and existing preferences. A screenshot of a well-rendered UI does not establish correct startup option application.

## Optional mods and Harmony integration

- Optional means **optional**: do not make unrelated players install Folktails+, Miners of Mountains, EBR, Keystone, BFR, SWC, TimberPhysics, SuperCursor, or Moddable Weathers merely to keep a compatibility patch from crashing. Use native required dependencies only when truly mandatory. Optional features must remain inactive when their owner mod is absent.
- A loaded DLL/type is **not** proof its mod is enabled. Where the game's lifecycle provides it, use `ModRepository.EnabledMods` and the actual manifest **Id**, then verify required types/methods/signatures/IL. At very early `StartMod` time, do not assume the same injected service is available; check startup order or fail closed. Identify and document any gap between settings-window detection and startup-patch detection.
- Detect **installed upstream equivalents** and prior Harmony owners; never install duplicate fast paths/dispatchers if the target mod already includes a new PR's code or the optimization is provided by another DLL. Conditional integrations must validate their whole target signature, not just a type name. Unknown versions fail safely back to vanilla.
- Patch the narrowest authoritative method. Preserve ordering, callback/event invariants, original side effects, exceptions, and success/failure semantics. Never skip critical work silently because a field lookup failed. For partial patch installation, unpatch **every hook installed by this feature** using only our Harmony owner ID; clear static state and leave the original code runnable.
- Use Harmony transpilers only after inspecting the actual IL (including fields, call shapes, surrounding control flow, existing patches, and changed mod versions). Prefer an existing public hook or service where it suffices. Fail closed on unknown IL instead of guessing.
- On unload/reload/save switch, clear or scope caches. `ConditionalWeakTable`/weak references can prevent leaked world references but are not replacements for correct invalidation and lifecycle hooks. Do not let static caches retain objects from prior maps.
- Prefer **game logging** (visible in `Player.log`) and the game's notification service instead of custom notification UI. If a notification needs 30-second auto-dismiss, verify the real notification API provides that behavior; do not invent a timer or promise dismissal without implementation. Avoid noisy per-tick logging in release builds.

## Gameplay and performance invariants: do not repeat failed experiments

- **Never slow/skip global tick dispatchers** (`TickableBucketService.TickBuckets` or shared singleton/entity update loops): planting and unrelated systems can starve. The earlier soil/planting throttling during weather recovery caused or coincided with stalled planting and was not proven safe. TB Optimizer's retained soil patch changes **reset work only**, not the simulation cadence.
- Keep benchmarks, tick intervals, CSV profiling, freeze diagnostics, GC research, and save/startup research on their assigned branches/projects unless explicitly requested in a release. BenchmarkAndOptimizerV11Port owns benchmarking/protected-tick policy; TBSave/TBGC hold respective research; TB Optimizer releases only independently selectable, guarded gameplay fixes. Remove temporary verbose diagnostics once the user says to ship quietly.
- **Measure first**: paused vs x1 vs x3 FPS, frame times, actual x3 simulation throughput, GC pauses, allocations, and correctness in the same late-game save. CPU/GPU utilization alone does not prove a subsystem is faster. Distinguish improvement from dropped/deferred simulation work. Do not add arbitrary thresholds or claim a speedup without before/after results.
- Unity incremental GC already exists. Manually scheduling slices may add overhead; don't claim it is better without controlled evidence. Detect whether incremental GC is actually enabled, do not force the engine mode, and do not mistake managed heap size for game process RAM.
- **EBR:** preserve builder/reachability and demolition semantics, 3D intersection checks, **8×8 XY spatial buckets**, registration/removal and changed-bounds reindexing, and guarded deferred-notification processing (**256 listeners or 8 ms**, in the PR test). When porting upstream EBR code to a separate optimizing DLL, old Workshop EBR may lack its direct `BoundsChanged` call; an external change-triggered adapter may be required. Do not run the upstream and external dispatchers together. Recheck save/load and large terrain edits.
- **TimberPhysics:** colliders must never leave mixed vanilla/merged dictionaries or stale keys after partial failure. Use transactional fallback/complete cleanup, account for interface/by-ref column accessors, and batch dirty **32×32** chunks. Limiting fixed-step catch-up can **drop simulation time**: document that as an explicit physics tradeoff, not semantics-preserving optimization.
- **SWC:** prefer small guarded changes over reproducing native crop selection/planting algorithms. Changes to harvest/plant scans must preserve cooldowns, blocked-state updates, reservations, workplace safety, and full scans at safe intervals. If custom fast scans exist, validate IL and fail to original method when mismatched, avoiding duplicate optimization when the installed SWC already ships it.
- **Keystone and MoM:** cosmetic decoration/flourishes and terrain/stone excavation interact; compatibility must not presume MoM is installed and must not make decoration unexcavatable or incorrectly spawn on water. Prefer a small optional adapter when core mod ownership is unclear. Test placement, excavation, and world changes.
- **Weather and save/load:** loading a save during drought, badtide, acid rain, monsoon, or combined weather modifiers does not necessarily replay the event-start callback. Reconstruct behavior from the **current live weather state** and test reopening mid-event (including base weather plus modifier combinations). Do not cache an event-start flag and assume it survives loading.
- **Faction content:** test Folktails and Iron Teeth rules separately. When merging Folktails+ add-ons, deliver **one manifest, one mod ID, one install folder**, optionally enabled features, and explicit prerequisite checks; do not bundle three separately enabled submods or assume the faction's unavailable buildings/tech can be unlocked without proper registration.
- **Demolition stalls:** distinguish a legitimately waiting job from a permanently invalid/corrupt job. Favor narrow repair, Player.log diagnostic context and optional native user notification; never delete or reorder arbitrary jobs just to clear a queue.

## Build, packaging, releases and PR etiquette

1. Understand which **repository/branch is the actual source** before editing. Pull/fetch the latest version, check upstream changes, then create a focused branch; do not port stale local optimizer code or overwrite a maintainer's fixes. Keep EBR, TimberPhysics, Keystone, BFR and other upstream proposals independently reviewable.
2. Follow **the target maintainer's `AGENTS.md`/`Agents.MD`** and style even if our own style differs. Prepare human-facing PRs: concise problem, root cause, changed files, lifecycle/compatibility explanation, before/after evidence, and concrete reproduction/test steps. Do not assume a reviewer is another AI. Keep optional mod support optional; make PRs compile and work even when the other mod is absent.
3. Verify official compiled references and dependencies are present, CI passes, and source↔artifact corresponds to the intended **branch and commit**. GitHub Actions can compile using real references if provided; absence of the SDK locally is not a reason to ship an old binary as if newly compiled. Never claim byte-for-byte or gameplay equivalence without comparing hashes/behavior.
4. Ship a Timberborn-compatible ZIP rooted at the mod folder (for example, `TBOptimizer/version-1.1/manifest.json` and `TBOptimizer/version-1.1/Scripts/TBOptimizer.dll`). Verify manifest ID, meaningful Description, dependencies, version, DLL validity, ZIP integrity, and **no duplicate older mod folder**. For one merged mod, don't leave old separate manifests/IDs active.
5. Respect the target repository's **version/changelog/ModDesc rules above**; do not bump other maintainers' versions or rewrite workshop copy without explicit authorization. For our own release when requested, keep manifest, DLL, workflow artifact and reported version consistent. Steam Workshop descriptions use BBCode where prescribed; a Workshop thumbnail isn't automatically an in-game icon.
6. Maintain an explicit **done/not done/needs-game-test** status. Successful CI proves compilation and archive creation, not correct runtime load. Test against installed optional DLLs and actual `Player.log`. Inspect the complete exception + inner exception + stack, not just the first error, and reproduce on the exact binary.
7. Record successes, failures, attempted fixes, API shapes, version-specific assumptions, and touched files in project docs (e.g. `OPTIMIZATIONS.md`, `FAILED_EXPERIMENTS.md`, `MOD-DETECTION.md`). Failed attempts are a **do-not-repeat register**, not an optimization wishlist. Only reopen a rejected idea when new measurements or a verified API justify it.
8. **Privacy in Git:** scan current files, all owned branches/tags/history, generated archives, manifests, binary resources and commit identity for personal names; don't mistake PNG binary coincidence for a textual leak. Do not rewrite published history or force-push without explicit request and verification. Preserve upstream authorship; for new work use `tharbad08`.
9. If a user requests a ready-to-test ZIP, actually provide it (not just the PR). If a build fails or the binary cannot be verified, report the blocker plainly. When available, include a successful Actions build link as a secondary fallback.

## Mandatory validation matrix for changes

- **Load:** main menu startup, settings open/close/reset, loaded-game startup; test with game version and dependency DLL inventory recorded.
- **Optional mod state:** not installed, installed but disabled, installed and enabled, compatible/changed API, and explicit OFF switch after a restart.
- **Gameplay:** new colony and late-game save; x1/x3; construction previews; EBR changed bounds, roads, large terrain edits and demolition; SWC planting/harvest; Keystone/MoM placement/excavation; physics terrain updates; weather event transitions and save/load during an event when relevant.
- **Safety:** no lost tick work, no stale collider/navigation objects, no duplicate Harmony patch owners, no leaking across map loads, and vanilla fallback on feature patch failure.
- **Deliverable:** exact source commit and build referenced, manifest and runtime DLL inspected, archive extraction verified, author/privacy checked, docs and limitations accurately reported.

If a test is not possible here, **say which test was not run and why**. Do not substitute an invented stub or a successful build for an in-game test.
