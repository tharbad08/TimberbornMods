using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

internal static class BlueprintSpecInstantiationCache
{
    // Immutable blueprint Specs are scanned linearly by Blueprint.GetSpec(Type).
    // BaseInstantiator.InstantiateComponent resolves the same Type repeatedly for
    // each entity/preview. Cache ONLY ComponentSpec references from that immutable
    // collection; container-created non-spec components always use vanilla.
    //
    // Weak blueprint keys avoid retaining mod blueprint/spec instances across
    // map unloads. Original execution handles all first-use lookups and errors.
    private sealed class Entry
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> Values = new();
    }

    private static readonly ConditionalWeakTable<object, Entry> Cached = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> SpecTypes = new();
    private static Type? _specBaseType;
    private static bool _enabled;
    private static int _hits;
    private static int _stored;

    public static void Patch(Harmony harmony)
    {
        var instantiator = AccessTools.TypeByName("Timberborn.BaseComponentSystem.BaseInstantiator");
        var blueprint = AccessTools.TypeByName("Timberborn.BlueprintSystem.Blueprint");
        var componentSpec = AccessTools.TypeByName("Timberborn.BlueprintSystem.ComponentSpec");
        if (instantiator is null || blueprint is null || componentSpec is null)
            return;

        var method = AccessTools.Method(instantiator, "InstantiateComponent");
        var getSpec = AccessTools.Method(blueprint, "GetSpec", new[] { typeof(Type) });
        var specs = AccessTools.Property(blueprint, "Specs");
        var parameters = method?.GetParameters();
        if (method is null || getSpec is null || specs is null ||
            specs.SetMethod is not null ||
            !specs.PropertyType.IsGenericType ||
            specs.PropertyType.GetGenericTypeDefinition().FullName !=
                "System.Collections.Immutable.ImmutableArray`1" ||
            parameters is null || parameters.Length != 2 ||
            parameters[0].ParameterType != blueprint ||
            parameters[1].ParameterType != typeof(Type) ||
            method.ReturnType != typeof(object))
        {
            Runtime.Log("blueprint spec lookup cache disabled: incompatible 1.1 method or immutable spec layout");
            return;
        }

        // Never bypass another mod's special Blueprint.GetSpec/InstantiateComponent
        // behavior. Harmony may attach its own hooks to these methods.
        foreach (var candidate in new[] { method, getSpec })
        {
            var info = Harmony.GetPatchInfo(candidate);
            if (info is not null &&
                info.Owners.Any(owner => !owner.StartsWith("tharbad08.BenchmarkAndOptimizerV11",
                    StringComparison.Ordinal)))
            {
                Runtime.Log("blueprint spec lookup cache disabled: external lookup/instantiation patch");
                return;
            }
        }

        _specBaseType = componentSpec;
        try
        {
            harmony.Patch(method,
                prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(BlueprintSpecInstantiationCache), nameof(Prefix)))
                { priority = Priority.Last },
                postfix: new HarmonyMethod(AccessTools.Method(
                    typeof(BlueprintSpecInstantiationCache), nameof(Postfix)))
                { priority = Priority.Last });
            _enabled = true;
            Runtime.Log(
                "blueprint spec lookup cache installed: weak per-blueprint ComponentSpec memoization, " +
                "non-spec dependency injection and entity creation remain vanilla");
        }
        catch (Exception ex)
        {
            _enabled = false;
            Runtime.Log("warning: blueprint spec lookup cache disabled: " +
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    internal static bool IsComponentSpecType(Type type) => _enabled && IsSpecType(type);

    internal static bool TryGetCachedSpec(object blueprint, Type type, out object spec)
    {
        spec = null!;
        return _enabled && Cached.TryGetValue(blueprint, out var entry)
            && entry.Values.TryGetValue(type, out spec!);
    }

    private static bool IsSpecType(Type type)
    {
        var spec = _specBaseType;
        return spec is not null && SpecTypes.GetOrAdd(type, t => spec.IsAssignableFrom(t));
    }

    private static bool Prefix(object __0, Type __1, ref object __result)
    {
        if (!_enabled || __0 is null || __1 is null || !IsSpecType(__1))
            return true;

        if (!Cached.TryGetValue(__0, out var entry) ||
            !entry.Values.TryGetValue(__1, out var existing))
            return true;

        __result = existing;
        _hits++;
        if (_hits == 10000 || _hits == 50000)
            Runtime.Log($"blueprint spec lookup cache: {_hits} redundant immutable-spec searches avoided");
        return false;
    }

    private static void Postfix(object __0, Type __1, object __result)
    {
        if (!_enabled || __0 is null || __1 is null || __result is null ||
            !IsSpecType(__1))
            return;

        var entry = Cached.GetValue(__0, _ => new Entry());
        if (entry.Values.TryAdd(__1, __result))
            _stored++;
    }
}
