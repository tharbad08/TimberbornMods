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
