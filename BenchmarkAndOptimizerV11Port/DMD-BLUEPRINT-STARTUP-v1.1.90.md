# v1.1.90: Bindito and blueprint type-discovery DMD crash guards

## Evidence from the new user log

`Player(20261011-174105).log` starts Benchmark & Optimizer v1.1.89
at 20:39:43–44. The earlier failure in
`Bindito.Core.Internal.ContainerDefinition.FindConfigurators`
does not appear. Instead, the first uncaught exception at
2026-10-11 20:39:57 was:

```
ReflectionTypeLoadException: Type 'DMD<>?984211908' is not finished
System.Reflection.Emit.AssemblyBuilder.GetTypes
System.Reflection.Assembly.GetTypes
Timberborn.BlueprintSystem.SpecTypeCache+<>c.<Create>b__5_0(Assembly)
Timberborn.BlueprintSystem.SpecTypeCache.Create
Timberborn.BlueprintSystem.BlueprintDeserializer..ctor
```

Nested errors reporting failure to construct `SpecService`, audio services,
`PanelStack`, and analytics UI are consequences of dependency-injection
failing on `BlueprintDeserializer`; they are not independent failures.

Source inspection of decompiled `SpecTypeCache.Create` confirms it
enumerates `AppDomain.CurrentDomain.GetAssemblies()`, calls
`Assembly.GetTypes()` on each assembly, filters for concrete
`ComponentSpec` subclasses, then builds a frozen spec map.
A MonoMod dynamic AssemblyBuilder can throw when scanned before its
emitted DMD type is finished. This is not evidence of save corruption.

## Fix and compatibility rules

- Retain v1.1.89 Bindito guard.
- Also patch the exact `SpecTypeCache+<>c.<Create>b__...`
  `(System.Reflection.Assembly)->System.Type[]` selector.
- The Harmony transpiler requires exactly one `Assembly.GetTypes()`
  instruction with matching stack signature, otherwise it fails closed.
- Replace that call with `GetSafeConfiguratorTypes(Assembly)`:
  `Type.EmptyTypes` for `Assembly.IsDynamic == true`, vanilla
  `assembly.GetTypes()` otherwise.
- This does **not** patch AppDomain, reflection globally, the blueprint
  creation pipeline, concrete mod assemblies, spec aliases, or normal
  failed reflection errors from compiled game/mod DLLs.
- This avoids catching/ignoring ReflectionTypeLoadException on a normal
  mod DLL, which could mask a genuinely broken mod.

Only two crash-proven compiler selectors are altered. Patching one
site does not fix arbitrary third-party assembly scanners, so any
new distinct scan failure should be diagnosed from its exact trace.

## Validation once

Check the optimizer session log for both

```
BINDITO.DMD.GUARD installed
BLUEPRINT.DMD.GUARD installed
```

and (if dynamic assemblies were encountered)

```
DMD.GUARD excluded dynamic assembly during type discovery
```

Then verify the game reaches its main menu and can load the late colony.
The existing load/save and UI-only FPS probes remain unchanged.
Source compilation is not a substitute for in-game verification.
