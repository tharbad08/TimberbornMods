# Startup dynamic-assembly guard (retained in TBSave research)

Source: `BenchmarkAndOptimizerV11Port/BinditoConfiguratorDynamicAssemblyGuard.cs`.

This is **not a save serialization or loading-performance optimization**. It protects the game-startup Bindito and SpecTypeCache discovery paths from incomplete MonoMod/Harmony-generated dynamic assemblies that cannot safely enumerate types. It has been removed from TB Optimizer on request and preserved on this research branch so the implementation and prior diagnostics are not lost.

Do not include this guard in a save-only release without independently verifying its initialization/assembly scope. Its related investigations are `STARTUP-SAFETY-1.1.88.md`, `BINDITO-DMD-STARTUP-v1.1.89.md`, and `DMD-SELECTOR-SIGNATURE-1.1.91.md`.

For new save tests, keep compression/serialization behavior unchanged until source-level timing validates safe separation of snapshot capture and archive compression.
