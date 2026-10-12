# TBGC — garbage collection and allocation research archive

## Observations from optimizer logs
- Very large managed heaps: earlier snapshots roughly 7.9 GB to 12 GB; heap dropped substantially around observed Gen2 collection.
- Multi-second pauses coincided with Gen2 collections and player-loop phases. Sample freeze 2746.9 ms, heapBeforeFrame ~12072 MB, heapAfterFrame ~7734 MB.
- Unity reported incremental GC disabled on the tested runtime, so the existing incremental-GC smoother generally deferred to vanilla behavior rather than enabling unsupported behavior.
- Saving and preview/bulk construction contributed allocation spikes; not all observed memory growth is a leak.
- Earlier instrumentation included managed-heap sampler, per-thread allocation tracking, freeze correlation, and GC generation counters; retired for quiet release.

## Ideas / not implemented
- Reduce allocations at known source hot loops, rather than forcing GC timing.
- Measure retained references after repeated loads before labeling persistent heap growth a leak.
- Examine large temporary serialization buffers and object pools; beware high RAM cost.
- A broad manual collection or moving Unity allocations across threads is not a validated fix.

## Status
Research archive; no active GC behavior changes are introduced on this branch. Keep these notes separate from performance claims requiring new controlled logs.
