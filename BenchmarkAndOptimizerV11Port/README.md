# TB Optimizer — Timberborn 1.1

This branch contains **only individually switchable gameplay optimizations**. It does not run benchmarks, write CSV/freeze logs or throttle any tick/update systems. It also does not patch the startup dynamic-assembly discovery paths.

- Complete final optimization list, settings keys, per-file implementation map and PR guidance: [OPTIMIZATIONS.md](OPTIMIZATIONS.md).
- **Do-not-repeat register** of crashes, failed optimizations, rejected shortcuts, inconclusive diagnostics and safe alternatives: [FAILED_EXPERIMENTS.md](FAILED_EXPERIMENTS.md).
- SWC reference performance patch (1.1.36.1) and separate harvest-safety guard (1.1.36.2): [implementation map, port status, and compatibility constraints](OPTIMIZATIONS.md#simple-workshop-control-swc--documented-port-not-in-this-branchs-release). **Documentation only on this branch**; work in progress on [draft PR #4](https://github.com/tharbad08/TimberbornMods/pull/4).
- The separate [BenchmarkAndOptimizerV11Port](https://github.com/tharbad08/TimberbornMods/tree/chatgpt/BenchmarkAndOptimizerV11Port) branch owns interval controls, protected tick policy, CSV benchmarks and diagnostic profiling.
- The [TBSave](https://github.com/tharbad08/TimberbornMods/tree/chatgpt/TBSave) research branch holds startup crash-guard code and save/load profiling research; the startup guard is not itself a save optimization.

Restart after changing an individual feature toggle. No global dispatcher hooks, broad profiling or per-system sliders are installed by this mod.
