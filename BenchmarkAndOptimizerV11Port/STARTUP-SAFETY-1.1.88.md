# Startup safety v1.1.88

## Evidence

Player(20261011-171359).log shows the app loaded optimizer v1.1.87
and numerous mods at approximately 20:10:44-20:10:54. At 20:11:05,
Bindito's ProjectConfigurator / ContainerDefinition.FindConfigurators
threw ReflectionTypeLoadException when an in-process MonoMod dynamic
assembly contained unfinished type `DMD<>?-601843978`. A follow-on
NullReferenceException occurred while creating the scene container.

The log establishes a dynamic assembly discovery error, but does
**not** identify which mod generated that DMD, nor prove our mod was
its origin. It occurred while Bindito called Assembly.GetTypes over
loaded assemblies. The previous optimizer's v1.1.86 load/save profiler
used AppDomain.CurrentDomain.GetAssemblies() and GetTypes() across
all assemblies, then applied up to 650 Harmony modifications **per
interface**, including ILoadableSingleton, INonSingletonLoader,
IPostLoadableSingleton, INonSingletonPostLoader, ISaveEntryWriter.
That broad startup patching created a large number of new
dynamically generated methods and unnecessarily increased exposure
to the unfinished-DMD failure. It is the most direct recent risk
introduced by our changes and is removed as precaution.

## Changes

- Removed all AddInterfaceHandlers calls and the global assembly
  discovery/patching helper.
- Removed Harmony patch on compiler-generated
  SceneLoader.LoadSceneCoroutine.MoveNext. Unity coroutine and
  SceneManager calls are still captured as time between macro stages.
- Retained ~30 targeted load and save method timers, including
  ValidatingGameLoader, GameSceneLoader, GameLoader,
  GameSaveDeserializer, JSON read/build, Serializer,
  SingletonLifecycleService.LoadAll and major phases,
  WorldEntitiesLoader, EntitiesLoader, GameInitializer,
  GameSaver.Save, SaveWriter.ZIP, WorldFactory, WriteJson and
  WorldEntryWriter.
- Logs `LOADSAVE.PROBE startup-safe v1.1.88 installed=N skipped=M`.
- Preserves scene-request-to-ShowPrimaryUI elapsed wall time and
  phase gaps, plus save total, snapshot/JSON/ZIP timing.
- No changes to UI-only rendering toggle, its camera mask or FPS
  sampling, other gameplay optimizers, Soil Contamination, save data,
  global scheduler, preview, T3MP or SmartPower.

## Remaining limitations

- Per-mod singleton load-handler and per-save-entry attribution
  cannot be identified in this safer variant. Large
  `Lifecycle.LoadSingletons` or `SaveWriter` numbers can help
  localize the next investigation without risking unbounded hooks.
- The exact DMD origin remains unproven. If the same crash persists,
  compare with v1.1.85 and isolate other mods patching
  Bindito.ContainerDefinition, especially T3MP and More Mod Logs,
  instead of assuming this precaution fixed all causes.
- Build passing is not in-game startup verification.

## Validation in one run

1. Install the new ZIP, replacing the previous optimizer DLL.
2. Confirm project/main-menu load and save selection succeed.
3. Confirm `LOADSAVE.PROBE startup-safe v1.1.88 ...` and
   `UIONLY service loaded` in optimizer-v11.log.
4. Load the late colony once and save once, then share
   optimizer-v11.log, optimizer-v11-freezes.log and Player.log
   only if a new startup error occurs.
