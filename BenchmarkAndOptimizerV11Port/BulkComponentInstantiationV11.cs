using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// 1.1-aware bulk-instantiation shortcut for WARMED, immutable ComponentSpec
/// lookups only. It preserves component ordering, the existing initializing
/// collection, capacity lookup, all non-spec DI, T3MP's earlier prefix and
/// SmartPower's later postfix. The method's live IL is audited before patching.
/// </summary>
internal static class BulkComponentInstantiationFastPath
{
    private sealed class Plan
    {
        internal readonly Type[] Types;
        internal readonly object?[] Specs;
        internal int CachedCount;
        internal Plan(Type[] types, object?[] specs, int cachedCount)
        {
            Types = types; Specs = specs; CachedCount = cachedCount;
        }
    }

    // Weak keys: never retain blueprints across map loads. ImmutableArray<Type>
    // boxes compare based on their internal immutable array identity, which
    // matches T3MP's own plan key semantics.
    private static readonly ConditionalWeakTable<object, Dictionary<object, Plan>> Plans = new();
    private static Func<object, object, Type, object>? _instantiateComponent;
    private static Func<object, string, int>? _capacity;
    private static FieldInfo? _componentCache;
    private static bool _enabled;
    private static int _fastBatches;
    private static long _cachedSpecs, _liveComponents;

    public static void Patch(Harmony harmony)
    {
        var owner = AccessTools.TypeByName("Timberborn.BaseComponentSystem.BaseInstantiator");
        var blueprint = AccessTools.TypeByName("Timberborn.BlueprintSystem.Blueprint");
        if (owner is null || blueprint is null) return;

        var batch = AccessTools.Method(owner, "InstantiateComponents");
        var item = AccessTools.Method(owner, "InstantiateComponent");
        var args = batch?.GetParameters();
        var itemArgs = item?.GetParameters();

        // 1.1 exact four-argument API. Do NOT attempt to simulate 1.0's
        // single initializing component in the 1.1 code path.
        var signatures = batch is not null && item is not null &&
            batch.ReturnType == typeof(List<object>) &&
            args is { Length: 4 } &&
            args[0].ParameterType == blueprint &&
            args[1].ParameterType == typeof(string) &&
            args[2].ParameterType == typeof(IReadOnlyList<object>) &&
            args[3].ParameterType.IsGenericType &&
            args[3].ParameterType.GetGenericTypeDefinition().FullName ==
                "System.Collections.Immutable.ImmutableArray`1" &&
            args[3].ParameterType.GetGenericArguments()[0] == typeof(Type) &&
            item.ReturnType == typeof(object) &&
            itemArgs is { Length: 2 } &&
            itemArgs[0].ParameterType == blueprint &&
            itemArgs[1].ParameterType == typeof(Type);

        if (!signatures)
        {
            Runtime.Log("bulk/preview fast path inactive: unknown InstantiateComponents " +
                "or InstantiateComponent signature; vanilla construction retained");
            return;
        }

        var cacheField = AccessTools.Field(owner, "_componentCacheService");
        var countMethod = cacheField?.FieldType.GetMethod(
            "GetComponentsCount", BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance, null, new[] { typeof(string) }, null);
        if (cacheField is null || countMethod is null ||
            countMethod.ReturnType != typeof(int))
        {
            Runtime.Log("bulk/preview fast path inactive: exact component count helper unavailable");
            return;
        }

        // Detect arbitrary external prefixes/IL replacements. T3MP source
        // inspected: its BeforeComponents prefix either populates __result
        // and returns false OR falls back to the original. We run last,
        // and never overwrite a result that T3MP already supplied.
        var patches = Harmony.GetPatchInfo(batch);
        if (patches is not null &&
            (patches.Prefixes.Any(p =>
                p.owner != harmony.Id &&
                p.owner != "t3mp.load.component-recipes") ||
             patches.Transpilers.Any(p => p.owner != harmony.Id) ||
             patches.Finalizers.Any(p => p.owner != harmony.Id)))
        {
            Runtime.Log("bulk/preview fast path inactive: unreviewed external " +
                "prefix, transpiler or finalizer; original creation retained");
            return;
        }

        // Do not bypass a foreign implementation of the single-item method
        // or Blueprint.GetSpec. Our existing immutable-spec cache patches are
        // part of this optimizer; unknown ones may have custom side effects.
        var getSpec = AccessTools.Method(blueprint, "GetSpec", new[] { typeof(Type) });
        foreach (var method in new[] { item, getSpec })
        {
            if (method is null) return;
            var info = Harmony.GetPatchInfo(method);
            if (info is not null &&
                info.Owners.Any(x => x != harmony.Id))
            {
                Runtime.Log("bulk/preview fast path inactive: external single-item or GetSpec patch");
                return;
            }
        }

        if (!AuditNativeBatch(batch!, item!, countMethod, out var reason))
        {
            Runtime.Log("bulk/preview fast path inactive: game batch IL is " +
                "not a reviewed list-building loop (" + reason + "); vanilla retained");
            return;
        }

        try
        {
            var self = Expression.Parameter(typeof(object), "self");
            var bp = Expression.Parameter(typeof(object), "bp");
            var type = Expression.Parameter(typeof(Type), "type");
            _instantiateComponent =
                Expression.Lambda<Func<object, object, Type, object>>(
                    Expression.Call(Expression.Convert(self, owner), item!,
                        Expression.Convert(bp, blueprint), type),
                    self, bp, type).Compile();

            var cache = Expression.Parameter(typeof(object), "cache");
            var name = Expression.Parameter(typeof(string), "name");
            _capacity = Expression.Lambda<Func<object, string, int>>(
                Expression.Call(Expression.Convert(cache, cacheField.FieldType),
                    countMethod, name), cache, name).Compile();
            _componentCache = cacheField;
            harmony.Patch(batch!, prefix: new HarmonyMethod(AccessTools.Method(
                typeof(BulkComponentInstantiationFastPath), nameof(Prefix)))
            { priority = Priority.Last });
            _enabled = true;
            Runtime.Log("bulk/preview fast path installed (1.1 four arguments): " +
                "verified native list-loop; source compatibility includes T3MP fallback, " +
                "SmartPower postfix, initializing-list enumeration and exact capacity; " +
                "one vanilla/spec-warming batch per immutable blueprint before acceleration");
        }
        catch (Exception ex)
        {
            _enabled = false;
            Runtime.Log("bulk/preview fast path inactive: patch/compile failed: " +
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool Prefix(object __instance, object __0, string __1,
        IReadOnlyList<object>? __2, object __3, ref object __result)
    {
        if (!_enabled || _instantiateComponent is null || _capacity is null ||
            _componentCache is null || __result is not null ||
            __0 is null || __3 is not IReadOnlyList<Type> types ||
            types.Count < 2) return true;

        var planMap = Plans.GetValue(__0, _ => new Dictionary<object, Plan>());
        if (!planMap.TryGetValue(__3, out var plan))
        {
            // Full-plan validation before any live creation. First use,
            // uncertain spec lookup, or missing spec retains vanilla.
            var typeArray = new Type[types.Count];
            var specs = new object?[types.Count];
            var cached = 0;
            for (var i = 0; i < types.Count; i++)
            {
                var type = types[i];
                typeArray[i] = type;
                if (!BlueprintSpecInstantiationCache.IsComponentSpecType(type))
                    continue;
                if (!BlueprintSpecInstantiationCache.TryGetCachedSpec(__0, type, out var spec))
                    return true;
                specs[i] = spec;
                cached++;
            }

            // Nothing to gain when no cached spec can replace an expensive
            // Harmony/Blueprint.GetSpec call. Prefer vanilla for DI-only plans.
            if (cached == 0) return true;

            plan = new Plan(typeArray, specs, cached);
            planMap[__3] = plan;
        }

        // Strictly use the original cache-count call and initial-component
        // enumeration, preserving order and exceptions. No pooled live objects.
        var cacheService = _componentCache.GetValue(__instance);
        if (cacheService is null) return true;
        var result = new List<object>(_capacity(cacheService, __1));
        if (__2 is not null)
            foreach (var initialized in __2)
                result.Add(initialized);

        for (var i = 0; i < plan.Types.Length; i++)
            result.Add(plan.Specs[i] ??
                _instantiateComponent(__instance, __0, plan.Types[i]));

        __result = result;
        _fastBatches++;
        _cachedSpecs += plan.CachedCount;
        _liveComponents += plan.Types.Length - plan.CachedCount;

        if (_fastBatches == 100 || _fastBatches == 1000 ||
            _fastBatches == 10000 || _fastBatches == 100000)
            Runtime.Log($"bulk/preview fast path: batches={_fastBatches}, " +
                $"cached immutable specs={_cachedSpecs}, " +
                $"live native components={_liveComponents}");
        return false;
    }

    /// <summary>
    /// Fail-closed structural verification of the RUNNING game's IL. We permit
    /// only the component-count lookup, List<object> creation/appends, the
    /// original single-component factory and normal enumeration operations.
    /// Unknown method calls, field writes, allocations or IL forms disqualify
    /// the shortcut instead of guessing that they have no side effects.
    /// </summary>
    private static bool AuditNativeBatch(
        MethodInfo batch, MethodInfo item, MethodInfo countMethod, out string reason)
    {
        reason = "not inspected";
        var bytes = batch.GetMethodBody()?.GetILAsByteArray();
        if (bytes is null) { reason = "no method body"; return false; }
        var calls = new List<string>();
        var hasCount = false; var hasItem = false;
        var hasListCtor = false; var hasListAdd = false;
        try
        {
            var index = 0;
            while (index < bytes.Length)
            {
                var first = bytes[index++];
                var op = first == 0xFE ?
                    TwoByte[bytes[index++]] : OneByte[first];
                if (op.Size == 0)
                { reason = "unknown IL opcode"; return false; }
                if (op == OpCodes.Stfld || op == OpCodes.Stsfld ||
                    op == OpCodes.Calli || op == OpCodes.Throw ||
                    op == OpCodes.Rethrow)
                { reason = "field mutation, indirect call, or explicit throw"; return false; }

                if (op.OperandType == OperandType.InlineMethod)
                {
                    var token = BitConverter.ToInt32(bytes, index);
                    var method = batch.Module.ResolveMethod(token,
                        batch.DeclaringType?.GetGenericArguments(), null);
                    if (method is null) { reason = "unresolved call"; return false; }
                    var name = method.DeclaringType?.FullName + "." + method.Name;
                    calls.Add(name ?? "unknown");
                    if (SameMethod(method, countMethod))
                        hasCount = true;
                    else if (SameMethod(method, item))
                        hasItem = true;
                    else if (method.DeclaringType == typeof(List<object>) &&
                             method.Name == ".ctor")
                        hasListCtor = true;
                    else if (method.DeclaringType == typeof(List<object>) &&
                             method.Name == "Add")
                        hasListAdd = true;
                    else if (!AllowedEnumeratorCall(method))
                    {
                        reason = "unknown native call: " + name;
                        return false;
                    }

                    if (op == OpCodes.Newobj && method.DeclaringType != typeof(List<object>))
                    { reason = "unreviewed object creation: " + name; return false; }
                }
                else if (op == OpCodes.Newarr || op == OpCodes.Stobj ||
                         op == OpCodes.Cpobj)
                { reason = "unreviewed object/array mutation"; return false; }

                index += OperandSize(op.OperandType, bytes, index);
                if (index > bytes.Length)
                { reason = "truncated IL"; return false; }
            }
            if (!hasCount || !hasItem || !hasListCtor || !hasListAdd)
            {
                reason = "native method lacks required count/list/add/item calls: " +
                    string.Join(", ", calls.Take(10));
                return false;
            }
            reason = "reviewed native list loop";
            return true;
        }
        catch (Exception ex)
        {
            reason = "IL inspection error: " + ex.GetType().Name;
            return false;
        }
    }

    private static bool SameMethod(MethodBase a, MethodBase b) =>
        a.Module == b.Module && a.MetadataToken == b.MetadataToken;

    private static bool AllowedEnumeratorCall(MethodBase method)
    {
        var name = method.Name;
        var type = method.DeclaringType;
        if (type is null) return false;
        if (name != "GetEnumerator" && name != "MoveNext" &&
            name != "get_Current" && name != "Dispose" &&
            name != "get_Count" && name != "get_Item" &&
            name != "get_Length") return false;

        var typeName = type.FullName ?? "";
        return typeName.StartsWith("System.Collections.Immutable.ImmutableArray`1", StringComparison.Ordinal) ||
               typeName.StartsWith("System.Collections.Generic.IReadOnlyList`1", StringComparison.Ordinal) ||
               typeName.StartsWith("System.Collections.Generic.IEnumerable`1", StringComparison.Ordinal) ||
               typeName.StartsWith("System.Collections.Generic.IEnumerator`1", StringComparison.Ordinal) ||
               typeName == "System.Collections.IEnumerator" ||
               typeName == "System.IDisposable";
    }

    private static int OperandSize(OperandType type, byte[] il, int index) =>
        type switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or
                OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineI or OperandType.InlineBrTarget or
                OperandType.InlineField or OperandType.InlineString or
                OperandType.InlineMethod or OperandType.InlineSig or
                OperandType.InlineTok or OperandType.InlineType or
                OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, index),
            _ => throw new InvalidOperationException("Unknown IL operand: " + type)
        };

    private static readonly OpCode[] OneByte = BuildOpCodes(false);
    private static readonly OpCode[] TwoByte = BuildOpCodes(true);

    private static OpCode[] BuildOpCodes(bool twoByte)
    {
        var result = new OpCode[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public))
        {
            if (field.GetValue(null) is not OpCode opcode) continue;
            var value = unchecked((ushort)opcode.Value);
            if ((value > 0xFF) == twoByte)
                result[value & 0xFF] = opcode;
        }
        return result;
    }
}
