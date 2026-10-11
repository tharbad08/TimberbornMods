using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Narrow workaround for Bindito's reflection scan encountering an unfinished
/// MonoMod DynamicMethodDefinition DMD type in an AssemblyBuilder.GetTypes().
///
/// Only changes the Assembly.GetTypes() call inside the compiler-generated
/// ContainerDefinition.FindConfigurators lambda, NOT AppDomain.GetAssemblies,
/// NOT Assembly.GetTypes globally, and NOT the actual configurator filters.
/// Bindito needs named mod assemblies, not transient emitted DMD assemblies.
/// </summary>
internal static class BinditoConfiguratorDynamicAssemblyGuard
{
    private static int _dynamicAssembliesSkipped;
    private static bool _installed;
    private static readonly MethodInfo? SafeTypesMethod =
        AccessTools.Method(typeof(BinditoConfiguratorDynamicAssemblyGuard),
            nameof(GetSafeConfiguratorTypes));

    public static void Patch(Harmony harmony)
    {
        try
        {
            var owner = AccessTools.TypeByName(
                "Bindito.Core.Internal.ContainerDefinition");
            if (owner is null)
            {
                Runtime.Log("BINDITO.DMD.GUARD unavailable: ContainerDefinition not found");
                return;
            }

            var candidates = owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic)
                .SelectMany(t => t.GetMethods(BindingFlags.Public |
                    BindingFlags.NonPublic | BindingFlags.Static |
                    BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.Name.Contains("FindConfigurators", StringComparison.Ordinal)
                    && m.GetParameters().Length == 1
                    && m.GetParameters()[0].ParameterType == typeof(Assembly)
                    && m.ReturnType != typeof(void)
                    && !m.ContainsGenericParameters)
                .ToArray();

            if (candidates.Length != 1 || SafeTypesMethod is null)
            {
                Runtime.Log("BINDITO.DMD.GUARD unavailable: expected exactly one " +
                    "FindConfigurators lambda(Assembly); matches=" + candidates.Length +
                    "; vanilla Bindito unchanged");
                return;
            }

            // Reject unexpected method IL instead of replacing arbitrary
            // compiler-generated/third-party behavior. Harmony transpiler
            // itself requires one Assembly.GetTypes call.
            var target = candidates[0];
            harmony.Patch(target, transpiler: new HarmonyMethod(
                AccessTools.Method(typeof(BinditoConfiguratorDynamicAssemblyGuard),
                    nameof(TranspileGetTypes))) { priority = Priority.First });

            _installed = true;
            Runtime.Log($"BINDITO.DMD.GUARD installed: {target.DeclaringType?.FullName}." +
                $"{target.Name}; only Assembly.GetTypes uses safe dynamic-assembly filter");
        }
        catch (Exception ex)
        {
            Runtime.Log("BINDITO.DMD.GUARD patch unavailable: " +
                ex.GetType().Name + ": " + ex.Message +
                "; original game behavior retained");
        }
    }

    private static IEnumerable<CodeInstruction> TranspileGetTypes(
        IEnumerable<CodeInstruction> instructions)
    {
        var list = instructions.ToList();
        var matches = list.Where(i =>
            (i.opcode == OpCodes.Call || i.opcode == OpCodes.Callvirt) &&
            i.operand is MethodInfo m &&
            m.Name == nameof(Assembly.GetTypes) &&
            m.DeclaringType == typeof(Assembly) &&
            m.GetParameters().Length == 0 &&
            m.ReturnType == typeof(Type[])).ToArray();
        if (matches.Length != 1 || SafeTypesMethod is null)
            throw new InvalidOperationException(
                "Bindito configurator selector IL changed: Assembly.GetTypes call count=" +
                matches.Length + "; refusing unsafe rewrite");

        foreach (var item in matches)
        {
            // Original callvirt consumes Assembly, wrapper static method
            // consumes the exact same Assembly argument, returns Type[].
            // No other IL or exception handling is modified.
            item.opcode = OpCodes.Call;
            item.operand = SafeTypesMethod;
        }
        return list;
    }

    public static Type[] GetSafeConfiguratorTypes(Assembly assembly)
    {
        // Emitted assemblies contain Harmony/MonoMod temporary methods and
        // no legitimate compile-time [Context] configurators. Calling
        // AssemblyBuilder.GetTypes can throw "DMD<>? is not finished".
        // Normal game/mod assemblies still use vanilla GetTypes(), and
        // their real reflection errors are not swallowed.
        if (assembly.IsDynamic)
        {
            if (Interlocked.Increment(ref _dynamicAssembliesSkipped) == 1)
                Runtime.Log("BINDITO.DMD.GUARD skipped dynamic MonoMod/Harmony " +
                    "assemblies during configurator discovery; first=" +
                    assembly.GetName().Name + "; static mod assemblies unchanged");
            return Type.EmptyTypes;
        }

        return assembly.GetTypes();
    }
}
