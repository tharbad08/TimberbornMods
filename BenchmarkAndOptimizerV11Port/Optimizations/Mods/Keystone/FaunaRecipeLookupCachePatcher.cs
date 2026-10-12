using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

internal static class FaunaRecipeLookupCachePatcher
{
    private static readonly ConditionalWeakTable<object, Dictionary<string, object>>
        CacheByDrainer = new();

    private static bool _installed;

    public static void Patch(Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        try
        {
            var type = AccessTools.TypeByName("Keystone.Mod.Fauna.FaunaSpawnDrainer");
            var target = type?
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "FindRecipeForBlueprint" &&
                    !method.ReturnType.IsValueType &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(string));

            if (target is null)
            {
                Runtime.Log(
                    "fauna recipe lookup cache not installed: FindRecipeForBlueprint not found");
                return;
            }

            var prefixDefinition = AccessTools.Method(
                typeof(FaunaRecipeLookupCachePatcher),
                nameof(Prefix));
            var postfixDefinition = AccessTools.Method(
                typeof(FaunaRecipeLookupCachePatcher),
                nameof(Postfix));

            if (prefixDefinition is null || postfixDefinition is null)
            {
                Runtime.Log(
                    "warning: fauna recipe lookup cache patch methods not found");
                return;
            }

            var prefix = prefixDefinition.MakeGenericMethod(target.ReturnType);
            var postfix = postfixDefinition.MakeGenericMethod(target.ReturnType);

            harmony.Patch(
                target,
                prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                postfix: new HarmonyMethod(postfix) { priority = Priority.Last });

            _installed = true;
            Runtime.Log(
                "Keystone fauna recipe lookup cache installed: successful " +
                "blueprint->ClassERecipe resolutions are reused per drainer instance");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: fauna recipe lookup cache installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool Prefix<T>(
        object __instance,
        string blueprintName,
        ref T __result)
        where T : class
    {
        if (Runtime.IsBenchmarking || __instance is null || blueprintName is null)
        {
            return true;
        }

        var cache = CacheByDrainer.GetOrCreateValue(__instance);
        if (!cache.TryGetValue(blueprintName, out var cached))
        {
            return true;
        }

        __result = (T)cached;
        return false;
    }

    private static void Postfix<T>(
        object __instance,
        string blueprintName,
        T __result)
        where T : class
    {
        if (Runtime.IsBenchmarking ||
            __instance is null ||
            blueprintName is null ||
            __result is null)
        {
            return;
        }

        CacheByDrainer.GetOrCreateValue(__instance)[blueprintName] = __result;
    }
}
