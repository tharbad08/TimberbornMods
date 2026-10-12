# Benchmark & Optimizer — isolated Timberborn v1.1 port

This branch is based on `chatgpt/BenchmarkAndOptimizerV11Port`. It retains only the original benchmark/CSV profiler and opt-in interval controls. It is **not** the TB Optimizer gameplay mod.

- **Per-system CSV benchmark:** start the 120-second sampling session from Mod Settings. Results `optimizer-v11-benchmark-*.csv` contain type, mod origin, total time, invocation count, average/min/max duration. TimberPhysics has observational StepAll/Physics.Simulate timing when installed.
- **Frame/GC CSV profile:** the same session writes `optimizer-v11-frames-*.csv` with ten-frame-average FPS/frame-time plus GC generation counts and managed heap. FPS is actual rendered-frame cadence, **not** simulation speed; no game-speed metric is inferred.
- **Update intervals:** per-system controls default to 1 (vanilla). All simulation-critical/global schedulers are pinned to interval 1. The benchmark temporarily removes interval patches to time unthrottled gameplay and restores them after completion.
- **No gameplay optimizations** (BFR, EBR, preview, fauna, soil, SuperCursor, TimberPhysics catch-up/collider, GC tweaks), no startup guard or save serialization patches.
- **No always-on tracing:** profiler hooks are installed only during a requested benchmark. The full historical diagnostic and experiment sources remain on the original parent branch; heavy legacy profilers are not installed in the isolated branch.

The benchmark's existing dispatcher instrumentation calls original tick/update methods via validated reflected dispatch. In-game verification is still required even if GitHub Actions succeeds.

## Verified source ownership

- [TB-Optimizer](https://github.com/tharbad08/TimberbornMods/tree/TB-Optimizer/BenchmarkAndOptimizerV11Port/Optimizations): gameplay fixes are split into per-mod files there.
- [TBSave](https://github.com/tharbad08/TimberbornMods/tree/chatgpt/TBSave): save/loading investigations and the Bindito generated-assembly startup guard are preserved there. They are research snapshots, not independently verified isolated save-only binaries.
- [TBGC](https://github.com/tharbad08/TimberbornMods/tree/chatgpt/TBGC): GC investigation/prototypes are preserved there; this is not proof of an independent live GC optimization.
- [EBR-Optimizations](https://github.com/tharbad08/TimberbornMods/tree/chatgpt/EBR-Optimizations): legacy EBR-specific experiments and profiling remain preserved; active EBR performance fix is in TB-Optimizer.

See [TICK_SAFETY.md](TICK_SAFETY.md) for protected systems and discovered failure cases.
