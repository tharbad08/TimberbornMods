# v1.1.86 — low-overhead late-game load and save probes

## Source-informed measurement, not a save rewrite

Historical Timberborn source was inspected for GameSceneLoader,
SceneLoader.LoadSceneCoroutine, GameLoader,
GameSaveDeserializer, SaveReader, WorldSerializer,
SerializedObjectReaderWriter, SingletonLifecycleService,
WorldEntitiesLoader, SerializedWorldFactory, WorldEntryWriter,
SaveWriter and GameSaver. These references guide runtime method discovery;
**they do not prove v1.1's installed game binaries share every signature**.
The probe logs skipped/missing methods and continues safely.

**Load path (roughly):** save-selection/validation → scene-load request →
scene coroutine (SceneManager.LoadScene/asset cleanup/GC) → open ZIP →
read/parse JSON → build serialized world → load singletons →
instantiate/initialize entities → post-load phases → ShowPrimaryUI.
The entire elapsed time includes time *between* callback stages.

**Save path (roughly):** GameSaver.Save → finish pending tick →
SerializedWorldFactory.Create (entities/singletons) →
WorldSerializer.WriteEntry (JSON) inside WorldEntryWriter →
SaveWriter.WriteToSaveStream (ZIP entries and compression/finalization) →
game-save repository creates the file → copy memory buffer to file →
callback/stream disposal. Distinct inclusive timers help tell whether
save cost is world snapshot, JSON, ZIP, or residual file-copy/I/O.

## Output

All reports go to the existing normal optimizer log:

- LOAD.START, LOAD.BEGIN, LOAD.GAP, LOAD.PHASE,
  LOAD.SUMMARY, LOAD.STAGE (up to 35 highest total),
  LOAD.SUMMARY_END.
- SAVE.START, SAVE.BEGIN, SAVE.PHASE, SAVE.SUMMARY,
  SAVE.STAGE (up to 35), SAVE.SUMMARY_END.
- LOADSAVE.PROBE startup: total installed + skipped hooks and counts
  of concrete load handler and save writer hooks.

The load session starts on the save-load request when available and
ends when GameInitializer.ShowPrimaryUI returns. It therefore includes
coroutine/native-engine gaps that are absent from UpdateSingleton profiles.
When the request method cannot be resolved, the fallback starts at the
first identifiable world-load phase; **time preceding that fallback is
unmeasured**, explicitly indicated by the reported origin.

A SAVE session starts on private GameSaver.Save rather than SaveQueued
(which runs every LateUpdate), and ends on Save return. If a save is
performed without GameSaver.Save, SaveWriter starts a writer-only session
and ends it on completion; this lacks final copy-to-file time.

Small individual steps are aggregated, not logged per entity or per
component. Load lifecycle handler implementations and save entry writers
are hooked only on their load/save methods, never tick/update methods.
At most 650 implementer methods per interface are probed. Method-name
misses are fail-closed; no guessed overload patches.

### How to interpret

- Stage values are **inclusive**, and nested stages overlap. Do not
  sum them (e.g., WorldFactory.Create contains SaveEntities).
- The SaveWriter timer covers the ZIP writing process including disposal
  and compression, not purely compression CPU. Comparing it with
  entry-writer timers gives an *approximate* residual.
- The remaining Save total beyond SaveWriter also includes
  Tick.FinishFullTick, file creation/copy/OS I/O, and callback logic.
  It cannot be attributed entirely to disk speed.
- JSON.BuildAndWrite includes both object traversal and streaming.
- A long scene load gap could be Unity native work, async waits, GC or
  other systems; scene coroutine MoveNext timings narrow this without
  replacing/canceling Unity operations.
- No timer proves whether a save snapshot can be frozen/released while
  compression continues: such an architecture change remains out
  of scope until approved.

## Normal-play FPS recovery

10 previously confirmed high-frequency **diagnostic-only**
profiler groups are no longer registered at startup:
FaunaInstantiationDetailProfiler,
InputProcessorDetailProfiler,
EbbAndFlowDetailProfiler,
InstantiationHotspotProfiler,
PreviewComponentBatchCounter,
HotInputDetailProfiler,
PreviewServiceMemberDetailProfiler,
BlockPlacementDetailProfiler,
LevelVisibilityHandlerProfiler,
SmartPowerPreviewWorkProfiler.

The remaining FreezeDetector, high-level singleton profile,
save adapter/Gen2/heap, navigation and EBR queue metrics still work.
All gameplay hooks remain exactly as before; Soil Contamination,
save serialization, TickableBucketService, preview validity,
T3MP, SmartPower, AutoScaffold and EzTube are not modified.

## One-run validation

Open a late-game settlement and allow it to reach the playable UI.
Do at least one manual save and one autosave. No repeated restarts needed.
Send **optimizer-v11.log** and **optimizer-v11-freezes.log**.
Look for LOAD.START → LOAD.SUMMARY and SAVE.START → SAVE.SUMMARY
with complete top-stage lists. If a hook cannot attach, its missing
signature should appear at startup and remaining phases will still
produce useful diagnostics. The build passing compilation is not an
in-game validation of hook attachment or measured performance.
