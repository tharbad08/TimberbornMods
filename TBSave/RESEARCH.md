# TBSave — save-performance research archive

## Findings from previous optimizer tests
- Game saving freezes the simulation while capturing/serializing authoritative state. The requested idea was to split state capture (paused) from later ZIP compression (potentially asynchronous).
- Timberborn saves appear compressed/ZIP-like; **do not infer** that compression is automatically safe to move off-thread.
- Observed GameSaverUnityAdapter activity around 5.55 seconds during one 6.18-second freeze; two older saves measured 5.27 and 7.88 seconds overall. Results vary by workload.
- An attempted Harmony hook directly on `GameSaver.Save` failed with `Incorrect code generation for exception block`; the hook was disabled. The SaveWriter and GameSaverUnityAdapter observational probes worked.
- User explicitly asked to postpone changes to saving until discussing with other modders.

## Design idea, NOT implemented
1. Identify a consistent snapshot boundary and what objects must remain on the Unity main thread.
2. Capture a complete, immutable serialization payload while paused.
3. Resume simulation only after capture is proven independent of live game objects.
4. Serialize/compress/write on a background thread *only* if snapshot consistency and serializer thread-safety are proven.
5. Handle retries, memory pressure, cancellation, partial files and atomic replacement.

## Scope/status
Research only. Do not ship gameplay or save hooks from this branch until source-level verification and explicit user approval. This document captures previously discussed ideas, not a verified implementation.

## Instrumented save path (October 11–12, 2026)

The working observational probes broke a save into these inclusive stages:
`SaveWriter.ZIPArchiveAndEntries` → `WorldEntryWriter.GenerateAndWrite` →
`WorldSerializer.WriteEntry` / `JSON.BuildAndWrite`; before writing,
`WorldFactory.CreateWorld` included singleton and entity snapshot collection.

The following are **observed samples**, not universal timings. Nested stages
overlap, so DO NOT sum their durations.

| Log | ZIP inclusive | Entry generation | WriteEntry | JSON | CreateWorld |
|---|---:|---:|---:|---:|---:|
| optimizer-v11(20261011-195527).log | 5,273.6 ms | 4,213.8 ms | 2,180.6 ms | 2,081.4 ms | 2,032.8 ms |
| optimizer-v11 (2).log, 23:33 | 5,468.6 ms | 4,413.8 ms | 2,260.1 ms | 2,157.6 ms | not extracted |
| optimizer-v11 (2).log, 23:37 | 8,472.4 ms | 7,416.3 ms | 2,402.4 ms | not extracted | not extracted |
| optimizer-v11(20261011-225839).log, 01:52 | 8,301.1 ms | 7,262.0 ms | 5,149.6 ms | 5,046.2 ms | 2,112.0 ms |

The October 12 sample additionally broke down `WorldFactory.CreateWorld`:
`SaveSingletons` 1,558.9 ms, `SaveEntities` 552.5 ms. Separate save callback
`GameSaverUnityAdapter.LateUpdate` ran 8,375.5 ms with one Gen2 collection and
an observed heap change from 11,853.2 MB to 9,044.7 MB; the surrounding frame
reached 8,870.8 ms. That callback measurement does not isolate disk I/O.

### Diagnostic limitations
- `SaveWriter.ZIPArchiveAndEntries` spans both entry-writing and ZIP
  archive finalization. Calling the full inclusive time "compression time"
  would overstate compression cost.
- `GameSaver.Save` direct Harmony instrumentation triggered **incorrect code
  generation for exception block** and was disabled. Do not retry blind
  Harmony prefix/postfix/finalizer patches on that method.
- The save writer and `GameSaverUnityAdapter` probes worked without modifying
  serialization. No corruption was proven, but no post-edit serialization
  compatibility validation exists.
- Build 1.1.101 retired all probes from the gameplay optimizer.

### Candidate next investigations (not implementation)
1. Verify exact byte format and ZIP streaming mode from *source/decompilation*,
   not the assumption that saves are "zipped".
2. Quantify duration of snapshot creation vs. serializing JSON vs. compression
   vs. disk flush, with nested measurement boundaries.
3. Determine whether immutable detached data can be created on the Unity main
   thread, and whether compression can operate on it independently.
4. Measure extra RAM needed by a complete snapshot: the heap already reached
   ~12 GB before heavy collections on the tested colony.
5. Verify atomic rename and shutdown/cancel behavior, save game versions,
   mods' custom serialization, autosave progress UI, and failure recovery.
6. Ask other mod authors before attempting the proposed save pipeline rewrite.

## Provenance / known log filenames
- `optimizer-v11(20261011-195527).log`
- `optimizer-v11 (1)(20261011-202714).log`
- `optimizer-v11 (1)(20261011-204437).log`
- `optimizer-v11 (2).log`
- `optimizer-v11(20261011-225839).log`
- Older optimizer-v11-freezes logs captured `GameSaverUnityAdapter` pauses.

Raw log attachments remain in the Timberborn ChatGPT project; this branch
archives distilled findings, *not* the raw private game logs or save files.
