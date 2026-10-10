# Bulk construction, EBR and preview review — v1.1.83

## Verified against source and logs

- **Timberborn 1.1 running signatures** from the supplied v1.1.82 log:
  `List<object> BaseInstantiator.InstantiateComponents(Blueprint, string, IReadOnlyList<object>, ImmutableArray<Type>)`.
  `object InstantiateComponent(Blueprint, Type)`.
- **T3MP ComponentConstructionPlans** source
  https://github.com/toropippi/Timberborn-More-More-More-Performance-Mod/blob/main/src/T3MP/Loading/ComponentConstructionPlans.cs
  defines an exact four-argument `BeforeComponents` implementation, which
  constructs `List<object>(ComponentCacheService.GetComponentsCount(name))`,
  appends each `initializingComponents` element, then each blueprint component.
  Its Harmony prefix either sets `__result` and returns false or falls back to
  vanilla. Its `CheckCompatibility` rejects ANY other Harmony patch owner on
  the protected methods, including SmartPower's postfix, our preview counter,
  and our spec cache. Thus `_compatible` can be false even with T3MP loaded.
- **SmartPower's CustomizableInstantiator**
  https://github.com/ihsoft/TimberbornMods/blob/master/TimberDev/Utils/CustomizableInstantiator.cs
  has a postfix that mutates `List<object> __result`. Harmony postfixes still
  run when a prefix supplies the original method's result.
- **Historical decompiled Accessible.SetAccesses** in
  https://github.com/millerscout/TimberbornMods/blob/master/ReflectionData/Timberborn.Navigation/Timberborn/Navigation/Accessible.cs
  calls `GetComponents(_accessibleValidators)`, clears/repopulates accesses,
  sets final access and calls `EnableComponent`. This is historical code, not
  proof that the current running DLL has exactly that body. Skipping identical
  coordinates cannot be assumed safe. The v1.1.82 strict EBR no-op guard stays
  fail-closed; its startup warning now explains these possible side effects.
- The preview factory historically creates the first preview eagerly and
  lazily materializes the remaining `Preview[]`. It is unsafe to cap the 899
  previews without changing placement semantics.

## Implemented in v1.1.83

1. **Four-argument 1.1-aware bulk fast path.** Hard checks exact runtime
   signature and exact `GetComponentsCount(string)` method.
2. **Fail-closed live native method IL audit** permits only list creation,
   capacity lookup, `InstantiateComponent`, appends and simple enumeration.
   Any additional call, field write or unrecognized IL disables the shortcut.
3. **Harmony owner review:** T3MP's *documented* earlier prefix is allowed;
   any unrecognized prefix, transpiler or finalizer disables our shortcut.
   Earlier T3MP result is never overwritten. SmartPower and other postfixes
   run after our prefix, preserving their component customization.
4. **Warm immutable-spec plan:** on first use all blueprint `ComponentSpec`
   entries must already have passed vanilla resolution into the weak cache.
   The plan captures only immutable spec references and type order; non-spec
   components continue through the original `InstantiateComponent`
   delegate on EVERY batch. Plans live behind weak blueprint keys, so map
   unload does not keep blueprint instances alive.
5. **Preview speedup:** the same fast path applies to the 899-entry brush.
   The first preview warms immutable specs via native construction; later
   preview instances can reuse the vetted plan while all live components are
   freshly constructed, all initializing entries preserved, and every
   SmartPower postfix still runs.
6. **EBR remains conservative.** Updated startup explanation states why
   full no-op SetAccesses suppression is not justified. No setter behavior
   or EBR scheduling changes.

## Expected startup checks

```
bulk/preview fast path installed (1.1 four arguments): ...
bulk/preview fast path: batches=100, cached immutable specs=..., live native components=...
```

If the IL audit rejects the runtime method, report the exact
`bulk/preview fast path inactive: ...` message, which names the unsupported
operation. Vanilla construction remains unchanged.

## Test matrix

- Enter and exit an UNUSED brush; keep the existing Lazy-pool fast path.
- Place a small brush, then a 899-preview brush and vary shape/rotation.
- Confirm AutoScaffold, EzTube and SmartPower behavior and placement validity.
- Confirm EBR access and demolition still work.
- Supply optimizer + freeze logs for timings and for the count of accelerated
  preview batches. A compiled DLL is **not** in-game validation.

## Exclusions

No save rework, GC cadence changes, Soil Contamination changes, global ticks,
physics patch, extra worker threads, or preview count reduction.
