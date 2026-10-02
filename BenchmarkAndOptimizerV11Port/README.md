# Benchmark & Optimizer — Timberborn v1.1 port

This port preserves the original per-system frequency control while avoiding U7-only private layout dependencies where possible.

- `optimizer-v11.json` is generated on first launch.
- Interval `1` = vanilla frequency.
- Interval `20` = run once, then skip the next 19 dispatcher calls.
- The config is reloaded live (default every 2 seconds).
- `optimizer-v11-discovered.txt` lists observed tick/update types to make configuration easier.
- Set `BenchmarkSeconds` above 0 to write a CSV benchmark for that many real seconds after game load.
- Compatibility failures fall back to vanilla dispatcher behavior instead of intentionally suppressing game updates.

The runtime package requires the normal Timberborn Harmony mod.

- Bober's Laws of Motion / TimberPhysics: caps physics catch-up to 4 fixed substeps per update. Normal 0.02 s physics cadence is unchanged when the game keeps up; excess backlog is dropped to prevent runaway catch-up stalls.
