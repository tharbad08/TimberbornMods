using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Narrow runtime guard for the two observed Timberborn startup paths that
/// enumerate AppDomain assemblies through Assembly.GetTypes():
///  - Bindito ContainerDefinition.FindConfigurators (v1.1.87/88 crash)
///  - Timberborn.BlueprintSystem.SpecTypeCache.Create (v1.1.89 crash)
///
/// MonoMod/Harmony dynamic AssemblyBuilders may contain unfinished DMD types.
/// Normal game/mod DLLs are NOT filtered, and this does not globally alter
/// Assembly.GetTypes, assembly discovery, or blueprint/configurator selection.
/// </summary>
internal static class BinditoConfiguratorDynamicAssemblyGuard
{
    private static int _dynamicAssembliesSkipped;
    private static readonly MethodInfo? SafeTypesMethod =
        AccessTools.Method(typeof(BinditoConfiguratorDynamicAssemblyGuard),
            nameof(GetSafeConfiguratorTypes));

    public static void Patch(Harmony harmony)
    {
        // Catch failures separately: one incompatible site must not prevent
        // us from protecting the other verified crash site.
        PatchSite(harmony, "Bindito.Core.Internal.ContainerDefinition",
            "FindConfigurators", "BINDITO.DMD.GUARD");
        PatchSite(harmony, "Timberborn.BlueprintSystem.SpecTypeCache",
            "Create", "BLUEPRINT.DMD.GUARD");
    }

    private static void PatchSite(Harmony harmony, string typeName,
        string selectorName, string logPrefix)
    {
        try
        {
            var owner = AccessTools.TypeByName(typeName);
            if (owner is null)
            {
                Runtime.Log($"{logPrefix} unavailable: {typeName} not found; " +
                    "vanilla method retained");
                return;
            }

            // v1.1.89 matched Bindito's compiler-generated
            // <FindConfigurators>b__10_0(Assembly). v1.1.89's Player log
            // identifies the other site as SpecTypeCache+<>c.<Create>b__5_0.
            // SelectMany selectors are compiler-generated for a
            // Func<Assembly, IEnumerable<Type>> delegate. Their declared
            // return type is often IEnumerable<Type>, NOT Type[], even
            // though Assembly.GetTypes returns Type[]. Our v1.1.90 check
            // incorrectly required Type[] and rejected the actual method.
            // Match either (and other interface-compatible variants),
            // while keeping the strict Assembly argument + IL proof.
            var selector = "<" + selectorName + ">b__";
            var candidates = owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(t => t.GetMethods(BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.Static |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.Name.StartsWith(selector, StringComparison.Ordinal)
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(Assembly)
                    && typeof(IEnumerable<Type>).IsAssignableFrom(m.ReturnType)
                    && !m.ContainsGenericParameters)
                .ToArray();

            if (candidates.Length != 1 || SafeTypesMethod is null)
            {
                var nameMatches = owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                    .SelectMany(t => t.GetMethods(BindingFlags.Public |
                        BindingFlags.NonPublic | BindingFlags.Static |
                        BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    .Where(m => m.Name.StartsWith(selector, StringComparison.Ordinal) &&
                        m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType == typeof(Assembly))
                    .Select(m => m.DeclaringType?.Name + "." + m.Name +
                        " returns " + (m.ReturnType.FullName ?? m.ReturnType.Name))
                    .Take(8).ToArray();
                Runtime.Log($"{logPrefix} unavailable: expected exactly one " +
                    $"{selector}(Assembly)->IEnumerable<Type> compatible selector, " +
                    $"matches={candidates.Length}; nameCandidates=[" +
                    string.Join("; ", nameMatches) + "]; no patch installed");
                return;
            }

            var target = candidates[0];
            harmony.Patch(target,
                transpiler: new HarmonyMethod(AccessTools.Method(
                    typeof(BinditoConfiguratorDynamicAssemblyGuard),
                    nameof(TranspileGetTypes))) { priority = Priority.First });
            Runtime.Log($"{logPrefix} installed: " +
                $"{target.DeclaringType?.FullName}.{target.Name}; " +
                $"selectorReturns={target.ReturnType.FullName}; " +
                "Assembly.GetTypes() replaced with guarded equivalent");
        }
        catch (Exception ex)
        {
            Runtime.Log($"{logPrefix} unavailable: {ex.GetType().Name}: " +
                ex.Message + "; original selector retained");
        }
    }

    private static IEnumerable<CodeInstruction> TranspileGetTypes(
        IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        var matches = list.Where(i =>
            (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) &&
            i.operand is MethodInfo method &&
            method.Name == nameof(Assembly.GetTypes) &&
            method.DeclaringType == typeof(Assembly) &&
            method.GetParameters().Length == 0 &&
            method.ReturnType == typeof(Type[])).ToArray();

        // If Timberborn's implementation or other Harmony transpilers have
        // changed this selector, never rewrite ambiguous IL.
        if (matches.Length != 1 || SafeTypesMethod is null)
            throw new InvalidOperationException(
                "Assembly.GetTypes call count=" + matches.Length +
                "; refusing ambiguous or missing discovery method rewrite");

        var instruction = matches[0];
        // Existing stack: Assembly receiver -> Type[] result.
        // Static wrapper has identical stack signature, and preserves labels,
        // exception blocks and all other IL in the selector.
        instruction.opcode = OpCodes.Call;
        instruction.operand = SafeTypesMethod;
        Runtime.Log("DMD.GUARD transpiler verified: replaced exactly one " +
            "Assembly.GetTypes() instruction; preserved all other IL");
        return list;
    }

    public static Type[] GetSafeConfiguratorTypes(Assembly assembly)
    {
        // No emitted MonoMod DMD assembly contains a real compiled
        // ComponentSpec or static mod configurator; skipping it does not
        // exclude blueprint specs or dependencies from installed mod DLLs.
        // Guard ONLY dynamic assemblies. All static assembly errors pass
        // through unmodified and remain diagnosable.
        if (assembly.IsDynamic)
        {
            int n = Interlocked.Increment(ref _dynamicAssembliesSkipped);
            if (n == 1 || n == 100)
                Runtime.Log($"DMD.GUARD excluded dynamic assembly during " +
                    $"type discovery count={n} name={assembly.GetName().Name}; " +
                    "static game/mod DLLs retained");
            return Type.EmptyTypes;
        }

        return assembly.GetTypes();
    }
}
