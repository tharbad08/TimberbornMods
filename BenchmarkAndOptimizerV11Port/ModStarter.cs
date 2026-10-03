using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        var harmony = new Harmony("shay.BenchmarkAndOptimizerV11");
        Runtime.Initialize(modEnvironment.ModPath, harmony);

        Runtime.Log($"loaded; config={Runtime.ConfigPath}; normal throttling uses direct method patches");
    }
}

internal static class DispatcherPatcher
{
    private static bool _patched;

    private sealed record Target(string TypeName, string MethodName, string PrefixName);

    private static readonly Target[] Targets =
    {
        new("Timberborn.TickSystem.TickableSingletonService", "TickSingletons", nameof(PrefixTickSingletons)),
        new("Timberborn.TickSystem.TickableEntity", "TickTickableComponents", nameof(PrefixTickableComponents)),
        new("Timberborn.SingletonSystem.SingletonLifecycleService", "UpdateSingletons", nameof(PrefixUpdateSingletons)),
        new("Timberborn.SingletonSystem.SingletonLifecycleService", "LateUpdateSingletons", nameof(PrefixLateUpdateSingletons)),
    };

    public static void Patch(Harmony harmony)
    {
        if (_patched)
        {
            return;
        }

        foreach (var target in Targets)
        {
            var type = AccessTools.TypeByName(target.TypeName);
            var original = type is null ? null : AccessTools.Method(type, target.MethodName);
            var prefix = AccessTools.Method(typeof(DispatcherPatcher), target.PrefixName);

            if (original is null || prefix is null)
            {
                Runtime.Log($"warning: target not found: {target.TypeName}.{target.MethodName}; leaving vanilla behavior");
                continue;
            }

            harmony.Patch(original, prefix: new HarmonyMethod(prefix) { priority = Priority.Low });
            Runtime.Log($"benchmark patch installed: {target.TypeName}.{target.MethodName}");
        }

        _patched = true;
    }

    public static void Unpatch(Harmony harmony)
    {
        if (!_patched)
        {
            return;
        }

        foreach (var target in Targets)
        {
            var type = AccessTools.TypeByName(target.TypeName);
            var original = type is null ? null : AccessTools.Method(type, target.MethodName);
            if (original is not null)
            {
                harmony.Unpatch(original, HarmonyPatchType.Prefix, harmony.Id);
            }
        }

        _patched = false;
        Runtime.Log("benchmark dispatcher patches removed");
    }

    public static bool PrefixTickSingletons(object __instance) =>
        Runtime.TryDispatch(__instance, "_tickableSingletons", "_tickableSingleton", "Tick");

    public static bool PrefixTickableComponents(object __instance) =>
        Runtime.TryDispatch(__instance, "_tickableComponents", "_tickableComponent", "StartAndTick", "Enabled");

    public static bool PrefixUpdateSingletons(object __instance) =>
        Runtime.TryDispatch(__instance, "_updatableSingletons", null, "UpdateSingleton");

    public static bool PrefixLateUpdateSingletons(object __instance) =>
        Runtime.TryDispatch(__instance, "_lateUpdatableSingletons", null, "LateUpdateSingleton");
}



internal static class FreezeDetectorPatcher
{
    private static readonly Harmony FreezeHarmony = new("shay.BenchmarkAndOptimizerV11.FreezeDetector");
    private static bool _patched;

    private sealed record Target(string TypeName, string MethodName, string PrefixName);

    private static readonly Target[] Targets =
    {
        new("Timberborn.SingletonSystem.SingletonLifecycleService", "UpdateSingletons", nameof(UpdatePhasePrefix)),
        new("Timberborn.SingletonSystem.SingletonLifecycleService", "LateUpdateSingletons", nameof(LateUpdatePhasePrefix)),
        new("Timberborn.TickSystem.TickableSingletonService", "TickSingletons", nameof(TickPhasePrefix)),
    };

    public static void Patch()
    {
        if (_patched)
        {
            return;
        }

        foreach (var target in Targets)
        {
            var type = AccessTools.TypeByName(target.TypeName);
            var original = type is null ? null : AccessTools.Method(type, target.MethodName);
            if (original is null)
            {
                Runtime.Log($"warning: freeze detector target not found: {target.TypeName}.{target.MethodName}");
                continue;
            }

            FreezeHarmony.Patch(
                original,
                prefix: new HarmonyMethod(AccessTools.Method(typeof(FreezeDetectorPatcher), target.PrefixName))
                {
                    priority = Priority.First
                },
                postfix: new HarmonyMethod(AccessTools.Method(typeof(FreezeDetectorPatcher), nameof(PhasePostfix)))
                {
                    priority = Priority.Last
                });

            Runtime.Log($"freeze detector patch installed: {target.TypeName}.{target.MethodName}");
        }

        PatchTickSingletonMethods();
        PatchLifecycleSingletonMethods(
            "Timberborn.SingletonSystem.IUpdatableSingleton",
            "UpdateSingleton",
            UpdateSingletonRuntimeTypes,
            UpdateSingletonMethods,
            nameof(UpdateSingletonPrefix),
            nameof(UpdateSingletonFinalizer),
            "UpdateSingleton");
        PatchLifecycleSingletonMethods(
            "Timberborn.SingletonSystem.ILateUpdatableSingleton",
            "LateUpdateSingleton",
            LateUpdateSingletonRuntimeTypes,
            LateUpdateSingletonMethods,
            nameof(LateUpdateSingletonPrefix),
            nameof(LateUpdateSingletonFinalizer),
            "LateUpdateSingleton");
        EntityTickDispatcherProfiler.Patch(FreezeHarmony);
        SoilContaminationDeepProfiler.Patch(FreezeHarmony);

        _patched = true;
        FreezeDetector.Initialize();
    }

    private static readonly HashSet<Type> TickSingletonRuntimeTypes = new();
    private static readonly HashSet<MethodBase> TickSingletonMethods = new();
    private static readonly HashSet<Type> UpdateSingletonRuntimeTypes = new();
    private static readonly HashSet<MethodBase> UpdateSingletonMethods = new();
    private static readonly HashSet<Type> LateUpdateSingletonRuntimeTypes = new();
    private static readonly HashSet<MethodBase> LateUpdateSingletonMethods = new();

    private struct TickSingletonSample
    {
        public bool Active;
        public long Started;
        public string? TypeName;
    }

    private static void PatchTickSingletonMethods()
    {
        var tickInterface = AccessTools.TypeByName("Timberborn.TickSystem.ITickableSingleton");
        if (tickInterface is null)
        {
            Runtime.Log("warning: always-on TickSingleton freeze profiler unavailable: ITickableSingleton not found");
            return;
        }

        var interfaceTick = tickInterface
            .GetMethods()
            .FirstOrDefault(method => method.Name == "Tick" && method.GetParameters().Length == 0);

        if (interfaceTick is null)
        {
            Runtime.Log("warning: always-on TickSingleton freeze profiler unavailable: ITickableSingleton.Tick not found");
            return;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || !tickInterface.IsAssignableFrom(type))
                {
                    continue;
                }

                MethodInfo? target = null;
                try
                {
                    var map = type.GetInterfaceMap(tickInterface);
                    for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        if (map.InterfaceMethods[i].Name == interfaceTick.Name &&
                            map.InterfaceMethods[i].GetParameters().Length == 0)
                        {
                            target = map.TargetMethods[i];
                            break;
                        }
                    }
                }
                catch
                {
                    // Some generated/runtime types cannot expose an interface map.
                }

                target ??= AccessTools.Method(type, "Tick", Type.EmptyTypes);
                if (target is null || target.ReturnType != typeof(void))
                {
                    continue;
                }

                var declaringType = target.DeclaringType;
                if (declaringType is not null && target.ReflectedType != declaringType)
                {
                    var declared = AccessTools.DeclaredMethod(
                        declaringType,
                        target.Name,
                        target.GetParameters().Select(parameter => parameter.ParameterType).ToArray());
                    if (declared is not null)
                    {
                        target = declared;
                    }
                }

                TickSingletonRuntimeTypes.Add(type);
                if (!TickSingletonMethods.Add(target))
                {
                    continue;
                }

                try
                {
                    FreezeHarmony.Patch(
                        target,
                        prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(FreezeDetectorPatcher), nameof(TickSingletonPrefix)))
                        {
                            priority = 900
                        },
                        finalizer: new HarmonyMethod(
                            AccessTools.Method(typeof(FreezeDetectorPatcher), nameof(TickSingletonFinalizer)))
                        {
                            priority = Priority.Last
                        });
                }
                catch (Exception ex)
                {
                    TickSingletonMethods.Remove(target);
                    Runtime.Log(
                        $"warning: TickSingleton freeze profiler could not patch " +
                        $"{declaringType?.FullName ?? type.FullName}.{target.Name}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Runtime.Log(
            $"always-on TickSingleton freeze profiler installed: " +
            $"{TickSingletonRuntimeTypes.Count} concrete type(s), " +
            $"{TickSingletonMethods.Count} effective method(s)");
    }

    private static void PatchLifecycleSingletonMethods(
        string interfaceTypeName,
        string methodName,
        HashSet<Type> runtimeTypes,
        HashSet<MethodBase> methods,
        string prefixName,
        string finalizerName,
        string label)
    {
        var singletonInterface = AccessTools.TypeByName(interfaceTypeName);
        if (singletonInterface is null)
        {
            Runtime.Log($"warning: always-on {label} freeze profiler unavailable: {interfaceTypeName} not found");
            return;
        }

        var interfaceMethod = singletonInterface
            .GetMethods()
            .FirstOrDefault(method => method.Name == methodName && method.GetParameters().Length == 0);

        if (interfaceMethod is null)
        {
            Runtime.Log($"warning: always-on {label} freeze profiler unavailable: {interfaceTypeName}.{methodName} not found");
            return;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(type => type is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface || !singletonInterface.IsAssignableFrom(type))
                {
                    continue;
                }

                MethodInfo? target = null;
                try
                {
                    var map = type.GetInterfaceMap(singletonInterface);
                    for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        if (map.InterfaceMethods[i].Name == methodName &&
                            map.InterfaceMethods[i].GetParameters().Length == 0)
                        {
                            target = map.TargetMethods[i];
                            break;
                        }
                    }
                }
                catch
                {
                    // Some generated/runtime types cannot expose an interface map.
                }

                target ??= AccessTools.Method(type, methodName, Type.EmptyTypes);
                if (target is null || target.ReturnType != typeof(void))
                {
                    continue;
                }

                var declaringType = target.DeclaringType;
                if (declaringType is not null && target.ReflectedType != declaringType)
                {
                    target = AccessTools.DeclaredMethod(
                        declaringType,
                        target.Name,
                        target.GetParameters().Select(parameter => parameter.ParameterType).ToArray())
                        ?? target;
                }

                runtimeTypes.Add(type);
                if (!methods.Add(target))
                {
                    continue;
                }

                try
                {
                    FreezeHarmony.Patch(
                        target,
                        prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(FreezeDetectorPatcher), prefixName))
                        {
                            priority = 900
                        },
                        finalizer: new HarmonyMethod(
                            AccessTools.Method(typeof(FreezeDetectorPatcher), finalizerName))
                        {
                            priority = Priority.Last
                        });
                }
                catch (Exception ex)
                {
                    methods.Remove(target);
                    Runtime.Log(
                        $"warning: {label} freeze profiler could not patch " +
                        $"{declaringType?.FullName ?? type.FullName}.{target.Name}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Runtime.Log(
            $"always-on {label} freeze profiler installed: " +
            $"{runtimeTypes.Count} concrete type(s), {methods.Count} effective method(s)");
    }

    private static void UpdateSingletonPrefix(object __instance, out TickSingletonSample __state)
    {
        BeginLifecycleSample(__instance, UpdateSingletonRuntimeTypes, out __state);
    }

    private static Exception? UpdateSingletonFinalizer(
        Exception? __exception,
        TickSingletonSample __state)
    {
        if (__state.Active && __state.Started != 0 && __state.TypeName is not null)
        {
            FreezeDetector.RecordUpdateSingleton(
                __state.TypeName,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started);
        }

        return __exception;
    }

    private static void LateUpdateSingletonPrefix(object __instance, out TickSingletonSample __state)
    {
        BeginLifecycleSample(__instance, LateUpdateSingletonRuntimeTypes, out __state);
    }

    private static Exception? LateUpdateSingletonFinalizer(
        Exception? __exception,
        TickSingletonSample __state)
    {
        if (__state.Active && __state.Started != 0 && __state.TypeName is not null)
        {
            FreezeDetector.RecordLateUpdateSingleton(
                __state.TypeName,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started);
        }

        return __exception;
    }

    private static void BeginLifecycleSample(
        object __instance,
        HashSet<Type> runtimeTypes,
        out TickSingletonSample __state)
    {
        __state = default;
        if (Runtime.IsBenchmarking || __instance is null)
        {
            return;
        }

        var runtimeType = __instance.GetType();
        if (!runtimeTypes.Contains(runtimeType))
        {
            return;
        }

        __state.Active = true;
        __state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        __state.TypeName = runtimeType.FullName ?? runtimeType.Name;
    }

    private static void TickSingletonPrefix(object __instance, out TickSingletonSample __state)
    {
        __state = default;

        if (Runtime.IsBenchmarking || __instance is null)
        {
            return;
        }

        var runtimeType = __instance.GetType();
        if (!TickSingletonRuntimeTypes.Contains(runtimeType))
        {
            return;
        }

        __state.Active = true;
        __state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        __state.TypeName = runtimeType.FullName ?? runtimeType.Name;
    }

    private static Exception? TickSingletonFinalizer(
        Exception? __exception,
        TickSingletonSample __state)
    {
        if (__state.Active && __state.Started != 0 && __state.TypeName is not null)
        {
            FreezeDetector.RecordTickSingleton(
                __state.TypeName,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started);
        }

        return __exception;
    }

    private static void UpdatePhasePrefix(out long __state)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        EntityTickDispatcherProfiler.FlushFrame();
        FreezeDetector.FrameBoundary(now, "UpdateSingletons");
        __state = now;
    }

    private static void TickPhasePrefix(out long __state)
    {
        __state = System.Diagnostics.Stopwatch.GetTimestamp();
        FreezeDetector.BeginPhase("TickSingletons", __state);
    }

    private static void LateUpdatePhasePrefix(out long __state)
    {
        __state = System.Diagnostics.Stopwatch.GetTimestamp();
        FreezeDetector.BeginPhase("LateUpdateSingletons", __state);
    }

    private static void PhasePostfix(long __state, MethodBase __originalMethod)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var section = __originalMethod.Name;
        FreezeDetector.RecordSection(section, now - __state);
        FreezeDetector.EndPhase(section, now);
    }
}

internal static class EntityTickDispatcherProfiler
{
    private const int MaxCallers = 32;
    private const double SlowEntityTickMs = 20.0;
    private const int MaxComponentTypes = 20;
    private static readonly HashSet<MethodBase> PatchedCallers = new();
    private static readonly Dictionary<string, long> SlowEntityTicks = new(StringComparer.Ordinal);
    private static long _frameTicks;
    private static int _frameCalls;

    private struct Sample
    {
        public bool Active;
        public long Started;
    }

    public static void Patch(Harmony harmony)
    {
        try
        {
            var entityType = AccessTools.TypeByName("Timberborn.TickSystem.TickableEntity");
            var target = entityType is null
                ? null
                : AccessTools.Method(entityType, "TickTickableComponents");

            if (target is null)
            {
                Runtime.Log("warning: entity tick dispatcher profiler unavailable: TickableEntity.TickTickableComponents not found");
                return;
            }

            var callers = FindDirectCallers(target)
                .Take(MaxCallers)
                .ToArray();

            foreach (var caller in callers)
            {
                try
                {
                    harmony.Patch(
                        caller,
                        prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(EntityTickDispatcherProfiler), nameof(Prefix)))
                        {
                            priority = Priority.First
                        },
                        finalizer: new HarmonyMethod(
                            AccessTools.Method(typeof(EntityTickDispatcherProfiler), nameof(Finalizer)))
                        {
                            priority = Priority.Last
                        });

                    PatchedCallers.Add(caller);
                    Runtime.Log(
                        $"entity tick dispatcher profiler patch installed: " +
                        $"{caller.DeclaringType?.FullName}.{caller.Name}");
                }
                catch (Exception ex)
                {
                    Runtime.Log(
                        $"warning: entity tick dispatcher profiler could not patch " +
                        $"{caller.DeclaringType?.FullName}.{caller.Name}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }

            Runtime.Log(
                $"entity tick dispatcher profiler installed: {PatchedCallers.Count} outer caller(s)");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: entity tick dispatcher profiler installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Prefix(out Sample __state)
    {
        __state = default;
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        __state.Active = true;
        __state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static Exception? Finalizer(
        object __instance,
        Exception? __exception,
        Sample __state)
    {
        if (!__state.Active || __state.Started == 0)
        {
            return __exception;
        }

        var elapsedTicks =
            System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started;
        if (elapsedTicks <= 0)
        {
            return __exception;
        }

        // TickableEntity.Tick runs on the main simulation thread. Accumulate locally
        // and take the FreezeDetector lock only once per frame, rather than once per
        // entity. This keeps the diagnostic from becoming a hot-path bottleneck.
        _frameTicks += elapsedTicks;
        _frameCalls++;

        var elapsedMs =
            elapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedMs >= SlowEntityTickMs)
        {
            var label = DescribeSlowEntity(__instance);
            SlowEntityTicks.TryGetValue(label, out var existing);
            SlowEntityTicks[label] = existing + elapsedTicks;
        }

        return __exception;
    }

    public static void FlushFrame()
    {
        if (_frameTicks > 0)
        {
            FreezeDetector.RecordEntityDispatcher("TickableEntity.Tick", _frameTicks);
        }

        foreach (var pair in SlowEntityTicks)
        {
            FreezeDetector.RecordSlowEntityTick(pair.Key, pair.Value);
        }

        _frameTicks = 0;
        _frameCalls = 0;
        SlowEntityTicks.Clear();
    }

    private static string DescribeSlowEntity(object instance)
    {
        try
        {
            var id = RuntimeHelpers.GetHashCode(instance).ToString("X8");
            var componentsMember =
                (MemberInfo?)AccessTools.Field(instance.GetType(), "_tickableComponents") ??
                AccessTools.Property(instance.GetType(), "_tickableComponents");

            var raw = ReadMember(instance, componentsMember);
            if (raw is not IEnumerable enumerable)
            {
                return $"entity#{id}[components=unavailable]";
            }

            var componentTypes = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var total = 0;

            foreach (var item in enumerable)
            {
                if (item is null)
                {
                    continue;
                }

                total++;
                var actual = ExtractTickableComponent(item) ?? item;
                var type = actual.GetType();
                var name = type.FullName ?? type.Name;
                if (seen.Add(name) && componentTypes.Count < MaxComponentTypes)
                {
                    componentTypes.Add(name);
                }
            }

            var suffix = seen.Count > MaxComponentTypes ? "|..." : "";
            var components = componentTypes.Count == 0
                ? "none"
                : string.Join("|", componentTypes) + suffix;

            return $"entity#{id}[count={total};components={components}]";
        }
        catch (Exception ex)
        {
            return $"entity[description-error={ex.GetType().Name}]";
        }
    }

    private static object? ExtractTickableComponent(object wrapper)
    {
        var type = wrapper.GetType();
        var member =
            (MemberInfo?)AccessTools.Field(type, "_tickableComponent") ??
            AccessTools.Property(type, "_tickableComponent") ??
            AccessTools.Field(type, "TickableComponent") ??
            AccessTools.Property(type, "TickableComponent");

        return ReadMember(wrapper, member);
    }

    private static object? ReadMember(object instance, MemberInfo? member) =>
        member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property when property.GetIndexParameters().Length == 0 =>
                property.GetValue(instance),
            _ => null
        };

    private static IEnumerable<MethodInfo> FindDirectCallers(MethodInfo target)
    {
        var assembly = target.DeclaringType?.Assembly;
        if (assembly is null)
        {
            yield break;
        }

        foreach (var type in SafeGetTypes(assembly))
        {
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly;

            foreach (var candidate in type.GetMethods(flags))
            {
                if (candidate == target ||
                    candidate.IsAbstract ||
                    candidate.ContainsGenericParameters)
                {
                    continue;
                }

                if (CallsTarget(candidate, target))
                {
                    yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Cast<Type>();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }

    private static bool SameMethod(MethodBase left, MethodBase right)
    {
        try
        {
            return left.Module == right.Module &&
                   left.MetadataToken == right.MetadataToken;
        }
        catch
        {
            return ReferenceEquals(left, right);
        }
    }

    private static bool CallsTarget(MethodInfo candidate, MethodInfo target)
    {
        var body = candidate.GetMethodBody();
        var il = body?.GetILAsByteArray();
        if (il is null)
        {
            return false;
        }

        var position = 0;
        while (position < il.Length)
        {
            System.Reflection.Emit.OpCode op;
            var first = il[position++];
            if (first == 0xFE)
            {
                if (position >= il.Length ||
                    !TwoByteOpCodes.TryGetValue((short)il[position++], out op))
                {
                    return false;
                }
            }
            else if (!OneByteOpCodes.TryGetValue((short)first, out op))
            {
                return false;
            }

            var operandStart = position;
            var operandSize = OperandSize(op.OperandType, il, operandStart);
            if (operandSize < 0 || operandStart + operandSize > il.Length)
            {
                return false;
            }

            if ((op == System.Reflection.Emit.OpCodes.Call ||
                 op == System.Reflection.Emit.OpCodes.Callvirt) &&
                operandSize == 4)
            {
                try
                {
                    var token = BitConverter.ToInt32(il, operandStart);
                    var called = candidate.Module.ResolveMethod(
                        token,
                        candidate.DeclaringType?.GetGenericArguments(),
                        candidate.GetGenericArguments());

                    if (called is not null && SameMethod(called, target))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore unresolved generic/runtime tokens.
                }
            }

            position += operandSize;
        }

        return false;
    }

    private static readonly Dictionary<short, System.Reflection.Emit.OpCode> OneByteOpCodes =
        typeof(System.Reflection.Emit.OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(System.Reflection.Emit.OpCode))
            .Select(field => (System.Reflection.Emit.OpCode)field.GetValue(null)!)
            .Where(op => op.Size == 1)
            .ToDictionary(op => (short)(byte)op.Value);

    private static readonly Dictionary<short, System.Reflection.Emit.OpCode> TwoByteOpCodes =
        typeof(System.Reflection.Emit.OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(System.Reflection.Emit.OpCode))
            .Select(field => (System.Reflection.Emit.OpCode)field.GetValue(null)!)
            .Where(op => op.Size == 2)
            .ToDictionary(op => (short)(op.Value & 0xFF));

    private static int OperandSize(
        System.Reflection.Emit.OperandType operandType,
        byte[] il,
        int position) =>
        operandType switch
        {
            System.Reflection.Emit.OperandType.InlineNone => 0,
            System.Reflection.Emit.OperandType.ShortInlineBrTarget => 1,
            System.Reflection.Emit.OperandType.ShortInlineI => 1,
            System.Reflection.Emit.OperandType.ShortInlineVar => 1,
            System.Reflection.Emit.OperandType.InlineVar => 2,
            System.Reflection.Emit.OperandType.InlineI => 4,
            System.Reflection.Emit.OperandType.InlineBrTarget => 4,
            System.Reflection.Emit.OperandType.InlineField => 4,
            System.Reflection.Emit.OperandType.InlineMethod => 4,
            System.Reflection.Emit.OperandType.InlineSig => 4,
            System.Reflection.Emit.OperandType.InlineString => 4,
            System.Reflection.Emit.OperandType.InlineTok => 4,
            System.Reflection.Emit.OperandType.ShortInlineR => 4,
            System.Reflection.Emit.OperandType.InlineI8 => 8,
            System.Reflection.Emit.OperandType.InlineR => 8,
            System.Reflection.Emit.OperandType.InlineSwitch =>
                position + 4 <= il.Length
                    ? 4 + (BitConverter.ToInt32(il, position) * 4)
                    : -1,
            _ => -1
        };
}

internal static class SoilContaminationDeepProfiler
{
    private static Type? _soilType;
    private static MethodInfo? _soilTick;

    private struct SoilTickSample
    {
        public bool Active;
        public int Gen0;
        public int Gen1;
        public int Gen2;
    }

    public static void Patch(Harmony harmony)
    {
        try
        {
            _soilType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .FirstOrDefault(type => type.Name == "SoilContaminationService");

            if (_soilType is null)
            {
                Runtime.Log("warning: SoilContamination lightweight profiler unavailable: type not found");
                return;
            }

            _soilTick = AccessTools.Method(_soilType, "Tick", Type.EmptyTypes);
            if (_soilTick is null)
            {
                Runtime.Log("warning: SoilContamination lightweight profiler unavailable: Tick not found");
                return;
            }

            // Intentionally patch ONLY the outer Tick. The v1.1.36 diagnostic
            // patched hot inner methods such as SetContaminationLevel, which are
            // called thousands of times and materially changed game performance.
            harmony.Patch(
                _soilTick,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(SoilContaminationDeepProfiler), nameof(SoilTickPrefix)))
                {
                    priority = Priority.First
                },
                finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(SoilContaminationDeepProfiler), nameof(SoilTickFinalizer)))
                {
                    priority = Priority.Last
                });

            Runtime.Log(
                $"SoilContamination lightweight profiler installed: " +
                $"type={_soilType.FullName}; outer Tick only, no inner-method patches");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: SoilContamination lightweight profiler installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void SoilTickPrefix(out SoilTickSample __state)
    {
        __state = default;
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        __state.Active = true;
        __state.Gen0 = GC.CollectionCount(0);
        __state.Gen1 = GC.CollectionCount(1);
        __state.Gen2 = GC.CollectionCount(2);
    }

    private static Exception? SoilTickFinalizer(Exception? __exception, SoilTickSample __state)
    {
        if (__state.Active)
        {
            FreezeDetector.RecordSoilGc(
                GC.CollectionCount(0) - __state.Gen0,
                GC.CollectionCount(1) - __state.Gen1,
                GC.CollectionCount(2) - __state.Gen2);
        }

        return __exception;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Cast<Type>();
        }
        catch
        {
            return Array.Empty<Type>();
        }
    }
}

internal static class FreezeDetector
{
    private const double SlowFrameMs = 250.0;
    private const double SevereFrameMs = 500.0;
    private const double FreezeFrameMs = 1000.0;
    private const int TopSystemCount = 8;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> SectionTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> SystemTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> TickSingletonTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> UpdateSingletonTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> LateUpdateSingletonTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> EntityDispatcherTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> SlowEntityTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> PhaseGapTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> SoilDetailTicks = new(StringComparer.Ordinal);

    private static bool _initialized;
    private static long _lastPhaseEndTicks;
    private static string? _lastPhaseName;
    private static int _soilGc0;
    private static int _soilGc1;
    private static int _soilGc2;
    private static long _frameStartTicks;
    private static long _frameNumber;
    private static int _gc0;
    private static int _gc1;
    private static int _gc2;

    public static void Initialize()
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            _frameStartTicks = 0;
            _frameNumber = 0;
            _gc0 = GC.CollectionCount(0);
            _gc1 = GC.CollectionCount(1);
            _gc2 = GC.CollectionCount(2);
            SectionTicks.Clear();
            SystemTicks.Clear();
            TickSingletonTicks.Clear();
            UpdateSingletonTicks.Clear();
            LateUpdateSingletonTicks.Clear();
            EntityDispatcherTicks.Clear();
            SlowEntityTicks.Clear();
            PhaseGapTicks.Clear();
            SoilDetailTicks.Clear();
            _lastPhaseEndTicks = 0;
            _lastPhaseName = null;
            _soilGc0 = 0;
            _soilGc1 = 0;
            _soilGc2 = 0;
        }

        Runtime.Log(
            $"freeze detector enabled: slow>={SlowFrameMs:F0}ms, " +
            $"severe>={SevereFrameMs:F0}ms, freeze>={FreezeFrameMs:F0}ms");
    }

    public static void FrameBoundary(long now, string nextPhase)
    {
        string? freezeLine = null;

        lock (Gate)
        {
            if (!_initialized)
            {
                return;
            }

            if (_frameStartTicks != 0)
            {
                RecordGapLocked(nextPhase, now);

                var elapsedTicks = now - _frameStartTicks;
                var elapsedMs = ToMs(elapsedTicks);

                var nextGc0 = GC.CollectionCount(0);
                var nextGc1 = GC.CollectionCount(1);
                var nextGc2 = GC.CollectionCount(2);

                if (elapsedMs >= SlowFrameMs)
                {
                    freezeLine = BuildFreezeLine(
                        elapsedMs,
                        nextGc0 - _gc0,
                        nextGc1 - _gc1,
                        nextGc2 - _gc2);
                }

                _gc0 = nextGc0;
                _gc1 = nextGc1;
                _gc2 = nextGc2;
            }

            _frameNumber++;
            _frameStartTicks = now;
            SectionTicks.Clear();
            SystemTicks.Clear();
            TickSingletonTicks.Clear();
            UpdateSingletonTicks.Clear();
            LateUpdateSingletonTicks.Clear();
            EntityDispatcherTicks.Clear();
            SlowEntityTicks.Clear();
            PhaseGapTicks.Clear();
            SoilDetailTicks.Clear();
            _soilGc0 = 0;
            _soilGc1 = 0;
            _soilGc2 = 0;
            _lastPhaseEndTicks = 0;
            _lastPhaseName = null;
        }

        if (freezeLine is not null)
        {
            Runtime.LogFreeze(freezeLine);
        }
    }

    public static void BeginPhase(string phaseName, long now)
    {
        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            RecordGapLocked(phaseName, now);
        }
    }

    public static void EndPhase(string phaseName, long now)
    {
        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            _lastPhaseName = phaseName;
            _lastPhaseEndTicks = now;
        }
    }

    private static void RecordGapLocked(string nextPhase, long now)
    {
        if (_lastPhaseEndTicks == 0 || string.IsNullOrEmpty(_lastPhaseName) || now <= _lastPhaseEndTicks)
        {
            return;
        }

        var key = $"{_lastPhaseName}->{nextPhase}";
        PhaseGapTicks.TryGetValue(key, out var existing);
        PhaseGapTicks[key] = existing + (now - _lastPhaseEndTicks);
    }

    public static void RecordSection(string name, long elapsedTicks)
    {
        if (elapsedTicks <= 0)
        {
            return;
        }

        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            SectionTicks.TryGetValue(name, out var existing);
            SectionTicks[name] = existing + elapsedTicks;
        }
    }

    public static void RecordSystem(string name, long elapsedTicks)
    {
        if (elapsedTicks <= 0)
        {
            return;
        }

        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            SystemTicks.TryGetValue(name, out var existing);
            SystemTicks[name] = existing + elapsedTicks;
        }
    }

    public static void RecordTickSingleton(string name, long elapsedTicks)
    {
        if (elapsedTicks <= 0)
        {
            return;
        }

        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            TickSingletonTicks.TryGetValue(name, out var existing);
            TickSingletonTicks[name] = existing + elapsedTicks;
        }
    }

    public static void RecordUpdateSingleton(string name, long elapsedTicks) =>
        RecordNamedTicks(UpdateSingletonTicks, name, elapsedTicks);

    public static void RecordLateUpdateSingleton(string name, long elapsedTicks) =>
        RecordNamedTicks(LateUpdateSingletonTicks, name, elapsedTicks);

    public static void RecordEntityDispatcher(string name, long elapsedTicks) =>
        RecordNamedTicks(EntityDispatcherTicks, name, elapsedTicks);

    public static void RecordSlowEntityTick(string name, long elapsedTicks) =>
        RecordNamedTicks(SlowEntityTicks, name, elapsedTicks);

    private static void RecordNamedTicks(
        Dictionary<string, long> destination,
        string name,
        long elapsedTicks)
    {
        if (elapsedTicks <= 0)
        {
            return;
        }

        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            destination.TryGetValue(name, out var existing);
            destination[name] = existing + elapsedTicks;
        }
    }

    public static void RecordSoilDetail(string name, long elapsedTicks)
    {
        if (elapsedTicks <= 0)
        {
            return;
        }

        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            SoilDetailTicks.TryGetValue(name, out var existing);
            SoilDetailTicks[name] = existing + elapsedTicks;
        }
    }

    public static void RecordSoilGc(int gen0, int gen1, int gen2)
    {
        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            _soilGc0 += gen0;
            _soilGc1 += gen1;
            _soilGc2 += gen2;
        }
    }

    private static string BuildFreezeLine(
        double elapsedMs,
        int gc0Delta,
        int gc1Delta,
        int gc2Delta)
    {
        var severity = elapsedMs >= FreezeFrameMs
            ? "FREEZE"
            : elapsedMs >= SevereFrameMs
                ? "SEVERE"
                : "SLOW";

        SectionTicks.TryGetValue("UpdateSingletons", out var updateTicks);
        SectionTicks.TryGetValue("LateUpdateSingletons", out var lateTicks);
        SectionTicks.TryGetValue("TickSingletons", out var tickTicks);
        SystemTicks.TryGetValue("TimberPhysics.Core.PhysicsSimulator", out var physicsTicks);

        var dispatcherTicks = updateTicks + lateTicks + tickTicks;
        var unattributedTicks = Math.Max(
            0,
            (long)(elapsedMs * System.Diagnostics.Stopwatch.Frequency / 1000.0) - dispatcherTicks);

        var topSystems = SystemTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{ShortName(x.Key)}={ToMs(x.Value):F1}ms")
            .ToArray();

        var topText = topSystems.Length == 0
            ? "none (run benchmark for per-system detail)"
            : string.Join(", ", topSystems);

        var topTickSingletons = TickSingletonTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{ShortName(x.Key)}={ToMs(x.Value):F1}ms")
            .ToArray();

        var topTickText = topTickSingletons.Length == 0
            ? "none"
            : string.Join(", ", topTickSingletons);

        var topUpdates = UpdateSingletonTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{ShortName(x.Key)}={ToMs(x.Value):F1}ms")
            .ToArray();
        var topUpdateText = topUpdates.Length == 0 ? "none" : string.Join(", ", topUpdates);

        var topLateUpdates = LateUpdateSingletonTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{ShortName(x.Key)}={ToMs(x.Value):F1}ms")
            .ToArray();
        var topLateUpdateText = topLateUpdates.Length == 0 ? "none" : string.Join(", ", topLateUpdates);

        var entityDispatchers = EntityDispatcherTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{ShortMethodName(x.Key)}={ToMs(x.Value):F1}ms")
            .ToArray();
        var entityDispatcherText = entityDispatchers.Length == 0
            ? "none"
            : string.Join(", ", entityDispatchers);

        var slowEntities = SlowEntityTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{x.Key}={ToMs(x.Value):F1}ms")
            .ToArray();
        var slowEntityText = slowEntities.Length == 0
            ? "none >=20ms"
            : string.Join(", ", slowEntities);

        var phaseGaps = PhaseGapTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{x.Key}={ToMs(x.Value):F1}ms")
            .ToArray();
        var phaseGapText = phaseGaps.Length == 0 ? "none" : string.Join(", ", phaseGaps);

        var soilDetails = SoilDetailTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{ShortMethodName(x.Key)}={ToMs(x.Value):F1}ms")
            .ToArray();
        var soilText = soilDetails.Length == 0 ? "none" : string.Join(", ", soilDetails);

        return
            $"{severity} frame={_frameNumber} elapsed={elapsedMs:F1}ms; " +
            $"dispatch: UpdateSingletons={ToMs(updateTicks):F1}ms, " +
            $"LateUpdateSingletons={ToMs(lateTicks):F1}ms, " +
            $"TickSingletons={ToMs(tickTicks):F1}ms, " +
            $"unattributed={ToMs(unattributedTicks):F1}ms; " +
            $"physics={ToMs(physicsTicks):F1}ms; " +
            $"GC delta=[gen0:{gc0Delta}, gen1:{gc1Delta}, gen2:{gc2Delta}]; " +
            $"phase gaps: {phaseGapText}; " +
            $"top update singletons: {topUpdateText}; " +
            $"top late-update singletons: {topLateUpdateText}; " +
            $"entity tick dispatchers: {entityDispatcherText}; " +
            $"slow entity ticks: {slowEntityText}; " +
            $"top tick singletons: {topTickText}; " +
            $"soil GC=[gen0:{_soilGc0}, gen1:{_soilGc1}, gen2:{_soilGc2}]; " +
            $"top systems: {topText}";
    }

    private static string ShortMethodName(string value)
    {
        var separator = value.LastIndexOf("::", StringComparison.Ordinal);
        if (separator >= 0)
        {
            var typePart = value[..separator];
            var typeSeparator = typePart.LastIndexOf('.');
            var shortType = typeSeparator >= 0 ? typePart[(typeSeparator + 1)..] : typePart;
            return shortType + "." + value[(separator + 2)..];
        }

        return ShortName(value);
    }

    private static string ShortName(string value)
    {
        const string timberPrefix = "TimberPhysics.Core.PhysicsSimulator::";
        if (value.StartsWith(timberPrefix, StringComparison.Ordinal))
        {
            return "PhysicsSimulator::" + value[timberPrefix.Length..];
        }

        var index = value.LastIndexOf('.');
        return index >= 0 ? value[(index + 1)..] : value;
    }

    private static double ToMs(long ticks) =>
        ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
}

internal static class TimberPhysicsBenchmarkPatcher
{
    private const string SimulatorTypeName = "TimberPhysics.Core.PhysicsSimulator";
    private const string RegistryTypeName = "TimberPhysics.Core.PhysicalObjectRegistry";
    private const string PhysicsTypeName = "UnityEngine.Physics";

    private static bool _patched;
    private static MethodBase? _simulatorMethod;
    private static MethodBase? _stepAllMethod;
    private static MethodBase? _simulateMethod;
    private static Type? _simulatorType;
    private static string _origin = "MOD: Bober's Laws of Motion (eMka.TimberPhysics)";

    [ThreadStatic]
    private static int _simulatorDepth;

    public static void Patch(Harmony harmony)
    {
        if (_patched)
        {
            return;
        }

        _simulatorType = AccessTools.TypeByName(SimulatorTypeName);
        var registryType = AccessTools.TypeByName(RegistryTypeName);
        var physicsType = AccessTools.TypeByName(PhysicsTypeName);

        _simulatorMethod = _simulatorType is null
            ? null
            : AccessTools.Method(_simulatorType, "UpdateSingleton", Type.EmptyTypes);
        _stepAllMethod = registryType is null
            ? null
            : AccessTools.Method(registryType, "StepAll", new[] { typeof(float) });
        _simulateMethod = physicsType is null
            ? null
            : AccessTools.Method(physicsType, "Simulate", new[] { typeof(float) });

        if (_simulatorMethod is null || _stepAllMethod is null || _simulateMethod is null)
        {
            Runtime.Log(
                $"warning: TimberPhysics split benchmark unavailable; " +
                $"simulator={_simulatorMethod is not null}, stepAll={_stepAllMethod is not null}, " +
                $"simulate={_simulateMethod is not null}");
            _simulatorMethod = null;
            _stepAllMethod = null;
            _simulateMethod = null;
            _simulatorType = null;
            return;
        }

        _origin = Runtime.ResolveDiagnosticOrigin(_simulatorType);

        harmony.Patch(
            _simulatorMethod,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(TimberPhysicsBenchmarkPatcher), nameof(SimulatorPrefix))) { priority = Priority.First },
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(TimberPhysicsBenchmarkPatcher), nameof(SimulatorFinalizer))));

        harmony.Patch(
            _stepAllMethod,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(TimberPhysicsBenchmarkPatcher), nameof(StepAllPrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(TimberPhysicsBenchmarkPatcher), nameof(StepAllPostfix))));

        harmony.Patch(
            _simulateMethod,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(TimberPhysicsBenchmarkPatcher), nameof(SimulatePrefix))),
            postfix: new HarmonyMethod(AccessTools.Method(typeof(TimberPhysicsBenchmarkPatcher), nameof(SimulatePostfix))));

        _patched = true;
        Runtime.Log("TimberPhysics split benchmark installed: StepAll + Physics.Simulate");
    }

    public static void Unpatch(Harmony harmony)
    {
        if (!_patched)
        {
            return;
        }

        foreach (var method in new[] { _simulatorMethod, _stepAllMethod, _simulateMethod })
        {
            if (method is not null)
            {
                harmony.Unpatch(method, HarmonyPatchType.All, harmony.Id);
            }
        }

        _patched = false;
        _simulatorDepth = 0;
        Runtime.Log("TimberPhysics split benchmark removed");
    }

    private static void SimulatorPrefix()
    {
        if (Runtime.IsBenchmarking)
        {
            _simulatorDepth++;
        }
    }

    private static Exception? SimulatorFinalizer(Exception? __exception)
    {
        if (_simulatorDepth > 0)
        {
            _simulatorDepth--;
        }

        return __exception;
    }

    private static void StepAllPrefix(out long __state)
    {
        __state = _simulatorDepth > 0 && Runtime.IsBenchmarking
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
    }

    private static void StepAllPostfix(long __state)
    {
        if (__state == 0)
        {
            return;
        }

        Runtime.RecordDiagnosticBenchmark(
            "TimberPhysics.Core.PhysicsSimulator::StepAll",
            _origin,
            System.Diagnostics.Stopwatch.GetTimestamp() - __state);
    }

    private static void SimulatePrefix(out long __state)
    {
        __state = _simulatorDepth > 0 && Runtime.IsBenchmarking
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
    }

    private static void SimulatePostfix(long __state)
    {
        if (__state == 0)
        {
            return;
        }

        Runtime.RecordDiagnosticBenchmark(
            "TimberPhysics.Core.PhysicsSimulator::Physics.Simulate",
            _origin,
            System.Diagnostics.Stopwatch.GetTimestamp() - __state);
    }
}


internal static class TimberPhysicsCatchUpLimiterPatcher
{
    private const string SimulatorTypeName = "TimberPhysics.Core.PhysicsSimulator";
    private const string RegistryTypeName = "TimberPhysics.Core.PhysicalObjectRegistry";
    private const string PhysicsTypeName = "UnityEngine.Physics";
    private const string TimeTypeName = "UnityEngine.Time";
    private const float FixedDeltaTime = 0.02f;
    private const int MaxSubstepsPerUpdate = 4;

    private static bool _patched;
    private static bool _loggedDrop;
    private static MethodBase? _simulatorMethod;
    private static Func<object, float>? _getTimer;
    private static Action<object, float>? _setTimer;
    private static Func<object, object>? _getRegistry;
    private static Func<float>? _getDeltaTime;
    private static Func<int>? _getSimulationMode;
    private static Action<object, float>? _stepAll;
    private static Action<float>? _simulate;
    private static int _scriptSimulationMode;

    public static void Patch(Harmony harmony)
    {
        if (_patched)
        {
            return;
        }

        try
        {
            var simulatorType = AccessTools.TypeByName(SimulatorTypeName);
            var registryType = AccessTools.TypeByName(RegistryTypeName);
            var physicsType = AccessTools.TypeByName(PhysicsTypeName);
            var timeType = AccessTools.TypeByName(TimeTypeName);

            if (simulatorType is null || registryType is null || physicsType is null || timeType is null)
            {
                Runtime.Log("TimberPhysics catch-up limiter not installed: required types are not loaded");
                return;
            }

            _simulatorMethod = AccessTools.Method(simulatorType, "UpdateSingleton", Type.EmptyTypes);
            var timerField = AccessTools.Field(simulatorType, "_timer");
            var registryField = AccessTools.Field(simulatorType, "_physicalObjectRegistry");
            var stepAllMethod = AccessTools.Method(registryType, "StepAll", new[] { typeof(float) });
            var simulateMethod = AccessTools.Method(physicsType, "Simulate", new[] { typeof(float) });
            var simulationModeGetter = AccessTools.PropertyGetter(physicsType, "simulationMode");
            var deltaTimeGetter = AccessTools.PropertyGetter(timeType, "deltaTime");

            if (_simulatorMethod is null || timerField is null || registryField is null ||
                stepAllMethod is null || simulateMethod is null ||
                simulationModeGetter is null || deltaTimeGetter is null)
            {
                Runtime.Log(
                    "warning: TimberPhysics catch-up limiter unavailable; " +
                    "the installed Bober's Laws of Motion build does not match the expected v1.1.1.0 layout");
                _simulatorMethod = null;
                return;
            }

            var instance = Expression.Parameter(typeof(object), "instance");
            var typedInstance = Expression.Convert(instance, simulatorType);
            var timerValue = Expression.Parameter(typeof(float), "timer");

            _getTimer = Expression.Lambda<Func<object, float>>(
                Expression.Field(typedInstance, timerField),
                instance).Compile();

            _setTimer = Expression.Lambda<Action<object, float>>(
                Expression.Block(
                    Expression.Assign(Expression.Field(typedInstance, timerField), timerValue),
                    Expression.Empty()),
                instance,
                timerValue).Compile();

            _getRegistry = Expression.Lambda<Func<object, object>>(
                Expression.Convert(Expression.Field(typedInstance, registryField), typeof(object)),
                instance).Compile();

            _getDeltaTime = Expression.Lambda<Func<float>>(
                Expression.Call(deltaTimeGetter)).Compile();

            _getSimulationMode = Expression.Lambda<Func<int>>(
                Expression.Convert(Expression.Call(simulationModeGetter), typeof(int))).Compile();

            _scriptSimulationMode = Convert.ToInt32(
                Enum.Parse(simulationModeGetter.ReturnType, "Script"));

            var registry = Expression.Parameter(typeof(object), "registry");
            var deltaTime = Expression.Parameter(typeof(float), "deltaTime");

            _stepAll = Expression.Lambda<Action<object, float>>(
                Expression.Call(
                    Expression.Convert(registry, registryType),
                    stepAllMethod,
                    deltaTime),
                registry,
                deltaTime).Compile();

            var simulateCall = Expression.Call(simulateMethod, deltaTime);
            var simulateBody = simulateMethod.ReturnType == typeof(void)
                ? (Expression)simulateCall
                : Expression.Block(simulateCall, Expression.Empty());

            _simulate = Expression.Lambda<Action<float>>(
                simulateBody,
                deltaTime).Compile();

            var prefix = AccessTools.Method(
                typeof(TimberPhysicsCatchUpLimiterPatcher),
                nameof(Prefix));

            harmony.Patch(
                _simulatorMethod,
                prefix: new HarmonyMethod(prefix) { priority = Priority.Last });

            _patched = true;
            Runtime.Log(
                $"TimberPhysics catch-up limiter installed: max {MaxSubstepsPerUpdate} x " +
                $"{FixedDeltaTime:F2}s PhysX substeps per UpdateSingleton");
        }
        catch (Exception ex)
        {
            _simulatorMethod = null;
            _getTimer = null;
            _setTimer = null;
            _getRegistry = null;
            _getDeltaTime = null;
            _getSimulationMode = null;
            _stepAll = null;
            _simulate = null;
            Runtime.Log(
                $"warning: TimberPhysics catch-up limiter installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}; leaving Bober's Laws of Motion vanilla");
        }
    }

    public static void Reapply(Harmony harmony)
    {
        _patched = false;
        Patch(harmony);
    }

    private static bool Prefix(object __instance)
    {
        var freezeStarted = !Runtime.IsBenchmarking
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;

        if (!Runtime.Enabled ||
            _getTimer is null ||
            _setTimer is null ||
            _getRegistry is null ||
            _getDeltaTime is null ||
            _getSimulationMode is null ||
            _stepAll is null ||
            _simulate is null)
        {
            return true;
        }

        if (_getSimulationMode() != _scriptSimulationMode)
        {
            return true;
        }

        var timer = _getTimer(__instance) + _getDeltaTime();
        _setTimer(__instance, timer);

        var registry = _getRegistry(__instance);
        var substeps = 0;

        while (timer >= FixedDeltaTime && substeps < MaxSubstepsPerUpdate)
        {
            timer -= FixedDeltaTime;

            // Match the original method's state ordering: the timer is decremented
            // before StepAll/Physics.Simulate. If either throws, no stale backlog is
            // left in the simulator field.
            _setTimer(__instance, timer);
            _stepAll(registry, FixedDeltaTime);
            _simulate(FixedDeltaTime);
            substeps++;
        }

        if (timer >= FixedDeltaTime)
        {
            var droppedSubsteps = (int)(timer / FixedDeltaTime);
            timer %= FixedDeltaTime;
            _setTimer(__instance, timer);

            if (!_loggedDrop)
            {
                _loggedDrop = true;
                Runtime.Log(
                    $"TimberPhysics catch-up limiter activated; dropped {droppedSubsteps} " +
                    "backlogged physics substep(s) on the first overloaded update");
            }
        }

        if (freezeStarted != 0)
        {
            FreezeDetector.RecordSystem(
                "TimberPhysics.Core.PhysicsSimulator",
                System.Diagnostics.Stopwatch.GetTimestamp() - freezeStarted);
        }

        return false;
    }
}


internal static class TimberPhysicsTerrainColliderMergerPatcher
{
    private const string ServiceTypeName = "TimberPhysics.Terrain.TerrainColliderService";
    private const string CoordinateSystemTypeName = "Timberborn.Coordinates.CoordinateSystem";
    private const int ChunkSize = 16;

    private static readonly ConditionalWeakTable<object, TerrainState> States = new();

    private static bool _patched;
    private static FieldInfo? _rootObjectField;
    private static FieldInfo? _columnTerrainMapField;
    private static FieldInfo? _mapSizeField;
    private static FieldInfo? _mapIndexServiceField;
    private static MemberInfo? _terrainSizeMember;
    private static MemberInfo? _columnCountMember;
    private static MethodInfo? _getColumnMethod;
    private static Func<object, int, object>? _getColumnValue;
    private static MethodInfo? _spawnAllMethod;
    private static FieldInfo? _boxCollidersField;
    private static bool _disabled;
    private static bool _skipNextVanillaSpawnXY;
    private static MemberInfo? _verticalStrideMember;
    private static MethodInfo? _cellToIndexMethod;
    private static MethodInfo? _gridToWorldCenteredMethod;
    private static MethodInfo? _addComponentByTypeMethod;
    private static MethodInfo? _destroyMethod;
    private static Type? _vector2IntType;
    private static Type? _vector3Type;
    private static Type? _boxColliderType;
    private static MemberInfo? _vector2X;
    private static MemberInfo? _vector2Y;
    private static MemberInfo? _vector3X;
    private static MemberInfo? _vector3Y;
    private static MemberInfo? _vector3Z;
    private static MemberInfo? _terrainFloor;
    private static MemberInfo? _terrainCeiling;
    private static MemberInfo? _colliderCenter;
    private static MemberInfo? _colliderSize;

    public static void Patch(Harmony harmony)
    {
        if (_patched)
        {
            return;
        }

        try
        {
            var serviceType = AccessTools.TypeByName(ServiceTypeName);
            var coordinateSystemType = AccessTools.TypeByName(CoordinateSystemTypeName);

            if (serviceType is null || coordinateSystemType is null)
            {
                Runtime.Log("TimberPhysics terrain merger not installed: required TimberPhysics/CoordinateSystem types are not loaded");
                return;
            }

            _rootObjectField = AccessTools.Field(serviceType, "_rootObject");
            _columnTerrainMapField = AccessTools.Field(serviceType, "_columnTerrainMap");
            _mapSizeField = AccessTools.Field(serviceType, "_mapSize");
            _mapIndexServiceField = AccessTools.Field(serviceType, "_mapIndexService");
            var originalBoxColliderDictionary = AccessTools.Field(serviceType, "_boxColliders");

            _disabled = false;
            var spawnAll = FindMethod(serviceType, "SpawnColliders", 0);
            var spawnXY = FindMethod(serviceType, "SpawnCollidersXY", 1);
            var removeXY = FindMethod(serviceType, "RemoveCollidersXY", 1);

            _vector2IntType = spawnXY?.GetParameters().FirstOrDefault()?.ParameterType;

            if (_rootObjectField is null || _columnTerrainMapField is null ||
                _mapSizeField is null || _mapIndexServiceField is null ||
                originalBoxColliderDictionary is null ||
                spawnAll is null || spawnXY is null || removeXY is null ||
                _vector2IntType is null)
            {
                LogResolutionFailure(
                    ("rootObject", _rootObjectField),
                    ("columnTerrainMap", _columnTerrainMapField),
                    ("mapSize", _mapSizeField),
                    ("mapIndexService", _mapIndexServiceField),
                    ("boxColliders", originalBoxColliderDictionary),
                    ("SpawnColliders", spawnAll),
                    ("SpawnCollidersXY", spawnXY),
                    ("RemoveCollidersXY", removeXY),
                    ("Vector2Int", _vector2IntType));
                return;
            }

            _spawnAllMethod = spawnAll;
            _boxCollidersField = originalBoxColliderDictionary;

            var mapSizeType = _mapSizeField.FieldType;
            var columnTerrainMapType = _columnTerrainMapField.FieldType;
            var mapIndexServiceType = _mapIndexServiceField.FieldType;

            _terrainSizeMember = FindMember(mapSizeType, "TerrainSize");
            _columnCountMember = FindMember(columnTerrainMapType, "ColumnCount");
            _getColumnMethod = FindMethod(
                columnTerrainMapType,
                "GetColumn",
                1,
                m => m.GetParameters()[0].ParameterType == typeof(int));
            _getColumnValue = _getColumnMethod is null
                ? null
                : CreateColumnValueGetter(_getColumnMethod);
            _verticalStrideMember = FindMember(mapIndexServiceType, "VerticalStride");
            _cellToIndexMethod = FindMethod(
                mapIndexServiceType,
                "CellToIndex",
                1,
                m => m.GetParameters()[0].ParameterType == _vector2IntType);

            _gridToWorldCenteredMethod = FindMethod(
                coordinateSystemType,
                "GridToWorldCentered",
                1,
                m => m.GetParameters()[0].ParameterType.FullName == "UnityEngine.Vector3");

            _vector3Type = _gridToWorldCenteredMethod?.GetParameters()[0].ParameterType;

            var boxArgs = originalBoxColliderDictionary.FieldType.IsGenericType
                ? originalBoxColliderDictionary.FieldType.GetGenericArguments()
                : Type.EmptyTypes;
            _boxColliderType = boxArgs.Length >= 2
                ? boxArgs[boxArgs.Length - 1]
                : AccessTools.TypeByName("UnityEngine.BoxCollider");

            var gameObjectType = _rootObjectField.FieldType;
            _addComponentByTypeMethod = FindMethod(
                gameObjectType,
                "AddComponent",
                1,
                m => m.GetParameters()[0].ParameterType == typeof(Type));

            var unityObjectType = gameObjectType.BaseType ?? AccessTools.TypeByName("UnityEngine.Object");
            _destroyMethod = unityObjectType is null
                ? null
                : FindMethod(
                    unityObjectType,
                    "Destroy",
                    1,
                    m => m.IsStatic);

            _vector2X = FindMember(_vector2IntType, "x");
            _vector2Y = FindMember(_vector2IntType, "y");

            if (_vector3Type is not null)
            {
                _vector3X = FindMember(_vector3Type, "x");
                _vector3Y = FindMember(_vector3Type, "y");
                _vector3Z = FindMember(_vector3Type, "z");
            }

            var terrainColumnType = _getColumnMethod?.ReturnType;
            if (terrainColumnType?.IsByRef == true)
            {
                terrainColumnType = terrainColumnType.GetElementType();
            }

            if (terrainColumnType is not null)
            {
                _terrainFloor = FindMember(terrainColumnType, "Floor");
                _terrainCeiling = FindMember(terrainColumnType, "Ceiling");

                if (_terrainFloor is null || _terrainCeiling is null)
                {
                    var members = terrainColumnType
                        .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .Select(member => member.Name)
                        .Distinct()
                        .OrderBy(name => name)
                        .Take(64);

                    Runtime.Log(
                        $"TimberPhysics terrain column diagnostic: type={terrainColumnType.FullName}; " +
                        $"members=[{string.Join(", ", members)}]");
                }
            }

            if (_boxColliderType is not null)
            {
                _colliderCenter = FindMember(_boxColliderType, "center");
                _colliderSize = FindMember(_boxColliderType, "size");
            }

            if (_terrainSizeMember is null || _columnCountMember is null ||
                _getColumnMethod is null || _getColumnValue is null || _verticalStrideMember is null ||
                _cellToIndexMethod is null || _gridToWorldCenteredMethod is null ||
                _vector3Type is null || _boxColliderType is null ||
                _addComponentByTypeMethod is null || _destroyMethod is null ||
                _vector2X is null || _vector2Y is null ||
                _vector3X is null || _vector3Y is null || _vector3Z is null ||
                _terrainFloor is null || _terrainCeiling is null ||
                _colliderCenter is null || _colliderSize is null)
            {
                LogResolutionFailure(
                    ("TerrainSize", _terrainSizeMember),
                    ("ColumnCount", _columnCountMember),
                    ("GetColumn", _getColumnMethod),
                    ("GetColumn value getter", _getColumnValue),
                    ("VerticalStride", _verticalStrideMember),
                    ("CellToIndex", _cellToIndexMethod),
                    ("GridToWorldCentered", _gridToWorldCenteredMethod),
                    ("Vector3", _vector3Type),
                    ("BoxCollider", _boxColliderType),
                    ("AddComponent(Type)", _addComponentByTypeMethod),
                    ("Object.Destroy", _destroyMethod),
                    ("Vector2.x", _vector2X),
                    ("Vector2.y", _vector2Y),
                    ("Vector3.x", _vector3X),
                    ("Vector3.y", _vector3Y),
                    ("Vector3.z", _vector3Z),
                    ("TerrainColumn.Floor", _terrainFloor),
                    ("TerrainColumn.Ceiling", _terrainCeiling),
                    ("BoxCollider.center", _colliderCenter),
                    ("BoxCollider.size", _colliderSize));
                return;
            }

            harmony.Patch(
                spawnAll,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(TimberPhysicsTerrainColliderMergerPatcher), nameof(SpawnAllPrefix)))
                { priority = Priority.First });

            harmony.Patch(
                spawnXY,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(TimberPhysicsTerrainColliderMergerPatcher), nameof(SpawnXYPrefix)))
                { priority = Priority.First });

            harmony.Patch(
                removeXY,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(TimberPhysicsTerrainColliderMergerPatcher), nameof(RemoveXYPrefix)))
                { priority = Priority.First });

            _patched = true;
            Runtime.Log(
                $"TimberPhysics terrain collider merger installed: {ChunkSize}x{ChunkSize} chunks, " +
                "exact floor/ceiling interval merging");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: TimberPhysics terrain merger installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}; leaving vanilla terrain colliders");
        }
    }

    private static void LogResolutionFailure(params (string Name, object? Value)[] values)
    {
        var missing = values
            .Where(x => x.Value is null)
            .Select(x => x.Name)
            .ToArray();

        Runtime.Log(
            "warning: TimberPhysics terrain merger could not resolve required members: " +
            (missing.Length == 0 ? "unknown" : string.Join(", ", missing)) +
            "; leaving vanilla terrain colliders");
    }

    private static MethodInfo? FindMethod(
        Type type,
        string name,
        int parameterCount,
        Func<MethodInfo, bool>? predicate = null)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            var methods = current.GetMethods(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly);

            var match = methods.FirstOrDefault(m =>
                m.Name == name &&
                m.GetParameters().Length == parameterCount &&
                (predicate is null || predicate(m)));

            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private static bool SpawnAllPrefix(object __instance)
    {
        if (_disabled)
        {
            return true;
        }

        try
        {
            BuildAll(__instance);
            return false;
        }
        catch (Exception ex)
        {
            try
            {
                var state = States.GetOrCreateValue(__instance);
                ClearState(state);
            }
            catch
            {
                // Best-effort cleanup only.
            }

            States.Remove(__instance);
            _disabled = true;
            _skipNextVanillaSpawnXY = false;
            Runtime.Log(
                $"warning: TimberPhysics merged terrain initial build failed: {ex}; " +
                "merger disabled for this session and vanilla terrain colliders restored");
            return true;
        }
    }

    private static bool SpawnXYPrefix(object __instance, object __0)
    {
        if (_disabled)
        {
            if (_skipNextVanillaSpawnXY)
            {
                _skipNextVanillaSpawnXY = false;
                Runtime.Log(
                    "TimberPhysics terrain merger suppressed one SpawnCollidersXY call " +
                    "already covered by the full vanilla restore");
                return false;
            }

            return true;
        }

        try
        {
            RebuildChunk(__instance, __0);
            return false;
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: TimberPhysics merged terrain chunk rebuild failed: " +
                $"{ex.GetType().Name}: {ex.Message}; attempting transactional vanilla restore");

            if (TryDisableMergerAndRestoreVanilla(
                    __instance,
                    "chunk rebuild",
                    ex,
                    skipNextSpawnXY: false))
            {
                // The full vanilla terrain set already includes this changed cell.
                return false;
            }

            // Do not mix one vanilla chunk into the merged representation. A failed
            // full restore leaves the merger enabled and logs loudly for diagnosis.
            return false;
        }
    }

    private static bool RemoveXYPrefix(object __instance, object __0)
    {
        if (_disabled)
        {
            return true;
        }

        try
        {
            RemoveChunkForCoordinates(__instance, __0);
            return false;
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: TimberPhysics merged terrain chunk removal failed: " +
                $"{ex.GetType().Name}: {ex.Message}; attempting transactional vanilla restore");

            if (TryDisableMergerAndRestoreVanilla(
                    __instance,
                    "chunk removal",
                    ex,
                    skipNextSpawnXY: true))
            {
                // The full vanilla rebuild supersedes both this removal and the
                // immediately following SpawnCollidersXY for the same terrain edit.
                return false;
            }

            return false;
        }
    }

    private static bool TryDisableMergerAndRestoreVanilla(
        object service,
        string phase,
        Exception cause,
        bool skipNextSpawnXY)
    {
        _disabled = true;
        _skipNextVanillaSpawnXY = skipNextSpawnXY;

        try
        {
            // Remove any stale vanilla dictionary entries first. In normal merged
            // operation this dictionary is empty, but clearing it makes recovery
            // safe even after an earlier partial/fallback path.
            ClearVanillaColliderDictionary(service);

            // _disabled is already true, so invoking the patched method re-enters
            // SpawnAllPrefix, which immediately yields to TimberPhysics' original
            // SpawnColliders implementation.
            _spawnAllMethod!.Invoke(service, null);

            if (States.TryGetValue(service, out var state))
            {
                ClearState(state);
            }

            States.Remove(service);
            Runtime.Log(
                $"TimberPhysics terrain merger disabled after {phase} failure; " +
                $"vanilla terrain colliders fully restored. Cause: " +
                $"{cause.GetType().Name}: {cause.Message}");
            return true;
        }
        catch (Exception restoreEx)
        {
            try
            {
                ClearVanillaColliderDictionary(service);
            }
            catch
            {
                // Keep the original restore failure as the useful diagnostic.
            }

            _disabled = false;
            _skipNextVanillaSpawnXY = false;
            Runtime.Log(
                $"ERROR: TimberPhysics terrain merger could not restore vanilla colliders " +
                $"after {phase} failure. Merger remains enabled to avoid a mixed " +
                $"vanilla/merged collider dictionary. Original={cause.GetType().Name}: " +
                $"{cause.Message}; Restore={restoreEx}");
            return false;
        }
    }

    private static void ClearVanillaColliderDictionary(object service)
    {
        var dictionaryObject = _boxCollidersField!.GetValue(service);
        if (dictionaryObject is null)
        {
            return;
        }

        if (dictionaryObject is IDictionary dictionary)
        {
            var colliders = new List<object>();
            foreach (DictionaryEntry entry in dictionary)
            {
                if (entry.Value is not null)
                {
                    colliders.Add(entry.Value);
                }
            }

            dictionary.Clear();
            foreach (var collider in colliders)
            {
                TryDestroyCollider(collider);
            }

            return;
        }

        var dictionaryType = dictionaryObject.GetType();
        var valuesProperty = AccessTools.Property(dictionaryType, "Values");
        if (valuesProperty?.GetValue(dictionaryObject) is IEnumerable values)
        {
            var colliders = new List<object>();
            foreach (var value in values)
            {
                if (value is not null)
                {
                    colliders.Add(value);
                }
            }

            var clearMethod = AccessTools.Method(dictionaryType, "Clear");
            if (clearMethod is null)
            {
                throw new InvalidOperationException(
                    $"Cannot clear TimberPhysics collider dictionary type {dictionaryType.FullName}");
            }

            clearMethod.Invoke(dictionaryObject, null);
            foreach (var collider in colliders)
            {
                TryDestroyCollider(collider);
            }

            return;
        }

        throw new InvalidOperationException(
            $"Unsupported TimberPhysics collider dictionary type {dictionaryType.FullName}");
    }

    private static void BuildAll(object service)
    {
        var state = States.GetOrCreateValue(service);
        ClearState(state);

        var terrainSize = ReadMember(_mapSizeField!.GetValue(service)!, _terrainSizeMember!)!;
        var sizeX = Convert.ToInt32(ReadMember(terrainSize, _vector2X!));
        var sizeY = Convert.ToInt32(ReadMember(terrainSize, _vector2Y!));

        var before = 0L;
        var after = 0L;

        for (var y = 0; y < sizeY; y += ChunkSize)
        {
            for (var x = 0; x < sizeX; x += ChunkSize)
            {
                var counts = BuildChunk(service, state, x, y, sizeX, sizeY);
                before += counts.Cells;
                after += counts.Colliders;
            }
        }

        Runtime.Log(
            $"TimberPhysics terrain merge complete: terrain columns={before}, " +
            $"merged colliders={after}, reduction={(before == 0 ? 0 : 100.0 * (before - after) / before):F1}%");
    }

    private static void RebuildChunk(object service, object coordinates)
    {
        var state = States.GetOrCreateValue(service);
        var x = Convert.ToInt32(ReadMember(coordinates, _vector2X!));
        var y = Convert.ToInt32(ReadMember(coordinates, _vector2Y!));
        var originX = x / ChunkSize * ChunkSize;
        var originY = y / ChunkSize * ChunkSize;

        RemoveChunk(state, ChunkKey(originX, originY));

        var terrainSize = ReadMember(_mapSizeField!.GetValue(service)!, _terrainSizeMember!)!;
        var sizeX = Convert.ToInt32(ReadMember(terrainSize, _vector2X!));
        var sizeY = Convert.ToInt32(ReadMember(terrainSize, _vector2Y!));

        BuildChunk(service, state, originX, originY, sizeX, sizeY);
    }

    private static void RemoveChunkForCoordinates(object service, object coordinates)
    {
        var state = States.GetOrCreateValue(service);
        var x = Convert.ToInt32(ReadMember(coordinates, _vector2X!));
        var y = Convert.ToInt32(ReadMember(coordinates, _vector2Y!));
        var originX = x / ChunkSize * ChunkSize;
        var originY = y / ChunkSize * ChunkSize;
        RemoveChunk(state, ChunkKey(originX, originY));
    }

    private static (int Cells, int Colliders) BuildChunk(
        object service,
        TerrainState state,
        int originX,
        int originY,
        int mapSizeX,
        int mapSizeY)
    {
        var width = Math.Min(ChunkSize, mapSizeX - originX);
        var height = Math.Min(ChunkSize, mapSizeY - originY);
        if (width <= 0 || height <= 0)
        {
            return (0, 0);
        }

        var columnTerrainMap = _columnTerrainMapField!.GetValue(service)!;
        var mapIndexService = _mapIndexServiceField!.GetValue(service)!;
        var columnCounts = ReadMember(columnTerrainMap, _columnCountMember!)!;
        var verticalStride = Convert.ToInt32(ReadMember(mapIndexService, _verticalStrideMember!));

        var occupancyByInterval = new Dictionary<(float Floor, float Ceiling), bool[,]>();
        var terrainColumns = 0;

        for (var localY = 0; localY < height; localY++)
        {
            for (var localX = 0; localX < width; localX++)
            {
                var coordinates = CreateVector2Int(originX + localX, originY + localY);
                var cellIndex = Convert.ToInt32(
                    _cellToIndexMethod!.Invoke(mapIndexService, new[] { coordinates }));
                var columnCount = ReadIndexedInt(columnCounts, cellIndex);

                for (var i = 0; i < columnCount; i++)
                {
                    var index3D = cellIndex + i * verticalStride;
                    var terrainColumn = _getColumnValue!(columnTerrainMap, index3D);
                    var floor = Convert.ToSingle(ReadMember(terrainColumn, _terrainFloor!));
                    var ceiling = Convert.ToSingle(ReadMember(terrainColumn, _terrainCeiling!));

                    if (ceiling <= floor)
                    {
                        continue;
                    }

                    terrainColumns++;
                    var key = (floor, ceiling);
                    if (!occupancyByInterval.TryGetValue(key, out var occupancy))
                    {
                        occupancy = new bool[width, height];
                        occupancyByInterval.Add(key, occupancy);
                    }

                    occupancy[localX, localY] = true;
                }
            }
        }

        var colliders = new List<object>();
        try
        {
            foreach (var pair in occupancyByInterval)
            {
                MergeInterval(
                    service,
                    originX,
                    originY,
                    pair.Key.Floor,
                    pair.Key.Ceiling,
                    pair.Value,
                    colliders);
            }

            state.Chunks[ChunkKey(originX, originY)] = colliders;
            return (terrainColumns, colliders.Count);
        }
        catch
        {
            // BuildChunk is transactional: no partially-created merged colliders
            // may survive into a fallback path.
            foreach (var collider in colliders)
            {
                TryDestroyCollider(collider);
            }

            throw;
        }
    }

    private static void MergeInterval(
        object service,
        int originX,
        int originY,
        float floor,
        float ceiling,
        bool[,] occupancy,
        List<object> colliders)
    {
        var width = occupancy.GetLength(0);
        var height = occupancy.GetLength(1);
        var consumed = new bool[width, height];

        for (var localY = 0; localY < height; localY++)
        {
            for (var localX = 0; localX < width; localX++)
            {
                if (!occupancy[localX, localY] || consumed[localX, localY])
                {
                    continue;
                }

                var runWidth = 1;
                while (localX + runWidth < width &&
                       occupancy[localX + runWidth, localY] &&
                       !consumed[localX + runWidth, localY])
                {
                    runWidth++;
                }

                var runHeight = 1;
                var canGrow = true;
                while (localY + runHeight < height && canGrow)
                {
                    for (var x = 0; x < runWidth; x++)
                    {
                        if (!occupancy[localX + x, localY + runHeight] ||
                            consumed[localX + x, localY + runHeight])
                        {
                            canGrow = false;
                            break;
                        }
                    }

                    if (canGrow)
                    {
                        runHeight++;
                    }
                }

                for (var y = 0; y < runHeight; y++)
                {
                    for (var x = 0; x < runWidth; x++)
                    {
                        consumed[localX + x, localY + y] = true;
                    }
                }

                colliders.Add(SpawnMergedCollider(
                    service,
                    originX + localX,
                    originY + localY,
                    runWidth,
                    runHeight,
                    floor,
                    ceiling));
            }
        }
    }

    private static object SpawnMergedCollider(
        object service,
        int startX,
        int startY,
        int width,
        int height,
        float floor,
        float ceiling)
    {
        var first = _gridToWorldCenteredMethod!.Invoke(
            null,
            new[] { CreateVector3(startX, startY, floor) })!;
        var last = _gridToWorldCenteredMethod.Invoke(
            null,
            new[] { CreateVector3(startX + width - 1, startY + height - 1, floor) })!;

        var firstX = Convert.ToSingle(ReadMember(first, _vector3X!));
        var firstY = Convert.ToSingle(ReadMember(first, _vector3Y!));
        var firstZ = Convert.ToSingle(ReadMember(first, _vector3Z!));
        var lastX = Convert.ToSingle(ReadMember(last, _vector3X!));
        var lastZ = Convert.ToSingle(ReadMember(last, _vector3Z!));

        var colliderHeight = ceiling - floor;
        var center = CreateVector3(
            (firstX + lastX) / 2f,
            firstY + colliderHeight / 2f,
            (firstZ + lastZ) / 2f);
        var size = CreateVector3(
            Math.Abs(lastX - firstX) + 1f,
            colliderHeight,
            Math.Abs(lastZ - firstZ) + 1f);

        var rootObject = _rootObjectField!.GetValue(service)!;
        object? collider = null;
        try
        {
            collider = _addComponentByTypeMethod!.Invoke(
                rootObject,
                new object[] { _boxColliderType! })!;

            WriteMember(collider, _colliderCenter!, center);
            WriteMember(collider, _colliderSize!, size);
            return collider;
        }
        catch
        {
            // If AddComponent succeeded but configuring the collider failed, do
            // not leave an untracked component behind.
            TryDestroyCollider(collider);
            throw;
        }
    }

    private static int ReadIndexedInt(object collection, int index)
    {
        if (collection is Array array)
        {
            return Convert.ToInt32(array.GetValue(index));
        }

        if (collection is IList list)
        {
            return Convert.ToInt32(list[index]);
        }

        var item = AccessTools.Property(collection.GetType(), "Item");
        if (item is not null)
        {
            return Convert.ToInt32(item.GetValue(collection, new object[] { index }));
        }

        throw new InvalidOperationException(
            $"Unsupported ColumnCount collection type: {collection.GetType().FullName}");
    }

    private static object CreateVector2Int(int x, int y) =>
        Activator.CreateInstance(_vector2IntType!, new object[] { x, y })!;

    private static object CreateVector3(float x, float y, float z) =>
        Activator.CreateInstance(_vector3Type!, new object[] { x, y, z })!;

    private static Func<object, int, object>? CreateColumnValueGetter(MethodInfo method)
    {
        try
        {
            if (!method.ReturnType.IsByRef)
            {
                return (instance, index) =>
                    method.Invoke(instance, new object[] { index })!;
            }

            var valueType = method.ReturnType.GetElementType();
            var declaringType = method.DeclaringType;
            if (valueType is null || declaringType is null)
            {
                return null;
            }

            // MethodInfo.Invoke cannot reliably invoke by-ref-returning methods on
            // Timberborn's Mono runtime. Emit a tiny adapter that calls GetColumn,
            // dereferences TerrainColumn&, boxes the value, and returns it as object.
            var dynamicMethod = new System.Reflection.Emit.DynamicMethod(
                "BenchmarkOptimizer_GetTerrainColumnValue",
                typeof(object),
                new[] { typeof(object), typeof(int) },
                typeof(TimberPhysicsTerrainColliderMergerPatcher).Module,
                true);

            var il = dynamicMethod.GetILGenerator();
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, declaringType);
            il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
            il.Emit(
                method.IsVirtual
                    ? System.Reflection.Emit.OpCodes.Callvirt
                    : System.Reflection.Emit.OpCodes.Call,
                method);

            if (valueType.IsValueType)
            {
                il.Emit(System.Reflection.Emit.OpCodes.Ldobj, valueType);
                il.Emit(System.Reflection.Emit.OpCodes.Box, valueType);
            }
            else
            {
                il.Emit(System.Reflection.Emit.OpCodes.Ldind_Ref);
            }

            il.Emit(System.Reflection.Emit.OpCodes.Ret);
            return (Func<object, int, object>)dynamicMethod.CreateDelegate(
                typeof(Func<object, int, object>));
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: TimberPhysics terrain GetColumn by-ref adapter failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static MemberInfo? FindMember(Type type, string name)
    {
        // AccessTools handles ordinary inherited fields/properties. Timberborn 1.1
        // also exposes some terrain-column members through interfaces, and explicit
        // interface implementations are not necessarily discoverable by the simple
        // AccessTools.Property(type, "Floor") lookup.
        var direct = (MemberInfo?)AccessTools.Field(type, name) ?? AccessTools.Property(type, name);
        if (direct is not null)
        {
            return direct;
        }

        for (var current = type; current is not null; current = current.BaseType)
        {
            const BindingFlags flags =
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.DeclaredOnly;

            foreach (var field in current.GetFields(flags))
            {
                if (MemberNameMatches(field.Name, name))
                {
                    return field;
                }
            }

            foreach (var property in current.GetProperties(flags))
            {
                if (MemberNameMatches(property.Name, name))
                {
                    return property;
                }
            }
        }

        foreach (var iface in type.GetInterfaces())
        {
            var field = AccessTools.Field(iface, name);
            if (field is not null)
            {
                return field;
            }

            var property = AccessTools.Property(iface, name);
            if (property is not null)
            {
                return property;
            }

            foreach (var candidate in iface.GetProperties())
            {
                if (MemberNameMatches(candidate.Name, name))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static bool MemberNameMatches(string actualName, string wantedName)
    {
        if (string.Equals(actualName, wantedName, StringComparison.Ordinal))
        {
            return true;
        }

        var separator = actualName.LastIndexOf('.');
        return separator >= 0 &&
               string.Equals(actualName[(separator + 1)..], wantedName, StringComparison.Ordinal);
    }

    private static object? ReadMember(object instance, MemberInfo member)
    {
        var runtimeType = instance.GetType();

        if (member is FieldInfo field)
        {
            if (field.DeclaringType is not null &&
                !field.DeclaringType.IsInstanceOfType(instance))
            {
                var rebound = FindMember(runtimeType, TerminalMemberName(field.Name));
                if (rebound is not null && !ReferenceEquals(rebound, member))
                {
                    return ReadMember(instance, rebound);
                }
            }

            return field.GetValue(instance);
        }

        if (member is PropertyInfo property)
        {
            var getter = property.GetGetMethod(true);
            if (getter is not null &&
                getter.DeclaringType is not null &&
                getter.DeclaringType.IsInstanceOfType(instance))
            {
                return getter.Invoke(instance, null);
            }

            // Re-resolve against the actual boxed/runtime type. This handles
            // Timberborn properties discovered through an interface/base metadata
            // view whose PropertyInfo cannot be invoked directly on the object Mono
            // gives us at runtime.
            var rebound = FindMember(runtimeType, TerminalMemberName(property.Name));
            if (rebound is not null && !SameMember(rebound, member))
            {
                return ReadMember(instance, rebound);
            }

            if (property.DeclaringType?.IsInterface == true && getter is not null)
            {
                var map = runtimeType.GetInterfaceMap(property.DeclaringType);
                for (var i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i] == getter)
                    {
                        return map.TargetMethods[i].Invoke(instance, null);
                    }
                }
            }

            throw new TargetException(
                $"Cannot read {property.DeclaringType?.FullName}.{property.Name} " +
                $"from runtime type {runtimeType.FullName}");
        }

        throw new InvalidOperationException($"Unsupported member: {member}");
    }

    private static string TerminalMemberName(string name)
    {
        var separator = name.LastIndexOf('.');
        return separator >= 0 ? name[(separator + 1)..] : name;
    }

    private static bool SameMember(MemberInfo left, MemberInfo right)
    {
        try
        {
            return left.Module == right.Module &&
                   left.MetadataToken == right.MetadataToken;
        }
        catch
        {
            return ReferenceEquals(left, right);
        }
    }

    private static void WriteMember(object instance, MemberInfo member, object value)
    {
        var runtimeType = instance.GetType();

        switch (member)
        {
            case FieldInfo field:
                if (field.DeclaringType is not null &&
                    !field.DeclaringType.IsInstanceOfType(instance))
                {
                    var rebound = FindMember(runtimeType, TerminalMemberName(field.Name));
                    if (rebound is not null && !SameMember(rebound, member))
                    {
                        WriteMember(instance, rebound, value);
                        return;
                    }
                }

                field.SetValue(instance, value);
                return;

            case PropertyInfo property:
                var setter = property.GetSetMethod(true);
                if (setter is not null &&
                    setter.DeclaringType is not null &&
                    setter.DeclaringType.IsInstanceOfType(instance))
                {
                    setter.Invoke(instance, new[] { value });
                    return;
                }

                var reboundProperty = FindMember(runtimeType, TerminalMemberName(property.Name));
                if (reboundProperty is not null && !SameMember(reboundProperty, member))
                {
                    WriteMember(instance, reboundProperty, value);
                    return;
                }

                if (property.DeclaringType?.IsInterface == true && setter is not null)
                {
                    var map = runtimeType.GetInterfaceMap(property.DeclaringType);
                    for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        if (map.InterfaceMethods[i] == setter)
                        {
                            map.TargetMethods[i].Invoke(instance, new[] { value });
                            return;
                        }
                    }
                }

                throw new TargetException(
                    $"Cannot write {property.DeclaringType?.FullName}.{property.Name} " +
                    $"on runtime type {runtimeType.FullName}");

            default:
                throw new InvalidOperationException($"Unsupported member: {member}");
        }
    }

    private static long ChunkKey(int x, int y) =>
        ((long)x << 32) | (uint)y;

    private static void ClearState(TerrainState state)
    {
        foreach (var key in state.Chunks.Keys.ToArray())
        {
            RemoveChunk(state, key);
        }
    }

    private static void RemoveChunk(TerrainState state, long key)
    {
        if (!state.Chunks.TryGetValue(key, out var colliders))
        {
            return;
        }

        foreach (var collider in colliders)
        {
            TryDestroyCollider(collider);
        }

        state.Chunks.Remove(key);
    }

    private static void TryDestroyCollider(object? collider)
    {
        if (collider is null)
        {
            return;
        }

        try
        {
            _destroyMethod!.Invoke(null, new[] { collider });
        }
        catch
        {
            // A destroyed collider should not prevent rebuilding/recovery.
        }
    }

    private sealed class TerrainState
    {
        public Dictionary<long, List<object>> Chunks { get; } = new();
    }
}

internal static class Runtime
{
    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<object, CounterState> Counters = new();
    private static readonly ConditionalWeakTable<object, OwnerDispatchPlans> DispatchPlans = new();
    private static readonly Dictionary<string, ItemAdapter> AdapterCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> UnsupportedAdapters = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedWarnings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, BenchStat> BenchStats = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Discovered = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> OriginByType = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> AssemblyNameByType = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> OriginByAssemblyPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> OriginByAssemblyName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Type> DiscoveredTypes = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, MethodInfo> DirectMethodsByType = new(StringComparer.Ordinal);
    private static readonly HashSet<MethodBase> DirectPatchedMethods = new();
    private static Harmony? _harmony;
    private static bool _benchmarkRunning;
    private static int _settingsGeneration;

    private static Settings _settings = new();
    private static DateTime _nextReloadUtc = DateTime.MinValue;
    private static DateTime _configWriteUtc = DateTime.MinValue;
    private static long _benchmarkStart;
    private static bool _benchmarkCompleted;

    public static string ModPath { get; private set; } = "";
    public static string ConfigPath => Path.Combine(ModPath, "optimizer-v11.json");
    private static string DiscoveredPath => Path.Combine(ModPath, "optimizer-v11-discovered.txt");
    private static string FreezeLogPath => Path.Combine(ModPath, "optimizer-v11-freezes.log");

    public const int BenchmarkDurationSeconds = 120;

    // These systems own or synchronize authoritative simulation state. Skipping
    // their updates can break ordering assumptions and leave the world in a
    // partially-updated state, so they are permanently pinned to vanilla cadence.
    private static readonly HashSet<string> ProtectedTypeNames = new(StringComparer.Ordinal)
    {
        "PhysicsSimulator",
        "BehaviorManager",
        "NavMeshObserver",
        "NavigationSynchronizer",
        "AutomationRunner",
        "WaterSimulator",
        "SpeedManager",
        "ConstructionSite"
    };

    public static bool Enabled => _settings.Enabled;
    public static int DefaultInterval => _settings.DefaultInterval;
    public static bool IsProtected(string typeName) =>
        ProtectedTypeNames.Contains(ShortTypeName(typeName));
    public static IReadOnlyCollection<string> KnownTypes
    {
        get
        {
            lock (Sync)
            {
                return Discovered.Select(ShortTypeName).Distinct(StringComparer.Ordinal).OrderBy(x => x).ToArray();
            }
        }
    }

    public static string GetOrigin(string typeName)
    {
        lock (Sync)
        {
            if (OriginByType.TryGetValue(typeName, out var direct))
            {
                return direct;
            }

            var full = Discovered.FirstOrDefault(x => ShortTypeName(x) == typeName);
            return full is not null && OriginByType.TryGetValue(full, out var origin)
                ? origin
                : "UNKNOWN";
        }
    }

    public static string? GetThrottleOwnershipNote(string typeName)
    {
        Type? type;
        lock (Sync)
        {
            type = ResolveDiscoveredTypeLocked(typeName);
        }

        if (type is null)
        {
            return null;
        }

        var method = FindDirectTickMethod(type);
        var declaringType = method?.DeclaringType;
        if (method is null || declaringType is null || declaringType == type)
        {
            return null;
        }

        var depth = InheritanceDepth(type, declaringType);
        var depthText = depth > 0
            ? $" ({depth} inheritance level{(depth == 1 ? "" : "s")})"
            : "";

        return $"Inherited {method.Name}{depthText}. Effective throttle target: {FormatMethodTarget(method)}. " +
               "Shared base method is patched once; this concrete type keeps its own interval.";
    }

    public static string Mode =>
        BenchmarkActive ? "Benchmarking" :
        HasActiveThrottles() ? "Throttling" :
        "Vanilla fast path";

    public static bool IsBenchmarking => BenchmarkActive;
    public static double BenchmarkRemainingSeconds
    {
        get
        {
            if (!BenchmarkActive) return 0;
            var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - _benchmarkStart)
                          / (double)System.Diagnostics.Stopwatch.Frequency;
            return Math.Max(0, BenchmarkDurationSeconds - elapsed);
        }
    }

    public static void SetEnabled(bool value)
    {
        _settings.Enabled = value;
        _settingsGeneration++;
        SaveSettings(_settings);
        RefreshDirectThrottlePatches();
    }

    public static void SetDefaultInterval(int value)
    {
        _settings.DefaultInterval = Math.Clamp(value, 1, 1000);
        _settingsGeneration++;
        SaveSettings(_settings);
        RefreshDirectThrottlePatches();
    }

    public static int GetInterval(string typeName)
    {
        if (IsProtected(typeName))
        {
            return 1;
        }

        if (_settings.Intervals.TryGetValue(typeName, out var value))
        {
            return Math.Clamp(value, 1, 1000);
        }

        var full = Discovered.FirstOrDefault(x => ShortTypeName(x) == typeName);
        if (full is not null && _settings.Intervals.TryGetValue(full, out value))
        {
            return Math.Clamp(value, 1, 1000);
        }

        return _settings.DefaultInterval;
    }

    public static void SetInterval(string typeName, int value)
    {
        var key = Discovered.FirstOrDefault(x => ShortTypeName(x) == typeName) ?? typeName;

        if (IsProtected(typeName))
        {
            _settings.Intervals[key] = 1;
            _settingsGeneration++;
            SaveSettings(_settings);
            RefreshDirectThrottlePatches();
            LogOnce($"protected-set:{ShortTypeName(typeName)}",
                $"protected system {typeName} is permanently pinned to interval=1");
            return;
        }

        value = Math.Clamp(value, 1, 1000);
        _settings.Intervals[key] = value;
        _settingsGeneration++;
        SaveSettings(_settings);
        RefreshDirectThrottlePatches();
    }

    public static void ResetIntervals()
    {
        _settings.DefaultInterval = 1;
        _settings.Intervals.Clear();
        _settingsGeneration++;
        SaveSettings(_settings);
        RefreshDirectThrottlePatches();
        Log("all optimizer intervals reset to vanilla (1)");
    }

    public static void StartBenchmark()
    {
        lock (Sync)
        {
            BenchStats.Clear();
        }

        RemoveDirectThrottlePatches();
        if (_harmony is not null)
        {
            TimberPhysicsCatchUpLimiterPatcher.Patch(_harmony);
            DispatcherPatcher.Patch(_harmony);
            TimberPhysicsBenchmarkPatcher.Patch(_harmony);
        }

        _benchmarkStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _benchmarkCompleted = false;
        _benchmarkRunning = true;
        Log($"benchmark started for {BenchmarkDurationSeconds}s; output will be written under {ModPath}");
    }

    private static string ShortTypeName(string value)
    {
        var i = value.LastIndexOf('.');
        return i < 0 ? value : value[(i + 1)..];
    }

    private static Type? ResolveDiscoveredTypeLocked(string typeName)
    {
        if (DiscoveredTypes.TryGetValue(typeName, out var exact))
        {
            return exact;
        }

        foreach (var pair in DiscoveredTypes)
        {
            if (string.Equals(ShortTypeName(pair.Key), typeName, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static int InheritanceDepth(Type concreteType, Type declaringType)
    {
        var depth = 0;
        for (var current = concreteType; current is not null; current = current.BaseType)
        {
            if (current == declaringType)
            {
                return depth;
            }

            depth++;
        }

        return -1;
    }

    private static string FormatMethodTarget(MethodInfo method)
    {
        var declaring = method.DeclaringType;
        return declaring is null
            ? method.Name
            : $"{FormatTypeName(declaring)}.{method.Name}";
    }

    private static string FormatTypeName(Type type)
    {
        if (type.IsByRef)
        {
            return FormatTypeName(type.GetElementType()!) + "&";
        }

        if (type.IsArray)
        {
            return FormatTypeName(type.GetElementType()!) + "[]";
        }

        if (!type.IsGenericType)
        {
            return type.FullName?.Replace('+', '.') ?? type.Name;
        }

        var genericDefinition = type.GetGenericTypeDefinition();
        var baseName = genericDefinition.FullName ?? genericDefinition.Name;
        var tick = baseName.IndexOf((char)96);
        if (tick >= 0)
        {
            baseName = baseName[..tick];
        }

        baseName = baseName.Replace('+', '.');
        var arguments = string.Join(", ", type.GetGenericArguments().Select(FormatTypeName));
        return $"{baseName}<{arguments}>";
    }

    public static void Initialize(string modPath, Harmony harmony)
    {
        _harmony = harmony;
        ModPath = modPath;
        Directory.CreateDirectory(ModPath);
        ResetSessionLogs();
        Log("session log started; previous optimizer and freeze logs cleared");

        if (!File.Exists(ConfigPath))
        {
            _settings = Settings.CreateDefault();
            SaveSettings(_settings);
        }

        ReloadConfig(force: true);

        // Benchmarks are session-only and must never be resumed from persisted settings.
        _settings.BenchmarkSeconds = 0;
        _benchmarkRunning = false;
        _benchmarkCompleted = true;
        _benchmarkStart = System.Diagnostics.Stopwatch.GetTimestamp();

        FreezeDetectorPatcher.Patch();
        TimberPhysicsCatchUpLimiterPatcher.Patch(harmony);
        TimberPhysicsTerrainColliderMergerPatcher.Patch(harmony);
        DiscoverLoadedOptimizableTypes();
        RefreshDirectThrottlePatches();
    }

    public static void RefreshDirectThrottlePatches()
    {
        if (_harmony is null || BenchmarkActive)
        {
            return;
        }

        RemoveDirectThrottlePatches();

        if (!_settings.Enabled)
        {
            return;
        }

        foreach (var pair in DiscoveredTypes)
        {
            var type = pair.Value;
            if (IsProtected(type) || GetInterval(type) <= 1)
            {
                continue;
            }

            var method = FindDirectTickMethod(type);
            if (method is null)
            {
                LogOnce(
                    $"no-direct-method:{type.FullName}",
                    $"warning: no directly patchable Tick/Update method found for {type.FullName}; leaving vanilla");
                continue;
            }

            method = ResolveImplementedMethod(method);
            var declaringType = method.DeclaringType;
            var inherited = declaringType is not null && declaringType != type;

            if (inherited)
            {
                var depth = InheritanceDepth(type, declaringType!);
                var effectiveTarget = FormatMethodTarget(method);
                LogOnce(
                    $"inherited-direct-method:{type.FullName}",
                    $"nested throttle mapping: {type.FullName}.{method.Name} is inherited" +
                    $"{(depth > 0 ? $" through {depth} inheritance level(s)" : "")}; " +
                    $"effective throttle target={effectiveTarget}; sibling runtime types keep their own intervals");
            }

            try
            {
                var prefix = AccessTools.Method(typeof(Runtime), nameof(DirectThrottlePrefix));

                // Several concrete types can share the same inherited method. Patch the
                // effective method only once; DirectThrottlePrefix uses __instance.GetType()
                // so each concrete sibling still gets its own configured interval.
                if (DirectPatchedMethods.Add(method))
                {
                    _harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(prefix) { priority = Priority.First });

                    Log(
                        inherited
                            ? $"direct throttle patch installed: effective target {FormatMethodTarget(method)}"
                            : $"direct throttle patch installed: {type.FullName}.{method.Name}");
                }

                DirectMethodsByType[pair.Key] = method;
                Log(
                    inherited
                        ? $"direct throttle binding: {type.FullName} interval={GetInterval(type)} -> {FormatMethodTarget(method)}"
                        : $"direct throttle binding: {type.FullName} interval={GetInterval(type)}");
            }
            catch (Exception ex)
            {
                DirectPatchedMethods.Remove(method);
                LogOnce(
                    $"direct-patch-failed:{type.FullName}:{method.Name}",
                    $"warning: direct throttle patch failed for {type.FullName}.{method.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void RemoveDirectThrottlePatches()
    {
        if (_harmony is null || DirectPatchedMethods.Count == 0)
        {
            return;
        }

        foreach (var method in DirectPatchedMethods.ToArray())
        {
            try
            {
                _harmony.Unpatch(method, HarmonyPatchType.Prefix, _harmony.Id);
            }
            catch
            {
                // A stale direct patch must never break settings changes.
            }
        }

        DirectPatchedMethods.Clear();
        DirectMethodsByType.Clear();
    }

    public static bool DirectThrottlePrefix(object __instance, MethodBase __originalMethod)
    {
        if (!_settings.Enabled || BenchmarkActive || IsProtected(__instance.GetType()))
        {
            return true;
        }

        return ShouldRun(__instance);
    }

    private static MethodInfo ResolveImplementedMethod(MethodInfo method)
    {
        var declaringType = method.DeclaringType;
        if (declaringType is null || method.ReflectedType == declaringType)
        {
            return method;
        }

        var parameterTypes = method.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();

        return AccessTools.DeclaredMethod(declaringType, method.Name, parameterTypes)
            ?? method;
    }

    private static MethodInfo? FindDirectTickMethod(Type type)
    {
        foreach (var methodName in new[] { "Tick", "UpdateSingleton", "LateUpdateSingleton", "StartAndTick" })
        {
            var method = AccessTools.Method(type, methodName, Type.EmptyTypes);
            if (method is not null && method.ReturnType == typeof(void))
            {
                return method;
            }
        }

        return null;
    }

    private static void DiscoverLoadedOptimizableTypes()
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(x => x is not null).Cast<Type>().ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                var method = FindDirectTickMethod(type);
                if (method is null)
                {
                    continue;
                }

                var interfaces = type.GetInterfaces()
                    .Select(i => i.FullName ?? i.Name)
                    .ToArray();

                var looksOptimizable =
                    interfaces.Any(n =>
                        n == "Timberborn.TickSystem.ITickableSingleton" ||
                        n == "Timberborn.SingletonSystem.IUpdatableSingleton" ||
                        n == "Timberborn.SingletonSystem.ILateUpdatableSingleton" ||
                        n.Contains("ITickable", StringComparison.Ordinal)) ||
                    type.BaseType?.FullName == "Timberborn.TickSystem.TickableComponent";

                if (looksOptimizable)
                {
                    Discover(type);
                }
            }
        }

        Log($"direct throttle discovery complete: {DiscoveredTypes.Count} type(s)");
    }

    public static bool TryDispatch(
        object owner,
        string listFieldName,
        string? actualFieldName,
        string invokeMethodName,
        string? enabledMemberName = null)
    {
        ReloadConfig(force: false);

        if (!_settings.Enabled)
        {
            return true;
        }

        if (!BenchmarkActive && !HasActiveThrottles())
        {
            return true;
        }

        try
        {
            var listField = AccessTools.Field(owner.GetType(), listFieldName);
            if (listField?.GetValue(owner) is not IList list)
            {
                LogOnce(
                    $"missing-list:{owner.GetType().FullName}:{listFieldName}",
                    $"warning: {owner.GetType().FullName}.{listFieldName} unavailable; falling back to vanilla");
                return true;
            }

            var plan = GetDispatchPlan(
                owner,
                list,
                listFieldName,
                actualFieldName,
                invokeMethodName,
                enabledMemberName);

            if (plan is null)
            {
                return true;
            }

            // Critical fast path: an active optimization elsewhere must not force this
            // owner through our dispatcher. Let Timberborn run it natively unless this
            // exact owner contains a throttled type.
            if (!BenchmarkActive && !plan.HasThrottle)
            {
                return true;
            }

            foreach (var entry in plan.Entries)
            {
                if (!entry.Adapter.IsEnabled(entry.Item))
                {
                    continue;
                }

                if (!ShouldRun(entry.Actual))
                {
                    continue;
                }

                if (BenchmarkActive)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    entry.Adapter.Invoke(entry.Item);
                    RecordBenchmark(entry.ActualType, System.Diagnostics.Stopwatch.GetTimestamp() - started);
                }
                else
                {
                    entry.Adapter.Invoke(entry.Item);
                }
            }

            FinishBenchmarkIfNeeded();
            return false;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        catch (Exception ex)
        {
            LogOnce(
                $"dispatch:{owner.GetType().FullName}:{listFieldName}:{ex.GetType().FullName}:{ex.Message}",
                $"warning: optimizer dispatch failed before safe completion: {ex.GetType().Name}: {ex.Message}; falling back to vanilla");
            return true;
        }
    }

    private static DispatchPlan? GetDispatchPlan(
        object owner,
        IList list,
        string listFieldName,
        string? actualFieldName,
        string invokeMethodName,
        string? enabledMemberName)
    {
        var ownerPlans = DispatchPlans.GetOrCreateValue(owner);

        lock (ownerPlans.Gate)
        {
            if (ownerPlans.Plans.TryGetValue(listFieldName, out var cached)
                && cached.SettingsGeneration == _settingsGeneration
                && cached.ListCount == list.Count
                && SameBoundaryItems(cached, list))
            {
                return cached;
            }

            var entries = new List<DispatchEntry>(list.Count);
            var hasThrottle = false;

            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item is null)
                {
                    continue;
                }

                var adapter = GetAdapter(
                    item.GetType(),
                    actualFieldName,
                    invokeMethodName,
                    enabledMemberName);

                if (adapter is null)
                {
                    LogOnce(
                        $"unsupported:{item.GetType().FullName}:{listFieldName}",
                        $"warning: cannot adapt {item.GetType().FullName} in {listFieldName}; falling back to vanilla");
                    return null;
                }

                var actual = adapter.GetActual(item);
                if (actual is null)
                {
                    continue;
                }

                var actualType = actual.GetType();
                Discover(actualType);

                if (GetInterval(actualType) > 1)
                {
                    hasThrottle = true;
                }

                entries.Add(new DispatchEntry(item, adapter, actual, actualType));
            }

            var plan = new DispatchPlan(
                _settingsGeneration,
                list.Count,
                list.Count > 0 ? list[0] : null,
                list.Count > 1 ? list[list.Count - 1] : null,
                hasThrottle,
                entries.ToArray());

            ownerPlans.Plans[listFieldName] = plan;
            return plan;
        }
    }

    private static bool SameBoundaryItems(DispatchPlan plan, IList list)
    {
        if (list.Count == 0)
        {
            return true;
        }

        if (!ReferenceEquals(plan.FirstItem, list[0]))
        {
            return false;
        }

        return list.Count <= 1 || ReferenceEquals(plan.LastItem, list[list.Count - 1]);
    }

    private static ItemAdapter? GetAdapter(Type itemType, string? actualFieldName, string methodName, string? enabledMemberName)
    {
        var cacheKey = $"{itemType.AssemblyQualifiedName}|{actualFieldName}|{methodName}|{enabledMemberName}";

        lock (Sync)
        {
            if (AdapterCache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            if (UnsupportedAdapters.Contains(cacheKey))
            {
                return null;
            }

            FieldInfo? actualField = null;
            if (!string.IsNullOrEmpty(actualFieldName))
            {
                actualField = AccessTools.Field(itemType, actualFieldName);
                if (actualField is null)
                {
                    UnsupportedAdapters.Add(cacheKey);
                    return null;
                }
            }

            var method = AccessTools.Method(itemType, methodName, Type.EmptyTypes);
            if (method is null && methodName == "StartAndTick")
            {
                method = AccessTools.Method(itemType, "Tick", Type.EmptyTypes);
            }

            if (method is null)
            {
                UnsupportedAdapters.Add(cacheKey);
                return null;
            }

            try
            {
                var itemParameter = Expression.Parameter(typeof(object), "item");
                var typedItem = Expression.Convert(itemParameter, itemType);

                Func<object, object?> getActual;
                if (actualField is not null)
                {
                    var fieldAccess = Expression.Field(typedItem, actualField);
                    var boxedField = Expression.Convert(fieldAccess, typeof(object));
                    getActual = Expression.Lambda<Func<object, object?>>(boxedField, itemParameter).Compile();
                }
                else
                {
                    getActual = item => item;
                }

                var call = Expression.Call(typedItem, method);
                var invoke = Expression.Lambda<Action<object>>(call, itemParameter).Compile();

                Func<object, bool> enabled = _ => true;
                if (!string.IsNullOrEmpty(enabledMemberName))
                {
                    var prop = AccessTools.Property(itemType, enabledMemberName);
                    var field = AccessTools.Field(itemType, enabledMemberName);
                    Expression? enabledExpression = null;

                    if (prop?.PropertyType == typeof(bool) && prop.GetMethod is not null)
                    {
                        enabledExpression = Expression.Property(typedItem, prop);
                    }
                    else if (field?.FieldType == typeof(bool))
                    {
                        enabledExpression = Expression.Field(typedItem, field);
                    }

                    if (enabledExpression is not null)
                    {
                        enabled = Expression.Lambda<Func<object, bool>>(enabledExpression, itemParameter).Compile();
                    }
                }

                var adapter = new ItemAdapter(
                    actualFieldName,
                    enabledMemberName,
                    getActual,
                    invoke,
                    enabled);

                AdapterCache[cacheKey] = adapter;
                return adapter;
            }
            catch
            {
                UnsupportedAdapters.Add(cacheKey);
                return null;
            }
        }
    }

    private static bool ShouldRun(object actual)
    {
        if (IsProtected(actual.GetType()))
        {
            return true;
        }

        var interval = GetInterval(actual.GetType());
        if (interval <= 1)
        {
            return true;
        }

        var state = Counters.GetOrCreateValue(actual);
        if (state.Remaining <= 0)
        {
            state.Remaining = interval - 1;
            return true;
        }

        state.Remaining--;
        return false;
    }

    private static bool IsProtected(Type type) =>
        ProtectedTypeNames.Contains(type.Name);

    private static bool NormalizeProtectedIntervals(Settings settings)
    {
        var changed = false;

        foreach (var key in settings.Intervals.Keys.ToArray())
        {
            if (IsProtected(key) && settings.Intervals[key] != 1)
            {
                settings.Intervals[key] = 1;
                changed = true;
            }
        }

        return changed;
    }

    private static bool HasActiveThrottles()
    {
        if (_settings.DefaultInterval > 1)
        {
            return true;
        }

        return _settings.Intervals.Values.Any(value => value > 1);
    }

    private static int GetInterval(Type type)
    {
        if (IsProtected(type))
        {
            return 1;
        }

        var full = type.FullName ?? type.Name;

        if (_settings.Intervals.TryGetValue(full, out var exact))
        {
            return Math.Clamp(exact, 1, 1000);
        }

        if (_settings.Intervals.TryGetValue(type.Name, out var shortName))
        {
            return Math.Clamp(shortName, 1, 1000);
        }

        return Math.Clamp(_settings.DefaultInterval, 1, 1000);
    }

    private static void Discover(Type type)
    {
        var name = type.FullName ?? type.Name;

        lock (Sync)
        {
            if (Discovered.Contains(name))
            {
                return;
            }
        }

        // Origin resolution can touch paths/manifests. Do it only once per discovered type,
        // never on the per-frame hot path.
        var origin = ResolveOrigin(type);
        var assemblyName = type.Assembly.GetName().Name ?? "";

        lock (Sync)
        {
            if (!Discovered.Add(name))
            {
                return;
            }

            OriginByType[name] = origin;
            AssemblyNameByType[name] = assemblyName;
            DiscoveredTypes[name] = type;

            if (_settings.WriteDiscoveredTypes)
            {
                File.AppendAllText(
                    DiscoveredPath,
                    $"{name}\t{origin}{Environment.NewLine}");
            }
        }
    }

    public static void RegisterModAssemblyOrigin(string assemblyName, string origin)
    {
        if (string.IsNullOrWhiteSpace(assemblyName) || string.IsNullOrWhiteSpace(origin))
        {
            return;
        }

        lock (Sync)
        {
            OriginByAssemblyName[assemblyName] = origin;

            foreach (var pair in AssemblyNameByType)
            {
                if (string.Equals(pair.Value, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    OriginByType[pair.Key] = origin;
                }
            }
        }
    }

    public static void RegisterModPathOrigin(string path, string origin)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(origin))
        {
            return;
        }

        try
        {
            path = Path.GetFullPath(path);
        }
        catch
        {
            return;
        }

        lock (Sync)
        {
            OriginByAssemblyPath[path] = origin;
        }
    }

    private static string ResolveOrigin(Type type)
    {
        string location;
        try
        {
            location = type.Assembly.Location ?? "";
        }
        catch
        {
            location = "";
        }

        if (string.IsNullOrWhiteSpace(location))
        {
            var assemblyName = type.Assembly.GetName().Name ?? "";
            lock (Sync)
            {
                return OriginByAssemblyName.TryGetValue(assemblyName, out var mapped)
                    ? mapped
                    : $"UNKNOWN: {assemblyName}";
            }
        }

        var fullPath = Path.GetFullPath(location);
        lock (Sync)
        {
            if (OriginByAssemblyPath.TryGetValue(fullPath, out var cached))
            {
                return cached;
            }
        }

        var normalized = fullPath.Replace('\\', '/');
        string origin;

        lock (Sync)
        {
            if (OriginByAssemblyPath.TryGetValue(fullPath, out var exactPathOrigin))
            {
                return exactPathOrigin;
            }

            var assemblyName = type.Assembly.GetName().Name ?? "";
            if (OriginByAssemblyName.TryGetValue(assemblyName, out var assemblyOrigin))
            {
                return assemblyOrigin;
            }
        }

        if (normalized.Contains("/Timberborn_Data/Managed/", StringComparison.OrdinalIgnoreCase))
        {
            origin = "CORE";
        }
        else
        {
            var manifest = FindNearestManifest(fullPath);
            if (manifest is not null)
            {
                origin = DescribeModOrigin(manifest, normalized);
            }
            else
            {
                var workshopMarker = "/steamapps/workshop/content/1062090/";
                var workshopIndex = normalized.IndexOf(workshopMarker, StringComparison.OrdinalIgnoreCase);
                if (workshopIndex >= 0)
                {
                    var rest = normalized[(workshopIndex + workshopMarker.Length)..];
                    var workshopId = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "?";
                    origin = $"MOD: Workshop {workshopId}";
                }
                else
                {
                    var localMarker = "/Timberborn/Mods/";
                    var localIndex = normalized.IndexOf(localMarker, StringComparison.OrdinalIgnoreCase);
                    if (localIndex >= 0)
                    {
                        var rest = normalized[(localIndex + localMarker.Length)..];
                        var folder = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "local";
                        origin = $"MOD: {folder} (local)";
                    }
                    else
                    {
                        origin = $"OTHER: {type.Assembly.GetName().Name}";
                    }
                }
            }
        }

        lock (Sync)
        {
            OriginByAssemblyPath[fullPath] = origin;
        }

        return origin;
    }

    private static string? FindNearestManifest(string assemblyPath)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetDirectoryName(assemblyPath)!);
            for (var depth = 0; directory is not null && depth < 6; depth++, directory = directory.Parent)
            {
                var manifest = Path.Combine(directory.FullName, "manifest.json");
                if (File.Exists(manifest))
                {
                    return manifest;
                }
            }
        }
        catch
        {
            // Origin enrichment is diagnostic only.
        }

        return null;
    }

    private static string DescribeModOrigin(string manifestPath, string normalizedAssemblyPath)
    {
        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonConvert.DeserializeObject<Dictionary<string, object?>>(json);
            manifest ??= new Dictionary<string, object?>();

            var name = manifest.TryGetValue("Name", out var nameValue)
                ? Convert.ToString(nameValue)
                : null;
            var id = manifest.TryGetValue("Id", out var idValue)
                ? Convert.ToString(idValue)
                : null;

            var workshopMarker = "/steamapps/workshop/content/1062090/";
            var workshopIndex = normalizedAssemblyPath.IndexOf(workshopMarker, StringComparison.OrdinalIgnoreCase);
            string? suffix = null;

            if (workshopIndex >= 0)
            {
                var rest = normalizedAssemblyPath[(workshopIndex + workshopMarker.Length)..];
                var workshopId = rest.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(workshopId))
                {
                    suffix = $"Workshop {workshopId}";
                }
            }
            else if (normalizedAssemblyPath.Contains("/Timberborn/Mods/", StringComparison.OrdinalIgnoreCase))
            {
                suffix = "local";
            }

            var display = !string.IsNullOrWhiteSpace(name)
                ? name!
                : !string.IsNullOrWhiteSpace(id) ? id! : Path.GetFileName(Path.GetDirectoryName(manifestPath));

            if (!string.IsNullOrWhiteSpace(id) && !string.Equals(display, id, StringComparison.Ordinal))
            {
                display += $" ({id})";
            }

            return suffix is null
                ? $"MOD: {display}"
                : $"MOD: {display} [{suffix}]";
        }
        catch
        {
            return normalizedAssemblyPath.Contains("/Timberborn/Mods/", StringComparison.OrdinalIgnoreCase)
                ? "MOD: local"
                : "MOD";
        }
    }

    private static bool BenchmarkActive =>
        _benchmarkRunning && !_benchmarkCompleted;

    private static void RecordBenchmark(Type type, long elapsedTicks)
    {
        var name = type.FullName ?? type.Name;

        lock (Sync)
        {
            if (!BenchStats.TryGetValue(name, out var stat))
            {
                stat = new BenchStat { Origin = ResolveOrigin(type) };
                BenchStats[name] = stat;
            }

            stat.Count++;
            stat.TotalTicks += elapsedTicks;
            if (elapsedTicks < stat.MinTicks) stat.MinTicks = elapsedTicks;
            if (elapsedTicks > stat.MaxTicks) stat.MaxTicks = elapsedTicks;
        }

        FreezeDetector.RecordSystem(name, elapsedTicks);
    }

    public static string ResolveDiagnosticOrigin(Type type) => ResolveOrigin(type);

    public static void RecordDiagnosticBenchmark(string name, string origin, long elapsedTicks)
    {
        if (!BenchmarkActive || elapsedTicks < 0)
        {
            return;
        }

        lock (Sync)
        {
            if (!BenchStats.TryGetValue(name, out var stat))
            {
                stat = new BenchStat { Origin = origin };
                BenchStats[name] = stat;
            }

            stat.Count++;
            stat.TotalTicks += elapsedTicks;
            if (elapsedTicks < stat.MinTicks) stat.MinTicks = elapsedTicks;
            if (elapsedTicks > stat.MaxTicks) stat.MaxTicks = elapsedTicks;
        }

        FreezeDetector.RecordSystem(name, elapsedTicks);
    }

    private static void FinishBenchmarkIfNeeded()
    {
        if (!BenchmarkActive)
        {
            return;
        }

        var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - _benchmarkStart)
                      / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsed < BenchmarkDurationSeconds)
        {
            return;
        }

        _benchmarkCompleted = true;
        _benchmarkRunning = false;

        var file = Path.Combine(ModPath, $"optimizer-v11-benchmark-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var lines = new List<string> { "Type,Origin,TotalMs,Count,AverageMs,MinMs,MaxMs" };

        lock (Sync)
        {
            foreach (var pair in BenchStats.OrderByDescending(x => x.Value.TotalTicks))
            {
                var s = pair.Value;
                var totalMs = TicksToMs(s.TotalTicks);
                var minMs = s.Count == 0 ? 0 : TicksToMs(s.MinTicks);
                var maxMs = s.Count == 0 ? 0 : TicksToMs(s.MaxTicks);
                var avgMs = s.Count == 0 ? 0 : totalMs / s.Count;
                lines.Add($"{Csv(pair.Key)},{Csv(s.Origin)},{totalMs:F6},{s.Count},{avgMs:F6},{minMs:F6},{maxMs:F6}");
            }
        }

        File.WriteAllLines(file, lines);

        if (_harmony is not null)
        {
            TimberPhysicsBenchmarkPatcher.Unpatch(_harmony);
            DispatcherPatcher.Unpatch(_harmony);

            // The benchmark cleanup removes all patches under our Harmony ID from
            // PhysicsSimulator, so restore the persistent catch-up limiter.
            TimberPhysicsCatchUpLimiterPatcher.Reapply(_harmony);
        }

        RefreshDirectThrottlePatches();
        Log($"benchmark complete: {file}");
    }

    private static double TicksToMs(long ticks) =>
        ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static string Csv(string value) =>
        "\"" + value.Replace("\"", "\"\"") + "\"";

    private static void ReloadConfig(bool force)
    {
        var now = DateTime.UtcNow;
        if (!force && now < _nextReloadUtc)
        {
            return;
        }

        _nextReloadUtc = now.AddSeconds(Math.Max(1, _settings.ReloadSeconds));

        try
        {
            if (!File.Exists(ConfigPath))
            {
                return;
            }

            var writeUtc = File.GetLastWriteTimeUtc(ConfigPath);
            if (!force && writeUtc == _configWriteUtc)
            {
                return;
            }

            var json = File.ReadAllText(ConfigPath);
            var loaded = JsonConvert.DeserializeObject<Settings>(json);
            if (loaded is null)
            {
                return;
            }

            loaded.Normalize();
            var protectedValuesCorrected = NormalizeProtectedIntervals(loaded);
            _settings = loaded;
            _settingsGeneration++;
            _configWriteUtc = writeUtc;

            if (protectedValuesCorrected)
            {
                SaveSettings(_settings);
                Log("protected optimizer intervals found in config and reset to 1");
            }

            if (!BenchmarkActive)
            {
                RefreshDirectThrottlePatches();
            }
            Log($"config reloaded: {_settings.Intervals.Count} explicit interval(s)");
        }
        catch (Exception ex)
        {
            Log($"warning: config reload failed: {ex.GetType().Name}: {ex.Message}; keeping previous settings");
        }
    }

    private static void SaveSettings(Settings settings)
    {
        NormalizeProtectedIntervals(settings);
        var json = JsonConvert.SerializeObject(settings, Formatting.Indented);
        File.WriteAllText(ConfigPath, json + Environment.NewLine);
        _configWriteUtc = File.GetLastWriteTimeUtc(ConfigPath);
    }


    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(ModPath, "optimizer-v11.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the simulation.
        }
    }

    public static void LogFreeze(string message)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}";
            File.AppendAllText(FreezeLogPath, line);
            File.AppendAllText(Path.Combine(ModPath, "optimizer-v11.log"), line);
        }
        catch
        {
            // Freeze diagnostics must never break the simulation.
        }
    }

    private static void LogOnce(string key, string message)
    {
        lock (Sync)
        {
            if (!LoggedWarnings.Add(key))
            {
                return;
            }
        }

        Log(message);
    }

    private static void ResetSessionLogs()
    {
        try
        {
            File.WriteAllText(Path.Combine(ModPath, "optimizer-v11.log"), string.Empty);
        }
        catch
        {
            // Never block startup because the main diagnostic log cannot be reset.
        }

        try
        {
            File.WriteAllText(FreezeLogPath, string.Empty);
        }
        catch
        {
            // Freeze logging remains best-effort if the previous log is locked.
        }
    }

    private sealed class CounterState
    {
        public int Remaining;
    }

    private sealed class OwnerDispatchPlans
    {
        public object Gate { get; } = new();
        public Dictionary<string, DispatchPlan> Plans { get; } = new(StringComparer.Ordinal);
    }

    private sealed record DispatchPlan(
        int SettingsGeneration,
        int ListCount,
        object? FirstItem,
        object? LastItem,
        bool HasThrottle,
        DispatchEntry[] Entries);

    private sealed record DispatchEntry(
        object Item,
        ItemAdapter Adapter,
        object Actual,
        Type ActualType);

    private sealed class BenchStat
    {
        public string Origin = "UNKNOWN";
        public long Count;
        public long TotalTicks;
        public long MinTicks = long.MaxValue;
        public long MaxTicks;
    }

    private sealed record ItemAdapter(
        string? ActualFieldName,
        string? EnabledMemberName,
        Func<object, object?> ActualGetter,
        Action<object> Invoke,
        Func<object, bool> Enabled)
    {
        public object? GetActual(object item) => ActualGetter(item);
        public bool IsEnabled(object item) => Enabled(item);
    }
}

internal sealed class Settings
{
    public bool Enabled { get; set; } = true;
    public int DefaultInterval { get; set; } = 1;
    public int ReloadSeconds { get; set; } = 2;
    public bool WriteDiscoveredTypes { get; set; } = true;

    // 0 = disabled. Set e.g. 30 to benchmark the first 30 real seconds after loading a game.
    public int BenchmarkSeconds { get; set; } = 0;

    // Key may be a full type name (preferred) or a short class name.
    // 1 = vanilla frequency; 20 = run once, then skip the next 19 dispatches.
    public Dictionary<string, int> Intervals { get; set; } = new(StringComparer.Ordinal);

    public static Settings CreateDefault() => new()
    {
        Enabled = true,
        DefaultInterval = 1,
        ReloadSeconds = 2,
        WriteDiscoveredTypes = true,
        BenchmarkSeconds = 0,
        Intervals = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ConstructionSite"] = 1,
            ["ResourceCountingService"] = 1,
            ["BehaviorManager"] = 1,
            ["NavMeshObserver"] = 1
        }
    };

    public void Normalize()
    {
        DefaultInterval = Math.Clamp(DefaultInterval, 1, 1000);
        ReloadSeconds = Math.Clamp(ReloadSeconds, 1, 60);
        // Legacy field kept only for backwards-compatible config parsing.
        // Benchmark execution is session-only.
        BenchmarkSeconds = 0;
        Intervals ??= new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var key in Intervals.Keys.ToArray())
        {
            Intervals[key] = Math.Clamp(Intervals[key], 1, 1000);
        }
    }
}
