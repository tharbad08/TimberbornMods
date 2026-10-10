# EBR / GC review — 2026-10-10, v1.1.79

## Evidence

Inputs: optimizer-v11(20261010-204058).log and
optimizer-v11-freezes(20261010-204058).log.

- EBR: ~20,656 listeners initially; large batches are deferred at 1,024
  candidates and drained at a maximum of 256 listeners/8ms per frame.
  The sampled large batches completed; no deferred listener failures were
  reported in the severe frames inspected.
- Full Gen2 garbage collections coincided with ~2.7s, 8.5s, 8.3s and 8.3s
  pauses. Unity reports incremental GC disabled.
- GameSaverUnityAdapter save callbacks took ~5.1s and ~8.0s; save
  serialization is explicitly out of scope.
- GC.GetTotalMemory(false) is a global live-heap fallback, **not** a
  per-method allocation counter. The repeated SoilContaminationService
  heap-growth label is not proof that method allocated the bytes.

## Changes and invariants

1. EBR large candidate snapshots rent ArrayPool<object> buffers instead of
   allocating a separate ToArray on every large update.
2. A PendingBatch stores both the rental and exact logical count; rented
   capacity must never extend iteration or pending accounting.
3. References are cleared when the completed batch returns its rental.
4. Exact intersection tests, listener activation, update event delivery,
   FIFO ordering, and 256-listener/8ms budgets remain unchanged.
5. Public Int32 x/y coordinate members use one-time compiled readers;
   unsupported member shapes retain previous reflection + Convert.ToInt32.
6. No save, Soil Contamination, global tick, gameplay, or other-mod hooks
   are changed. No full GC is explicitly requested.
7. GitHub Actions artifacts are one ZIP with
   BenchmarkAndOptimizerV11/version-1.1/manifest.json and
   BenchmarkAndOptimizerV11/version-1.1/Scripts/BenchmarkAndOptimizerV11.dll,
   *not* an outer ZIP containing another ZIP.

## Verification

Check Release compilation and manifest/assembly versions, then in-game:
load a 20k-listener save, trigger small and large road/terrain edits, confirm
all deferred queues reach zero, test builder reach and demolition, and compare
Gen2 pauses and live heap to v1.1.78. Source-level safety review and CI
compilation do not substitute for in-game validation.
