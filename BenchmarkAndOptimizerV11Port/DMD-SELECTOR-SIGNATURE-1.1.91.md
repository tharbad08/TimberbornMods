# v1.1.91 — correct compiler-generated scanner return type

## Evidence

User's `Player(20261011-174837).log` confirms v1.1.90 loaded
but crashed again during ProjectConfigurator discovery at 20:47:37:

```
ReflectionTypeLoadException: Type 'DMD<>?-945929992' is not finished
AssemblyBuilder.GetTypes
Assembly.GetTypes
Bindito.Core.Internal.ContainerDefinition+<>c.<FindConfigurators>b__10_0(Assembly)
```

v1.1.90 changed the return-type selection predicate from
`ReturnType != typeof(void)` in the formerly usable Bindito
v1.1.89 patch to `ReturnType == typeof(Type[])`, which is too strict.

## Root of the regression

For source similar to:

```csharp
assemblies.SelectMany((Assembly assembly) => assembly.GetTypes())
```

the generated lambda's delegate is typically
`Func<Assembly, IEnumerable<Type>>`; the generated selector can
have **declared return type IEnumerable<Type>**, whereas the inner
`Assembly.GetTypes` call yields **Type[]**. Our v1.1.90 candidate
selection accidentally conflated these two signatures, causing
the guard to miss the actual target (with only a message in
optimizer-v11.log; Player.log contains no such messages).

## Correction

- Find nested `<FindConfigurators>b__...` and
  `<Create>b__...` selectors with EXACTLY one Assembly
  parameter and a return type assignable to `IEnumerable<Type>`
  (including `Type[]`).
- Retain strict transpiler validation: exactly one call to
  `System.Reflection.Assembly.GetTypes(): Type[]`; fail closed
  if the implementation changes or has ambiguous instructions.
- Runtime logs identify each exact target return type and the
  transpiler verification. On discovery failure log candidates'
  actual return types, not merely a boolean.
- The replacement still filters ONLY dynamic assemblies
  (`assembly.IsDynamic`). Original `GetTypes` executes on
  static mod/game DLLs, preserving concrete `ComponentSpec` and
  mod configurator discovery.
- Do not globally suppress reflection errors or modify T3MP,
  More Mod Logs, save/load data, UI-only rendering, game ticks.

## Validation

Read **optimizer-v11.log** startup for

```
DMD.GUARD transpiler verified: replaced exactly one ...
BINDITO.DMD.GUARD installed: ... selectorReturns=...
BLUEPRINT.DMD.GUARD installed: ... selectorReturns=...
DMD.GUARD excluded dynamic assembly during type discovery ...
```

Note: the last line depends on encountering a dynamic assembly, so
its absence alone is not evidence of failure. Player.log by itself
cannot confirm guard installation. Build success does not prove
the actual in-game selector shape or resolve other patch conflicts.
