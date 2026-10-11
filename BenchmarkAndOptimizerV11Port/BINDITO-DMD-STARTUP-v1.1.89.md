# v1.1.89 — Bindito unfinished MonoMod assembly fix

## Crash evidence

Player(20261011-171359).log (v1.1.87): first uncaught
ReflectionTypeLoadException on 2026-10-11 20:11:05:
\`Type 'DMD<>?-601843978' is not finished\`.
Player(20261011-172436).log (v1.1.88): **same exact
failure class at 20:22:56**, with
\`Type 'DMD<>?221846672' is not finished\`.

Both crashes originate:
\`System.Reflection.Emit.AssemblyBuilder.GetTypes\` →
\`Assembly.GetTypes\` →
\`Bindito.Core.Internal.ContainerDefinition+<>c.<FindConfigurators>b__10_0\`
→ Bindito InstallAll → ProjectConfigurator, with follow-on
SceneConfigurator NullReferenceException. No save/colony deserialization
has occurred. The previous load/save handler scan fix did not prevent it.
Log is not sufficient to identify which specific mod produced the
unfinished dynamic DMD assembly.

## Mechanism

Bindito enumerates all loaded assemblies, including transient
dynamically generated MonoMod/Harmony assemblies. An
AssemblyBuilder.GetTypes() called before emission completed throws.
Dynamic DMD assemblies contain no legitimate static
Timberborn [Context] configuration classes.

At mod initialization, v1.1.89 attempts an exact Harmony transpiler
on the compiler-generated lambda used by FindConfigurators.
The transpiler replaces exactly ONE
\`Assembly.GetTypes()\` call with
\`BinditoConfiguratorDynamicAssemblyGuard.GetSafeConfiguratorTypes(Assembly)\`.
The helper returns Type.EmptyTypes if assembly.IsDynamic; otherwise
it forwards to assembly.GetTypes, preserving vanilla exceptions.

**No global changes to Assembly.GetTypes, GetAssemblies, or scene
startup semantics.** The normal configurator type filters,
context checks and original static-assembly enumerations remain
unchanged. If the target lambda is absent, ambiguous, or its
GetTypes instruction count is not exactly one, the patch logs why
and stays disabled; it never replaces unrelated IL.

## Single startup test

1. Replace v1.1.88 with v1.1.89; use only one local optimizer DLL.
2. Check optimizer-v11.log during startup for
   \`BINDITO.DMD.GUARD installed\`, and (optionally)
   \`BINDITO.DMD.GUARD skipped dynamic\`.
3. Confirm Main Menu appears, then load the late-game save.
4. UI-only panel/FPS measurements and curated LOAD/SAVE probe
   remain active once in game. Provide optimizer-v11.log and
   optimizer-v11-freezes.log for analysis.
5. If it still crashes, supply Player.log. Do **not** assume
   this patch fixes arbitrary separate MonoMod problems.

The GitHub CI build validates compilation/packaging only;
actual modded Unity/Proton startup is not reproduced in CI.
