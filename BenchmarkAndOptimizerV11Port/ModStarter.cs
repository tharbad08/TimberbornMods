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
        BfrLocalizedChangeOptimizerPatcher.Patch(FreezeHarmony);
        ExtendedBuilderReachNavOptimizer.Patch(FreezeHarmony);
        NavigationSynchronizerDetailProfiler.Patch(FreezeHarmony);
        PreviewNavMeshBatcher.Patch(FreezeHarmony);
        FaunaSpawnBudgetPatcher.Patch(FreezeHarmony);
        FaunaRecipeLookupCachePatcher.Patch(FreezeHarmony);
        KeystoneFaunaTemplatePrewarmer.Patch(FreezeHarmony);
        FaunaInstantiationDetailProfiler.Patch(FreezeHarmony);
        TerrainRecoveryTickLimiter.Patch(FreezeHarmony);
        SoilContaminationResetOptimizer.Patch(FreezeHarmony);
        SoilContaminationDeepProfiler.Patch(FreezeHarmony);
        SoilMoistureProfiler.Patch(FreezeHarmony);
        SoilParallelTaskProfiler.Patch(FreezeHarmony);
        InputAndFaunaDetailProfiler.Patch(FreezeHarmony);
        InputProcessorDetailProfiler.Patch(FreezeHarmony);
        EbbAndFlowDetailProfiler.Patch(FreezeHarmony);
        HotInputDetailProfiler.Patch(FreezeHarmony);
        PreviewServiceMemberDetailProfiler.Patch(FreezeHarmony);
        BlockPlacementDetailProfiler.Patch(FreezeHarmony);
        LevelVisibilityHandlerProfiler.Patch(FreezeHarmony);
        SuperCursorRefreshSmoother.Patch(FreezeHarmony);

        _patched = true;
        FreezeDetector.Initialize();
        PlayerLoopPhaseProfiler.Install();
        Runtime.Log(
            "performance build: v1.1.58 validation/fix pass enabled; v1.1.57 behavior retained; " +
            $"allocation metric source={AllocationCounter.Mode}; fallback mode is reported as heap growth, not exact allocation; " +
            "Keystone Cow/Bull/Deer prewarm moved to end-of-game initialization before primary UI; " +
            "soil worker observations are aggregated; long external/load gaps are excluded from freeze reports");
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
        public long ThreadAllocatedBefore;
        public ManagedHeapSampler.Sample HeapSample;
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
            AllocationTracker.Record("Update", __state.TypeName,
                Math.Max(0, AllocationCounter.Read() - __state.ThreadAllocatedBefore));
            FreezeDetector.RecordUpdateSingleton(
                __state.TypeName,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started);
            ManagedHeapSampler.EndSingletonSample(
                "Update", __state.TypeName, __state.HeapSample);
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
            AllocationTracker.Record("LateUpdate", __state.TypeName,
                Math.Max(0, AllocationCounter.Read() - __state.ThreadAllocatedBefore));
            FreezeDetector.RecordLateUpdateSingleton(
                __state.TypeName,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started);
            ManagedHeapSampler.EndSingletonSample(
                "LateUpdate", __state.TypeName, __state.HeapSample);
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
        __state.ThreadAllocatedBefore = AllocationCounter.Read();
        __state.HeapSample = ManagedHeapSampler.BeginSingletonSample();
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
        __state.ThreadAllocatedBefore = AllocationCounter.Read();
        __state.HeapSample = ManagedHeapSampler.BeginSingletonSample();
        __state.TypeName = runtimeType.FullName ?? runtimeType.Name;
    }

    private static Exception? TickSingletonFinalizer(
        Exception? __exception,
        TickSingletonSample __state)
    {
        if (__state.Active && __state.Started != 0 && __state.TypeName is not null)
        {
            AllocationTracker.Record("Tick", __state.TypeName,
                Math.Max(0, AllocationCounter.Read() - __state.ThreadAllocatedBefore));
            FreezeDetector.RecordTickSingleton(
                __state.TypeName,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started);
            ManagedHeapSampler.EndSingletonSample(
                "Tick", __state.TypeName, __state.HeapSample);
        }

        return __exception;
    }

    private static void UpdatePhasePrefix(out long __state)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        FreezeDetector.FrameBoundary(now, "UpdateSingletons");
        AllocationTracker.FrameBoundary();
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
    private const double DeepSampleArmMs = 40.0;
    private const int MaxComponentTypes = 20;
    private const double DeepSampleCooldownSeconds = 2.0;
    private static readonly HashSet<MethodBase> PatchedCallers = new();
    private static readonly Dictionary<string, long> SlowEntityTicks = new(StringComparer.Ordinal);
    private static readonly ConditionalWeakTable<object, DeepSampleMarker> DeepSampleTargets = new();
    private static long _nextDeepSampleArmTicks;
    private static long _frameTicks;
    private static int _frameCalls;

    private sealed class DeepSampleMarker
    {
    }

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

            try
            {
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(EntityTickDispatcherProfiler),
                            nameof(ArmedComponentSamplePrefix)))
                    {
                        priority = Priority.First
                    });

                Runtime.Log(
                    "slow-entity component sampler installed persistently on " +
                    "TickableEntity.TickTickableComponents");
            }
            catch (Exception ex)
            {
                Runtime.Log(
                    $"warning: slow-entity component sampler patch failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");
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

    private static bool ArmedComponentSamplePrefix(object __instance)
    {
        if (Runtime.IsBenchmarking)
        {
            return true;
        }

        // Returning false means we already invoked the entity's normal component
        // wrappers once with per-component timing. Otherwise let Timberborn run
        // TickTickableComponents normally.
        return !TryProfileArmedEntityComponents(__instance);
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

            if (elapsedMs >= DeepSampleArmMs)
            {
                ArmDeepComponentSample(__instance);
            }
        }

        return __exception;
    }

    private static void ArmDeepComponentSample(object entity)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now < _nextDeepSampleArmTicks)
        {
            return;
        }

        DeepSampleTargets.Remove(entity);
        DeepSampleTargets.Add(entity, new DeepSampleMarker());
        _nextDeepSampleArmTicks =
            now + (long)(DeepSampleCooldownSeconds * System.Diagnostics.Stopwatch.Frequency);
    }

    public static bool TryProfileArmedEntityComponents(object entity)
    {
        if (Runtime.IsBenchmarking || !DeepSampleTargets.Remove(entity))
        {
            return false;
        }

        return Runtime.TryProfileTickableComponents(entity);
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
            MemberInfo? componentsMember = AccessTools.Field(instance.GetType(), "_tickableComponents");
            componentsMember ??= AccessTools.Property(instance.GetType(), "_tickableComponents");

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
        MemberInfo? member = AccessTools.Field(type, "_tickableComponent");
        member ??= AccessTools.Property(type, "_tickableComponent");
        member ??= AccessTools.Field(type, "TickableComponent");
        member ??= AccessTools.Property(type, "TickableComponent");

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

internal static class BfrLocalizedChangeOptimizerPatcher
{
    private const int LocalBucketSize = 8;
    private const int MinBoundingAreaToSplit = 144;
    private static MethodInfo? _localizedMethod;
    private static MemberInfo? _xMember;
    private static MemberInfo? _yMember;
    private static Type? _coordinateType;
    private static Type? _typedListType;
    private static bool _patched;

    [ThreadStatic]
    private static bool _bypass;

    public static void Patch(Harmony harmony)
    {
        if (_patched)
        {
            return;
        }

        var type = AccessTools.TypeByName("Calloatti.BeaversForReal.BFRManager");
        _localizedMethod = type is null
            ? null
            : type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(method =>
                    method.Name == "ProcessLocalizedChange" &&
                    method.GetParameters().Length == 1);

        if (_localizedMethod is null)
        {
            Runtime.Log("BFR localized-change optimizer not installed: BFRManager.ProcessLocalizedChange not found");
            return;
        }

        var enumerableType = _localizedMethod.GetParameters()[0].ParameterType;
        _coordinateType = enumerableType.IsGenericType
            ? enumerableType.GetGenericArguments().FirstOrDefault()
            : null;

        if (_coordinateType is null)
        {
            Runtime.Log("warning: BFR localized-change optimizer unavailable: coordinate type not resolved");
            return;
        }

        _xMember = (MemberInfo?)AccessTools.Field(_coordinateType, "x")
            ?? AccessTools.Property(_coordinateType, "x");
        _yMember = (MemberInfo?)AccessTools.Field(_coordinateType, "y")
            ?? AccessTools.Property(_coordinateType, "y");
        _typedListType = typeof(List<>).MakeGenericType(_coordinateType);

        if (_xMember is null || _yMember is null)
        {
            Runtime.Log("warning: BFR localized-change optimizer unavailable: Vector3Int x/y not resolved");
            return;
        }

        harmony.Patch(
            _localizedMethod,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(BfrLocalizedChangeOptimizerPatcher), nameof(Prefix)))
            {
                priority = Priority.First
            },
            finalizer: new HarmonyMethod(
                AccessTools.Method(typeof(BfrLocalizedChangeOptimizerPatcher), nameof(Finalizer)))
            {
                priority = Priority.Last
            });

        _patched = true;
        Runtime.Log(
            $"BFR localized-change optimizer installed: split sparse navmesh changes into " +
            $"{LocalBucketSize}x{LocalBucketSize} local buckets before BFR rescans");
    }

    private static bool Prefix(
        object __instance,
        object __0,
        out long __state)
    {
        __state = 0;
        if (_bypass || Runtime.IsBenchmarking)
        {
            return true;
        }

        __state = System.Diagnostics.Stopwatch.GetTimestamp();

        if (__0 is not IEnumerable enumerable)
        {
            return true;
        }

        var coordinates = new List<(object Value, int X, int Y)>();
        foreach (var value in enumerable)
        {
            if (value is null)
            {
                continue;
            }

            coordinates.Add((
                value,
                Convert.ToInt32(ReadMember(value, _xMember!)),
                Convert.ToInt32(ReadMember(value, _yMember!))));
        }

        if (coordinates.Count <= 1)
        {
            return true;
        }

        var minX = coordinates.Min(item => item.X);
        var maxX = coordinates.Max(item => item.X);
        var minY = coordinates.Min(item => item.Y);
        var maxY = coordinates.Max(item => item.Y);

        // BFR itself adds one cell of padding around the rectangle.
        var originalArea = (maxX - minX + 3L) * (maxY - minY + 3L);
        if (originalArea < MinBoundingAreaToSplit)
        {
            return true;
        }

        var groups = coordinates
            .GroupBy(item => (FloorDiv(item.X, LocalBucketSize), FloorDiv(item.Y, LocalBucketSize)))
            .ToArray();

        if (groups.Length <= 1)
        {
            return true;
        }

        long groupedArea = 0;
        foreach (var group in groups)
        {
            var gx0 = group.Min(item => item.X);
            var gx1 = group.Max(item => item.X);
            var gy0 = group.Min(item => item.Y);
            var gy1 = group.Max(item => item.Y);
            groupedArea += (gx1 - gx0 + 3L) * (gy1 - gy0 + 3L);
        }

        // Splitting nearby edits can increase work because BFR's padded rectangles
        // overlap. Only take over when the total rescanned area is materially lower.
        if (groupedArea * 4 >= originalArea * 3)
        {
            return true;
        }

        try
        {
            _bypass = true;
            foreach (var group in groups)
            {
                var typedList = (IList)Activator.CreateInstance(_typedListType!)!;
                foreach (var item in group)
                {
                    typedList.Add(item.Value);
                }

                _localizedMethod!.Invoke(__instance, new object?[] { typedList });
            }

            Runtime.Log(
                $"BFR sparse localized-change batching active: reduced a " +
                $"{originalArea}-cell padded bounding scan to {groupedArea} cells across " +
                $"{groups.Length} local group(s)");
            return false;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
        }
        finally
        {
            _bypass = false;
        }
    }

    private static Exception? Finalizer(Exception? __exception, long __state)
    {
        if (__state != 0)
        {
            FreezeDetector.RecordBfrDetail(
                "ProcessLocalizedChange",
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }

        return __exception;
    }

    private static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? result - 1 : result;
    }

    private static object? ReadMember(object instance, MemberInfo member) =>
        member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => property.GetValue(instance),
            _ => null
        };
}

internal static class PreviewNavMeshBatcher
{
    private static Action<object>? _enqueueAdd;
    private static Action<object>? _enqueueRemove;
    private static bool _installed;

    public static void Patch(Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        var type = AccessTools.TypeByName("Timberborn.Navigation.NavMeshObject");
        var add = type is null ? null : AccessTools.Method(type, "AddToPreviewNavMesh", Type.EmptyTypes);
        var remove = type is null ? null : AccessTools.Method(type, "RemoveFromPreviewNavMesh", Type.EmptyTypes);
        var enqueueAdd = type is null ? null : AccessTools.Method(type, "EnqueueAddToPreviewNavMesh", Type.EmptyTypes);
        var enqueueRemove = type is null ? null : AccessTools.Method(type, "EnqueueRemoveFromPreviewNavMesh", Type.EmptyTypes);

        if (type is null || add is null || remove is null || enqueueAdd is null || enqueueRemove is null)
        {
            Runtime.Log(
                "warning: preview-navmesh batching unavailable: NavMeshObject immediate/enqueue methods not resolved");
            return;
        }

        _enqueueAdd = CompileInvoker(type, enqueueAdd);
        _enqueueRemove = CompileInvoker(type, enqueueRemove);

        harmony.Patch(
            add,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(PreviewNavMeshBatcher), nameof(AddPrefix)))
            {
                priority = Priority.First
            });
        harmony.Patch(
            remove,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(PreviewNavMeshBatcher), nameof(RemovePrefix)))
            {
                priority = Priority.First
            });

        _installed = true;
        Runtime.Log(
            "preview-navmesh batching installed: BlockObject preview navmesh add/remove now enqueue changes; " +
            "NavigationSynchronizer applies and notifies the combined preview update once instead of once per preview");
    }

    private static bool AddPrefix(object __instance)
    {
        _enqueueAdd!(__instance);
        return false;
    }

    private static bool RemovePrefix(object __instance)
    {
        _enqueueRemove!(__instance);
        return false;
    }

    private static Action<object> CompileInvoker(Type type, MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var call = Expression.Call(Expression.Convert(instance, type), method);
        return Expression.Lambda<Action<object>>(call, instance).Compile();
    }
}


internal static class NavigationSynchronizerDetailProfiler
{
    private static readonly HashSet<MethodBase> PatchedMethods = new();

    public static void Patch(Harmony harmony)
    {
        var type = AccessTools.TypeByName("Timberborn.Navigation.NavigationSynchronizer");
        if (type is null)
        {
            Runtime.Log("NavigationSynchronizer detail profiler unavailable: type not found");
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        var methods = type.GetMethods(flags)
            .Where(method =>
                !method.IsAbstract &&
                !method.ContainsGenericParameters &&
                method.ReturnType == typeof(void) &&
                (method.Name.StartsWith("Process", StringComparison.Ordinal) ||
                 method.Name.Contains("Change", StringComparison.Ordinal) ||
                 method.Name.Contains("Synchron", StringComparison.Ordinal)))
            .ToArray();

        foreach (var method in methods)
        {
            try
            {
                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(NavigationSynchronizerDetailProfiler), nameof(Prefix)))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(NavigationSynchronizerDetailProfiler), nameof(Finalizer)))
                    {
                        priority = Priority.Last
                    });

                PatchedMethods.Add(method);
            }
            catch (Exception ex)
            {
                Runtime.Log(
                    $"warning: NavigationSynchronizer detail profiler could not patch " +
                    $"{method.Name}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var registryMethodCount = 0;
        foreach (var registryTypeName in new[]
        {
            "Timberborn.Navigation.NavMeshListenerSingletonRegistry",
            "Timberborn.Navigation.NavMeshListenerEntityRegistry"
        })
        {
            var registryType = AccessTools.TypeByName(registryTypeName);
            if (registryType is null)
            {
                Runtime.Log(
                    $"warning: navigation listener registry profiler unavailable: " +
                    $"{registryTypeName} not found");
                continue;
            }

            var registryMethods = registryType
                .GetMethods(flags)
                .Where(method =>
                    !method.IsAbstract &&
                    !method.ContainsGenericParameters &&
                    method.ReturnType == typeof(void) &&
                    method.Name.StartsWith("NotifyAll", StringComparison.Ordinal))
                .ToArray();

            foreach (var method in registryMethods)
            {
                try
                {
                    harmony.Patch(
                        method,
                        prefix: new HarmonyMethod(
                            AccessTools.Method(
                                typeof(NavigationSynchronizerDetailProfiler),
                                nameof(Prefix)))
                        {
                            priority = Priority.First
                        },
                        finalizer: new HarmonyMethod(
                            AccessTools.Method(
                                typeof(NavigationSynchronizerDetailProfiler),
                                nameof(Finalizer)))
                        {
                            priority = Priority.Last
                        });

                    PatchedMethods.Add(method);
                    registryMethodCount++;
                }
                catch (Exception ex)
                {
                    Runtime.Log(
                        $"warning: navigation listener registry profiler could not patch " +
                        $"{registryType.Name}.{method.Name}: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        Runtime.Log(
            $"NavigationSynchronizer detail profiler installed: " +
            $"{PatchedMethods.Count - registryMethodCount} synchronizer method(s), " +
            $"{registryMethodCount} listener-registry method(s)");
    }

    private static void Prefix(out long __state)
    {
        __state = Runtime.IsBenchmarking
            ? 0
            : System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static Exception? Finalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        long __state)
    {
        if (__state != 0)
        {
            var declaringName = __originalMethod.DeclaringType?.Name ?? "Unknown";
            var key = declaringName.Contains("NavMeshListener", StringComparison.Ordinal)
                ? $"ListenerRegistry.{declaringName}.{__originalMethod.Name}"
                : __originalMethod.Name;

            FreezeDetector.RecordNavigationDetail(
                key,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }

        return __exception;
    }
}


internal static class KeystoneComponentAllocationProfiler
{
    private static readonly string[] TypeNames =
    {
        "Keystone.Mod.Flourish.KeystoneFlourish",
        "Keystone.Mod.Flourish.KeystoneRockTint",
        "Keystone.Mod.Growth.KeystoneGrowthBonus",
        "Keystone.Mod.Overgrowth.KeystoneOvergrowth",
    };

    private sealed class Aggregate
    {
        public long TotalTicks;
        public long MaxTicks;
        public long TotalAllocatedBytes;
        public long MaxAllocatedBytes;
        public int Calls;
        public int Gc0;
        public int Gc1;
        public int Gc2;
    }

    private struct Sample
    {
        public bool Active;
        public long Started;
        public long AllocatedBytes;
        public int Gc0;
        public int Gc1;
        public int Gc2;
    }

    private static readonly Dictionary<string, Aggregate> FrameStats =
        new(StringComparer.Ordinal);

    public static void Patch(Harmony harmony)
    {
        var installed = 0;

        foreach (var typeName in TypeNames)
        {
            var type = AccessTools.TypeByName(typeName);
            var tick = type is null ? null : AccessTools.Method(type, "Tick", Type.EmptyTypes);
            if (tick is null)
            {
                Runtime.Log(
                    $"warning: Keystone allocation profiler target not found: {typeName}.Tick");
                continue;
            }

            try
            {
                harmony.Patch(
                    tick,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(KeystoneComponentAllocationProfiler),
                            nameof(Prefix)))
                    {
                        priority = Priority.First
                    },
                    postfix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(KeystoneComponentAllocationProfiler),
                            nameof(Postfix)))
                    {
                        priority = Priority.Last
                    });

                installed++;
            }
            catch (Exception ex)
            {
                Runtime.Log(
                    $"warning: Keystone allocation profiler could not patch {typeName}.Tick: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }

        Runtime.Log(
            $"Keystone allocation/GC profiler installed: {installed}/{TypeNames.Length} component Tick method(s)");
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
        __state.AllocatedBytes = AllocationCounter.Read();
        __state.Gc0 = GC.CollectionCount(0);
        __state.Gc1 = GC.CollectionCount(1);
        __state.Gc2 = GC.CollectionCount(2);
    }

    private static void Postfix(
        MethodBase __originalMethod,
        bool __runOriginal,
        Sample __state)
    {
        if (!__state.Active || !__runOriginal || __state.Started == 0)
        {
            return;
        }

        var elapsed =
            System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started;
        var allocated =
            AllocationCounter.Read() - __state.AllocatedBytes;
        if (allocated < 0)
        {
            allocated = 0;
        }

        var typeName =
            __originalMethod.DeclaringType?.FullName ??
            __originalMethod.DeclaringType?.Name ??
            "UnknownKeystoneComponent";

        if (!FrameStats.TryGetValue(typeName, out var aggregate))
        {
            aggregate = new Aggregate();
            FrameStats[typeName] = aggregate;
        }

        aggregate.TotalTicks += Math.Max(0, elapsed);
        aggregate.MaxTicks = Math.Max(aggregate.MaxTicks, elapsed);
        aggregate.TotalAllocatedBytes += allocated;
        aggregate.MaxAllocatedBytes = Math.Max(aggregate.MaxAllocatedBytes, allocated);
        aggregate.Calls++;
        aggregate.Gc0 += GC.CollectionCount(0) - __state.Gc0;
        aggregate.Gc1 += GC.CollectionCount(1) - __state.Gc1;
        aggregate.Gc2 += GC.CollectionCount(2) - __state.Gc2;
    }

    public static void FlushFrame()
    {
        foreach (var pair in FrameStats)
        {
            var value = pair.Value;
            FreezeDetector.RecordKeystoneProfile(
                pair.Key,
                value.TotalTicks,
                value.MaxTicks,
                value.TotalAllocatedBytes,
                value.MaxAllocatedBytes,
                value.Calls,
                value.Gc0,
                value.Gc1,
                value.Gc2);
        }

        FrameStats.Clear();
    }
}


internal static class ExtendedBuilderReachNavOptimizer
{
    private const string EbrTypeName =
        "ExtendedBuilderReach.Components.ExtendedDemolishableAccessible";
    private const string RegistryTypeName =
        "Timberborn.Navigation.NavMeshListenerEntityRegistry";
    private const int BucketSize = 8;

    private static Type? _ebrType;
    private static FieldInfo? _registryListenersField;
    private static FieldInfo? _listenerBoundsField;
    private static FieldInfo? _boundsMinX;
    private static FieldInfo? _boundsMinY;
    private static FieldInfo? _boundsMaxX;
    private static FieldInfo? _boundsMaxY;
    private static PropertyInfo? _terrainCoordinatesProperty;
    private static PropertyInfo? _updatedRoadsProperty;
    private static MemberInfo? _coordX;
    private static MemberInfo? _coordY;
    private static Func<object, object, bool>? _intersects;
    private static Action<object, object>? _notify;

    private static readonly List<object> EbrListeners = new();
    private static readonly Dictionary<long, List<object>> SpatialBuckets = new();
    private static readonly ConditionalWeakTable<object, BucketMembership> Memberships = new();
    private static readonly ConditionalWeakTable<object, MigrationMarker> MigratedRegistries = new();
    private static readonly HashSet<object> CandidateSet = new(ReferenceComparer.Instance);
    private static bool _installed;

    private sealed class MigrationMarker
    {
    }

    private sealed class BucketMembership
    {
        public List<long> Keys { get; } = new();
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    public static void Patch(Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        var registryType = AccessTools.TypeByName(RegistryTypeName);
        _ebrType = AccessTools.TypeByName(EbrTypeName);
        if (registryType is null || _ebrType is null)
        {
            Runtime.Log(
                $"EBR nav optimizer not installed: registry={registryType is not null}, " +
                $"ExtendedDemolishableAccessible={_ebrType is not null}");
            return;
        }

        var register = AccessTools.Method(registryType, "RegisterNavMeshListener");
        var unregister = AccessTools.Method(registryType, "UnregisterNavMeshListener");
        var notifyAll = AccessTools.Method(registryType, "NotifyAll");
        _registryListenersField = AccessTools.Field(registryType, "_navMeshListeners");

        if (register is null ||
            unregister is null ||
            notifyAll is null ||
            _registryListenersField is null)
        {
            Runtime.Log(
                "warning: EBR nav optimizer unavailable: required registry members not found");
            return;
        }

        var updateType = notifyAll.GetParameters().FirstOrDefault()?.ParameterType;
        _listenerBoundsField = AccessTools.Field(_ebrType, "bounds");
        var updateBounds = updateType is null ? null : AccessTools.Property(updateType, "Bounds");
        _terrainCoordinatesProperty =
            updateType is null ? null : AccessTools.Property(updateType, "TerrainCoordinates");
        _updatedRoadsProperty =
            updateType is null ? null : AccessTools.Property(updateType, "UpdatedRoads");
        var notifyMethod = updateType is null
            ? null
            : AccessTools.Method(_ebrType, "OnNavMeshUpdated", new[] { updateType });

        if (updateType is null ||
            _listenerBoundsField is null ||
            updateBounds?.GetGetMethod(true) is null ||
            notifyMethod is null)
        {
            Runtime.Log(
                "warning: EBR nav optimizer unavailable: EBR/update bounds members not resolved");
            return;
        }

        var boundsType = _listenerBoundsField.FieldType;
        var minX = AccessTools.Field(boundsType, "_minX");
        var minY = AccessTools.Field(boundsType, "_minY");
        var minZ = AccessTools.Field(boundsType, "_minZ");
        var maxX = AccessTools.Field(boundsType, "_maxX");
        var maxY = AccessTools.Field(boundsType, "_maxY");
        var maxZ = AccessTools.Field(boundsType, "_maxZ");

        if (minX is null || minY is null || minZ is null ||
            maxX is null || maxY is null || maxZ is null)
        {
            Runtime.Log(
                "warning: EBR nav optimizer unavailable: BoundingBox fields not resolved");
            return;
        }

        _boundsMinX = minX;
        _boundsMinY = minY;
        _boundsMaxX = maxX;
        _boundsMaxY = maxY;

        var terrainCoordinatesType =
            _terrainCoordinatesProperty?.PropertyType.IsGenericType == true
                ? _terrainCoordinatesProperty.PropertyType.GetGenericArguments().FirstOrDefault()
                : null;
        if (terrainCoordinatesType is not null)
        {
            _coordX = (MemberInfo?)AccessTools.Field(terrainCoordinatesType, "x")
                ?? AccessTools.Property(terrainCoordinatesType, "x");
            _coordY = (MemberInfo?)AccessTools.Field(terrainCoordinatesType, "y")
                ?? AccessTools.Property(terrainCoordinatesType, "y");
        }

        try
        {
            _intersects = CreateIntersectsDelegate(
                _ebrType,
                updateType,
                _listenerBoundsField,
                updateBounds.GetGetMethod(true)!,
                boundsType,
                minX,
                minY,
                minZ,
                maxX,
                maxY,
                maxZ);
            _notify = CreateNotifyDelegate(_ebrType, updateType, notifyMethod);

            harmony.Patch(
                register,
                prefix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(ExtendedBuilderReachNavOptimizer),
                        nameof(RegisterPrefix)))
                {
                    priority = Priority.First
                });
            harmony.Patch(
                unregister,
                prefix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(ExtendedBuilderReachNavOptimizer),
                        nameof(UnregisterPrefix)))
                {
                    priority = Priority.First
                });
            harmony.Patch(
                notifyAll,
                prefix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(ExtendedBuilderReachNavOptimizer),
                        nameof(NotifyPrefix)))
                {
                    priority = Priority.First
                },
                postfix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(ExtendedBuilderReachNavOptimizer),
                        nameof(NotifyPostfix)))
                {
                    priority = Priority.Last
                });

            _installed = true;
            Runtime.Log(
                $"Extended Builder Reach nav optimizer installed: {BucketSize}x{BucketSize} " +
                "spatial buckets + exact terrain-change candidate selection; road updates " +
                "retain conservative full-list fallback");
        }
        catch (Exception ex)
        {
            _intersects = null;
            _notify = null;
            Runtime.Log(
                $"warning: EBR nav optimizer installation failed; vanilla registry retained: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool RegisterPrefix(object __0)
    {
        if (_installed && IsEbr(__0))
        {
            Add(__0);
            return false;
        }

        return true;
    }

    private static bool UnregisterPrefix(object __0)
    {
        if (_installed && IsEbr(__0))
        {
            Remove(__0);
            return false;
        }

        return true;
    }

    private static void NotifyPrefix(object __instance)
    {
        if (!_installed ||
            _registryListenersField is null ||
            MigratedRegistries.TryGetValue(__instance, out _))
        {
            return;
        }

        if (_registryListenersField.GetValue(__instance) is IList listeners)
        {
            var migrated = 0;
            for (var i = listeners.Count - 1; i >= 0; i--)
            {
                var listener = listeners[i];
                if (listener is not null && IsEbr(listener))
                {
                    Add(listener);
                    listeners.RemoveAt(i);
                    migrated++;
                }
            }

            if (migrated > 0)
            {
                Runtime.Log(
                    $"EBR nav optimizer migrated {migrated} pre-registered " +
                    "ExtendedDemolishableAccessible listener(s) into spatial buckets");
            }
        }

        MigratedRegistries.Add(__instance, new MigrationMarker());
    }

    private static void NotifyPostfix(object __0)
    {
        if (!_installed || _intersects is null || _notify is null)
        {
            return;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var total = EbrListeners.Count;
        var notified = 0;
        var scanned = 0;
        var usedSpatial = false;

        CandidateSet.Clear();

        var roadsUpdated = false;
        if (_updatedRoadsProperty is not null)
        {
            try
            {
                roadsUpdated = Convert.ToBoolean(_updatedRoadsProperty.GetValue(__0));
            }
            catch
            {
                roadsUpdated = true;
            }
        }

        if (!roadsUpdated &&
            _terrainCoordinatesProperty is not null &&
            _coordX is not null &&
            _coordY is not null &&
            _terrainCoordinatesProperty.GetValue(__0) is IEnumerable coordinates)
        {
            var coordinateCount = 0;
            foreach (var coordinate in coordinates)
            {
                if (coordinate is null)
                {
                    continue;
                }

                coordinateCount++;
                var x = Convert.ToInt32(ReadMember(coordinate, _coordX));
                var y = Convert.ToInt32(ReadMember(coordinate, _coordY));
                var key = BucketKey(FloorDiv(x, BucketSize), FloorDiv(y, BucketSize));
                if (!SpatialBuckets.TryGetValue(key, out var bucket))
                {
                    continue;
                }

                foreach (var listener in bucket)
                {
                    CandidateSet.Add(listener);
                }
            }

            usedSpatial = coordinateCount > 0;
        }

        if (usedSpatial)
        {
            scanned = CandidateSet.Count;
            foreach (var listener in CandidateSet)
            {
                if (_intersects(listener, __0))
                {
                    _notify(listener, __0);
                    notified++;
                }
            }
        }
        else
        {
            scanned = total;
            for (var i = 0; i < EbrListeners.Count; i++)
            {
                var listener = EbrListeners[i];
                if (_intersects(listener, __0))
                {
                    _notify(listener, __0);
                    notified++;
                }
            }
        }

        FreezeDetector.RecordNavigationDetail(
            usedSpatial
                ? $"EBR.SpatialDispatch[{total}~{scanned}->{notified}]"
                : $"EBR.FullDispatch[{total}->{notified}]",
            System.Diagnostics.Stopwatch.GetTimestamp() - started);
    }

    private static bool IsEbr(object listener) =>
        _ebrType is not null && listener.GetType() == _ebrType;

    private static void Add(object listener)
    {
        for (var i = 0; i < EbrListeners.Count; i++)
        {
            if (ReferenceEquals(EbrListeners[i], listener))
            {
                return;
            }
        }

        EbrListeners.Add(listener);
        AddToSpatialBuckets(listener);
    }

    private static void Remove(object listener)
    {
        for (var i = EbrListeners.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(EbrListeners[i], listener))
            {
                EbrListeners.RemoveAt(i);
                break;
            }
        }

        if (Memberships.TryGetValue(listener, out var membership))
        {
            foreach (var key in membership.Keys)
            {
                if (!SpatialBuckets.TryGetValue(key, out var bucket))
                {
                    continue;
                }

                for (var i = bucket.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(bucket[i], listener))
                    {
                        bucket.RemoveAt(i);
                    }
                }

                if (bucket.Count == 0)
                {
                    SpatialBuckets.Remove(key);
                }
            }

            Memberships.Remove(listener);
        }
    }

    private static void AddToSpatialBuckets(object listener)
    {
        if (_listenerBoundsField is null ||
            _boundsMinX is null ||
            _boundsMinY is null ||
            _boundsMaxX is null ||
            _boundsMaxY is null)
        {
            return;
        }

        var bounds = _listenerBoundsField.GetValue(listener);
        if (bounds is null)
        {
            return;
        }

        var minX = Convert.ToInt32(_boundsMinX.GetValue(bounds));
        var minY = Convert.ToInt32(_boundsMinY.GetValue(bounds));
        var maxX = Convert.ToInt32(_boundsMaxX.GetValue(bounds));
        var maxY = Convert.ToInt32(_boundsMaxY.GetValue(bounds));

        var membership = new BucketMembership();
        for (var bucketY = FloorDiv(minY, BucketSize);
             bucketY <= FloorDiv(maxY, BucketSize);
             bucketY++)
        {
            for (var bucketX = FloorDiv(minX, BucketSize);
                 bucketX <= FloorDiv(maxX, BucketSize);
                 bucketX++)
            {
                var key = BucketKey(bucketX, bucketY);
                if (!SpatialBuckets.TryGetValue(key, out var bucket))
                {
                    bucket = new List<object>();
                    SpatialBuckets[key] = bucket;
                }

                bucket.Add(listener);
                membership.Keys.Add(key);
            }
        }

        Memberships.Add(listener, membership);
    }

    private static long BucketKey(int x, int y) =>
        ((long)x << 32) | (uint)y;

    private static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        var remainder = value % divisor;
        return remainder < 0 ? result - 1 : result;
    }

    private static object? ReadMember(object instance, MemberInfo member) =>
        member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => property.GetValue(instance),
            _ => null,
        };

    private static Func<object, object, bool> CreateIntersectsDelegate(
        Type listenerType,
        Type updateType,
        FieldInfo listenerBounds,
        MethodInfo getUpdateBounds,
        Type boundsType,
        FieldInfo minX,
        FieldInfo minY,
        FieldInfo minZ,
        FieldInfo maxX,
        FieldInfo maxY,
        FieldInfo maxZ)
    {
        var dynamicMethod = new System.Reflection.Emit.DynamicMethod(
            "BenchmarkOptimizer_EbrBoundsIntersectFast",
            typeof(bool),
            new[] { typeof(object), typeof(object) },
            typeof(ExtendedBuilderReachNavOptimizer).Module,
            true);

        var il = dynamicMethod.GetILGenerator();
        var left = il.DeclareLocal(boundsType);
        var right = il.DeclareLocal(boundsType);
        var update = updateType.IsValueType ? il.DeclareLocal(updateType) : null;
        var noIntersection = il.DefineLabel();

        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Castclass, listenerType);
        il.Emit(System.Reflection.Emit.OpCodes.Ldfld, listenerBounds);
        il.Emit(System.Reflection.Emit.OpCodes.Stloc, left);

        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        if (updateType.IsValueType)
        {
            il.Emit(System.Reflection.Emit.OpCodes.Unbox_Any, updateType);
            il.Emit(System.Reflection.Emit.OpCodes.Stloc, update!);
            il.Emit(System.Reflection.Emit.OpCodes.Ldloca, update!);
            il.Emit(System.Reflection.Emit.OpCodes.Call, getUpdateBounds);
        }
        else
        {
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, updateType);
            il.Emit(
                getUpdateBounds.IsVirtual
                    ? System.Reflection.Emit.OpCodes.Callvirt
                    : System.Reflection.Emit.OpCodes.Call,
                getUpdateBounds);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Stloc, right);

        EmitAxisIntersection(il, left, right, minX, maxX, noIntersection);
        EmitAxisIntersection(il, left, right, minY, maxY, noIntersection);
        EmitAxisIntersection(il, left, right, minZ, maxZ, noIntersection);

        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_1);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);

        il.MarkLabel(noIntersection);
        il.Emit(System.Reflection.Emit.OpCodes.Ldc_I4_0);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);

        return (Func<object, object, bool>)dynamicMethod.CreateDelegate(
            typeof(Func<object, object, bool>));
    }

    private static void EmitAxisIntersection(
        System.Reflection.Emit.ILGenerator il,
        System.Reflection.Emit.LocalBuilder left,
        System.Reflection.Emit.LocalBuilder right,
        FieldInfo min,
        FieldInfo max,
        System.Reflection.Emit.Label noIntersection)
    {
        il.Emit(System.Reflection.Emit.OpCodes.Ldloca, left);
        il.Emit(System.Reflection.Emit.OpCodes.Ldfld, min);
        il.Emit(System.Reflection.Emit.OpCodes.Ldloca, right);
        il.Emit(System.Reflection.Emit.OpCodes.Ldfld, max);
        il.Emit(System.Reflection.Emit.OpCodes.Bgt, noIntersection);

        il.Emit(System.Reflection.Emit.OpCodes.Ldloca, left);
        il.Emit(System.Reflection.Emit.OpCodes.Ldfld, max);
        il.Emit(System.Reflection.Emit.OpCodes.Ldloca, right);
        il.Emit(System.Reflection.Emit.OpCodes.Ldfld, min);
        il.Emit(System.Reflection.Emit.OpCodes.Blt, noIntersection);
    }

    private static Action<object, object> CreateNotifyDelegate(
        Type listenerType,
        Type updateType,
        MethodInfo notifyMethod)
    {
        var dynamicMethod = new System.Reflection.Emit.DynamicMethod(
            "BenchmarkOptimizer_EbrNotify",
            typeof(void),
            new[] { typeof(object), typeof(object) },
            typeof(ExtendedBuilderReachNavOptimizer).Module,
            true);

        var il = dynamicMethod.GetILGenerator();
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_0);
        il.Emit(System.Reflection.Emit.OpCodes.Castclass, listenerType);
        il.Emit(System.Reflection.Emit.OpCodes.Ldarg_1);
        if (updateType.IsValueType)
        {
            il.Emit(System.Reflection.Emit.OpCodes.Unbox_Any, updateType);
        }
        else
        {
            il.Emit(System.Reflection.Emit.OpCodes.Castclass, updateType);
        }
        il.Emit(System.Reflection.Emit.OpCodes.Callvirt, notifyMethod);
        il.Emit(System.Reflection.Emit.OpCodes.Ret);

        return (Action<object, object>)dynamicMethod.CreateDelegate(
            typeof(Action<object, object>));
    }
}



internal static class FaunaSpawnBudgetPatcher
{
    private const int MaxActualSpawnsPerUpdate = 1;

    [ThreadStatic]
    private static int _updateDepth;

    [ThreadStatic]
    private static int _successfulActualSpawns;

    public static void Patch(Harmony harmony)
    {
        var type = AccessTools.TypeByName("Keystone.Mod.Fauna.FaunaSpawnDrainer");
        if (type is null)
        {
            Runtime.Log("fauna spawn budget not installed: FaunaSpawnDrainer not found");
            return;
        }

        var update = AccessTools.Method(type, "UpdateSingleton", Type.EmptyTypes);
        var spawn = type
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                method.Name == "Spawn" &&
                method.ReturnType == typeof(bool));

        if (update is null || spawn is null)
        {
            Runtime.Log(
                "warning: fauna spawn budget unavailable: UpdateSingleton/Spawn not resolved");
            return;
        }

        harmony.Patch(
            update,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(FaunaSpawnBudgetPatcher), nameof(UpdatePrefix)))
            {
                priority = Priority.First
            },
            finalizer: new HarmonyMethod(
                AccessTools.Method(typeof(FaunaSpawnBudgetPatcher), nameof(UpdateFinalizer)))
            {
                priority = Priority.Last
            });

        harmony.Patch(
            spawn,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(FaunaSpawnBudgetPatcher), nameof(SpawnPrefix)))
            {
                priority = Priority.First
            },
            postfix: new HarmonyMethod(
                AccessTools.Method(typeof(FaunaSpawnBudgetPatcher), nameof(SpawnPostfix)))
            {
                priority = Priority.Last
            });

        Runtime.Log(
            $"Keystone fauna spawn budget installed: max {MaxActualSpawnsPerUpdate} " +
            "actual EntityService.Instantiate success per UpdateSingleton; additional " +
            "same-frame spawn requests are re-queued by reporting deferred success");
    }

    private static void UpdatePrefix()
    {
        if (_updateDepth++ == 0)
        {
            _successfulActualSpawns = 0;
        }
    }

    private static Exception? UpdateFinalizer(Exception? __exception)
    {
        if (_updateDepth > 0)
        {
            _updateDepth--;
        }

        if (_updateDepth == 0)
        {
            _successfulActualSpawns = 0;
        }

        return __exception;
    }

    private static bool SpawnPrefix(ref bool __result, out bool __state)
    {
        __state = false;

        if (_updateDepth <= 0 || Runtime.IsBenchmarking)
        {
            return true;
        }

        if (_successfulActualSpawns >= MaxActualSpawnsPerUpdate)
        {
            // VisitCluster interprets true as Spawned and re-enqueues the cluster.
            // This intentionally defers the actual instantiation to a later frame
            // without dropping the population deficit from the queue.
            __result = true;
            return false;
        }

        __state = true;
        return true;
    }

    private static void SpawnPostfix(bool __result, bool __state)
    {
        if (__state && __result && _updateDepth > 0)
        {
            _successfulActualSpawns++;
        }
    }
}

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

internal static class EbbAndFlowDetailProfiler
{
    [ThreadStatic] private static int _depth;
    private static readonly Dictionary<MethodBase,string> Labels = new();

    public static void Patch(Harmony harmony)
    {
        var type = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(SafeGetTypes)
            .FirstOrDefault(t => t.Name == "EbbAndFlowManager");
        var tick = type is null ? null : AccessTools.Method(type, "Tick", Type.EmptyTypes);
        if (type is null || tick is null)
        {
            Runtime.Log("EbbAndFlow detail profiler not installed: manager/Tick not found");
            return;
        }

        harmony.Patch(tick,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(EbbAndFlowDetailProfiler), nameof(TickPrefix))) { priority = Priority.First },
            finalizer: new HarmonyMethod(AccessTools.Method(typeof(EbbAndFlowDetailProfiler), nameof(TickFinalizer))) { priority = Priority.Last });

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var m in type.GetMethods(flags))
        {
            if (m == tick || m.IsAbstract || m.IsSpecialName || m.ContainsGenericParameters || m.GetParameters().Any(p => p.ParameterType.IsByRef)) continue;
            try
            {
                Labels[m]=m.Name;
                harmony.Patch(m,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(EbbAndFlowDetailProfiler), nameof(InnerPrefix))) { priority = Priority.First },
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(EbbAndFlowDetailProfiler), nameof(InnerFinalizer))) { priority = Priority.Last });
            }
            catch { Labels.Remove(m); }
        }
        Runtime.Log($"EbbAndFlow detail profiler installed: {Labels.Count} inner method(s)");
    }

    private static void TickPrefix() { if (!Runtime.IsBenchmarking) _depth++; }
    private static Exception? TickFinalizer(Exception? __exception)
    {
        if (!Runtime.IsBenchmarking && _depth > 0) _depth--;
        return __exception;
    }
    private static void InnerPrefix(MethodBase __originalMethod, out long __state)
    {
        __state = 0;
        if (!Runtime.IsBenchmarking && _depth > 0 && Labels.ContainsKey(__originalMethod))
            __state = System.Diagnostics.Stopwatch.GetTimestamp();
    }
    private static Exception? InnerFinalizer(Exception? __exception, MethodBase __originalMethod, long __state)
    {
        if (__state != 0 && Labels.TryGetValue(__originalMethod, out var label))
            FreezeDetector.RecordTargetDetail("Ebb."+label, System.Diagnostics.Stopwatch.GetTimestamp()-__state);
        return __exception;
    }
    private static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try { return a.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t=>t is not null).Cast<Type>(); }
        catch { return Array.Empty<Type>(); }
    }
}

internal static class TerrainRecoveryTickLimiter
{
    private const int BaseRecoveryBudget = 8;
    private const int ElevatedRecoveryBudget = 16;
    private const int EmergencyRecoveryBudget = 24;
    private const int PostRecoveryLowBudget = 16;
    private const int PostRecoveryMediumBudget = 32;
    private const int PostRecoveryHighBudget = 64;
    private const int PostRecoveryEmergencyBudget = 96;
    private const double RecoverySeconds = 2.0;
    private const double ContinuousRecoveryGraceSeconds = 4.0;

    private static long _recoveryUntil;
    private static long _continuousRecoveryStartedAt;
    private static int _deferredBuckets;
    private static int _peakDeferredBuckets;
    private static bool _installed;
    private static bool _loggedActivation;
    private static bool _loggedAntiStarvation;

    public static void Patch(Harmony harmony)
    {
        if (_installed) return;

        var type = AccessTools.TypeByName("Timberborn.TickSystem.TickableBucketService");
        var method = type is null ? null : AccessTools.Method(type, "TickBuckets", new[] { typeof(int) });
        if (method is null)
        {
            Runtime.Log("warning: terrain recovery tick limiter unavailable: TickableBucketService.TickBuckets(int) not found");
            return;
        }

        harmony.Patch(method,
            prefix: new HarmonyMethod(AccessTools.Method(typeof(TerrainRecoveryTickLimiter), nameof(Prefix)))
            { priority = Priority.First });

        _installed = true;
        Runtime.Log(
            "terrain recovery tick limiter installed: recovery budgets 8/16/24; " +
            "debt-pressure ramp uses 32/48/64/96 once deferred debt exceeds 4096; " +
            "post-recovery debt budgets 16/32/64/96; debt-only calls can repay backlog");
    }

    public static void NotifyTerrainEdit()
    {
        if (!_installed || Runtime.IsBenchmarking) return;
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_continuousRecoveryStartedAt == 0 || now >= _recoveryUntil)
        {
            _continuousRecoveryStartedAt = now;
            _loggedAntiStarvation = false;
        }

        _recoveryUntil = Math.Max(
            _recoveryUntil,
            now + (long)(RecoverySeconds * System.Diagnostics.Stopwatch.Frequency));
    }

    private static void Prefix(ref int numberOfBucketsToTick)
    {
        if (!_installed || Runtime.IsBenchmarking) return;
        if (numberOfBucketsToTick <= 0 && _deferredBuckets <= 0) return;

        var now = System.Diagnostics.Stopwatch.GetTimestamp();

        if (now < _recoveryUntil)
        {
            var total = Math.Max(0L, (long)numberOfBucketsToTick) + _deferredBuckets;
            var budget = total >= 8192 ? EmergencyRecoveryBudget
                : total >= 2048 ? ElevatedRecoveryBudget
                : BaseRecoveryBudget;

            if (total >= 4096)
            {
                var pressureBudget = total >= 32768 ? PostRecoveryEmergencyBudget
                    : total >= 16384 ? PostRecoveryHighBudget
                    : total >= 8192 ? 48
                    : PostRecoveryMediumBudget;
                budget = Math.Max(budget, pressureBudget);

                if (!_loggedAntiStarvation)
                {
                    _loggedAntiStarvation = true;
                    Runtime.Log(
                        $"terrain recovery debt-pressure ramp active: " +
                        $"debt={_deferredBuckets}, bounded budget={budget}");
                }
            }

            var run = (int)Math.Min(total, budget);
            var deferred = total - run;
            _deferredBuckets = deferred >= int.MaxValue ? int.MaxValue : (int)deferred;
            _peakDeferredBuckets = Math.Max(_peakDeferredBuckets, _deferredBuckets);
            numberOfBucketsToTick = run;

            if (_deferredBuckets > 0 && !_loggedActivation)
            {
                _loggedActivation = true;
                Runtime.Log(
                    $"terrain recovery limiter active: deferred {_deferredBuckets} tick bucket(s); bounded budget={budget}");
            }
            return;
        }

        _loggedActivation = false;
        _loggedAntiStarvation = false;
        _continuousRecoveryStartedAt = 0;
        if (_deferredBuckets <= 0) return;

        var totalBudget = _deferredBuckets >= 16384 ? PostRecoveryEmergencyBudget
            : _deferredBuckets >= 4096 ? PostRecoveryHighBudget
            : _deferredBuckets >= 1024 ? PostRecoveryMediumBudget
            : PostRecoveryLowBudget;

        var baseRequested = Math.Max(0, numberOfBucketsToTick);
        var drain = Math.Min(_deferredBuckets, Math.Max(0, totalBudget - baseRequested));
        if (drain > 0)
        {
            numberOfBucketsToTick = baseRequested + drain;
            _deferredBuckets -= drain;
        }

        if (_deferredBuckets == 0)
        {
            Runtime.Log($"terrain recovery tick debt fully drained; peak debt={_peakDeferredBuckets}");
            _peakDeferredBuckets = 0;
        }
    }
}



internal static class SoilParallelTaskProfiler
{
    private sealed class Aggregate
    {
        public long Calls;
        public long TotalTicks;
        public long MaxTicks;
        public long TotalMetricBytes;
        public long MaxMetricBytes;
    }

    private static readonly object AggregateGate = new();
    private static readonly Dictionary<string, Aggregate> Aggregates = new(StringComparer.Ordinal);
    private static long _completedCalls;

    private static readonly string[] TypeNames =
    {
        "Timberborn.SoilContaminationSystem.ContaminationDataPreparationTask",
        "Timberborn.SoilContaminationSystem.ContaminationCandidatesCountingTask",
        "Timberborn.SoilContaminationSystem.ContaminationsUpdateTask",
        "Timberborn.SoilMoistureSystem.MoistureDataPreparationTask",
        "Timberborn.SoilMoistureSystem.WateredNeighborsCountingTask",
        "Timberborn.SoilMoistureSystem.ClusterSaturationCalculationTask",
        "Timberborn.SoilMoistureSystem.WaterEvaporationCalculationTask",
        "Timberborn.SoilMoistureSystem.MoistureCalculationTask",
    };

    private struct Sample
    {
        public long Started;
        public long AllocationBefore;
    }

    public static void Patch(Harmony harmony)
    {
        var installed = 0;
        foreach (var typeName in TypeNames)
        {
            var type = AccessTools.TypeByName(typeName);
            var method = type is null ? null : AccessTools.Method(type, "Run");
            if (method is null)
            {
                continue;
            }

            try
            {
                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(SoilParallelTaskProfiler), nameof(Prefix)))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(SoilParallelTaskProfiler), nameof(Finalizer)))
                    {
                        priority = Priority.Last
                    });
                installed++;
            }
            catch
            {
            }
        }

        Runtime.Log(
            $"soil parallel-task profiler installed: {installed}/{TypeNames.Length} task Run method(s); " +
            $"metric source={AllocationCounter.Mode}; fallback mode is heap growth, not exact allocation");
    }

    private static void Prefix(out Sample __state)
    {
        __state = new Sample
        {
            Started = System.Diagnostics.Stopwatch.GetTimestamp(),
            AllocationBefore = AllocationCounter.Read(),
        };
    }

    private static Exception? Finalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        Sample __state)
    {
        if (__state.Started == 0)
        {
            return __exception;
        }

        var elapsedTicks =
            System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started;
        var elapsedMs =
            elapsedTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        var allocated = Math.Max(
            0,
            AllocationCounter.Read() - __state.AllocationBefore);

        var name = __originalMethod.DeclaringType?.Name ?? "Unknown";
        lock (AggregateGate)
        {
            if (!Aggregates.TryGetValue(name, out var aggregate))
            {
                aggregate = new Aggregate();
                Aggregates[name] = aggregate;
            }

            aggregate.Calls++;
            aggregate.TotalTicks += elapsedTicks;
            aggregate.MaxTicks = Math.Max(aggregate.MaxTicks, elapsedTicks);
            aggregate.TotalMetricBytes += allocated;
            aggregate.MaxMetricBytes = Math.Max(aggregate.MaxMetricBytes, allocated);
            _completedCalls++;

            if ((_completedCalls % 512) == 0)
            {
                var top = Aggregates
                    .OrderByDescending(pair => pair.Value.TotalMetricBytes)
                    .ThenByDescending(pair => pair.Value.TotalTicks)
                    .Take(8)
                    .Select(pair =>
                        $"{pair.Key}:calls={pair.Value.Calls}," +
                        $"total={pair.Value.TotalTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms," +
                        $"max={pair.Value.MaxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms," +
                        $"metricTotal={FormatBytes(pair.Value.TotalMetricBytes)}," +
                        $"metricMax={FormatBytes(pair.Value.MaxMetricBytes)}")
                    .ToArray();

                Runtime.Log(
                    $"soil worker aggregate ({AllocationCounter.Mode}): " +
                    $"{string.Join(", ", top)}");

                Aggregates.Clear();
            }
        }

        if (elapsedMs >= 20.0 || allocated >= 1024L * 1024L)
        {
            Runtime.Log(
                $"soil worker task: {name}.Run elapsed={elapsedMs:F1}ms, " +
                $"metricDelta={FormatBytes(allocated)} ({AllocationCounter.Mode})");
        }

        return __exception;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0:F1}MB";
        }
        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:F1}KB";
        }
        return $"{bytes}B";
    }
}

internal static class InputAndFaunaDetailProfiler
{
    private static readonly Dictionary<MethodBase, string> Labels = new();

    public static void Patch(Harmony harmony)
    {
        PatchMethod(
            harmony,
            AccessTools.TypeByName("Timberborn.InputSystem.InputService"),
            "UpdateSingleton",
            "Input.Total");
        PatchMethod(
            harmony,
            AccessTools.TypeByName("Timberborn.InputSystem.InputService"),
            "CallInputProcessors",
            "Input.Processors");
        var faunaType = AccessTools.TypeByName("Keystone.Mod.Fauna.FaunaSpawnDrainer");
        PatchMethod(harmony, faunaType, "UpdateSingleton", "Fauna.Total");
        PatchMethod(harmony, faunaType, "VisitCluster", "Fauna.VisitCluster");
        PatchMethod(harmony, faunaType, "Spawn", "Fauna.Spawn");

        Runtime.Log(
            $"Input/Fauna detail profiler installed: {Labels.Count} method(s)");
    }

    private static void PatchMethod(
        Harmony harmony,
        Type? type,
        string methodName,
        string label)
    {
        if (type is null)
        {
            Runtime.Log(
                $"warning: detail profiler target type missing for {label}");
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var method = type
            .GetMethods(flags)
            .FirstOrDefault(candidate => candidate.Name == methodName);

        if (method is null)
        {
            Runtime.Log(
                $"warning: detail profiler method missing: {type.FullName}.{methodName}");
            return;
        }

        try
        {
            Labels[method] = label;
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(InputAndFaunaDetailProfiler),
                        nameof(Prefix)))
                {
                    priority = Priority.First
                },
                finalizer: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(InputAndFaunaDetailProfiler),
                        nameof(Finalizer)))
                {
                    priority = Priority.Last
                });
        }
        catch (Exception ex)
        {
            Labels.Remove(method);
            Runtime.Log(
                $"warning: detail profiler could not patch {type.FullName}.{methodName}: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Prefix(out long __state)
    {
        __state = Runtime.IsBenchmarking
            ? 0
            : System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static Exception? Finalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        long __state)
    {
        if (__state == 0 || !Labels.TryGetValue(__originalMethod, out var label))
        {
            return __exception;
        }

        var elapsed =
            System.Diagnostics.Stopwatch.GetTimestamp() - __state;

        if (label.StartsWith("Input.", StringComparison.Ordinal))
        {
            FreezeDetector.RecordInputDetail(label, elapsed);
        }
        else
        {
            FreezeDetector.RecordFaunaDetail(label, elapsed);
        }

        return __exception;
    }
}

internal static class NavigationEntityListenerProfiler
{
    private const double SlowNotifyThresholdMs = 50.0;
    private const int ThresholdCheckInterval = 32;

    private sealed class ListenerStat
    {
        public long Ticks;
        public int Calls;
    }

    private static readonly Dictionary<string, ListenerStat> Stats =
        new(StringComparer.Ordinal);

    [ThreadStatic]
    private static int _depth;

    [ThreadStatic]
    private static long _callStarted;

    [ThreadStatic]
    private static int _listenerCounter;

    [ThreadStatic]
    private static bool _profileThisCall;

    private static bool _profileNextCall;
    private static bool _installed;

    public static void Patch(Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        var registryType =
            AccessTools.TypeByName("Timberborn.Navigation.NavMeshListenerEntityRegistry");
        var notify = registryType is null
            ? null
            : AccessTools.Method(registryType, "NotifyAll");

        if (notify is null)
        {
            Runtime.Log(
                "warning: per-listener navmesh profiler unavailable: " +
                "NavMeshListenerEntityRegistry.NotifyAll not found");
            return;
        }

        try
        {
            harmony.Patch(
                notify,
                prefix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(NavigationEntityListenerProfiler),
                        nameof(Prefix)))
                {
                    priority = Priority.First
                },
                finalizer: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(NavigationEntityListenerProfiler),
                        nameof(Finalizer)))
                {
                    priority = Priority.Last
                },
                transpiler: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(NavigationEntityListenerProfiler),
                        nameof(Transpiler))));

            _installed = true;
            Runtime.Log(
                "adaptive per-entity navmesh-listener profiler installed: " +
                "profiles the tail of a >50ms NotifyAll call and fully profiles the following call");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: per-listener navmesh profiler installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Prefix()
    {
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        if (_depth++ == 0)
        {
            _callStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _listenerCounter = 0;
            _profileThisCall = _profileNextCall;
            _profileNextCall = false;
            Stats.Clear();
        }
    }

    private static Exception? Finalizer(Exception? __exception)
    {
        if (Runtime.IsBenchmarking || _depth <= 0)
        {
            return __exception;
        }

        if (--_depth != 0)
        {
            return __exception;
        }

        var elapsed =
            System.Diagnostics.Stopwatch.GetTimestamp() - _callStarted;
        var elapsedMs =
            elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        if (elapsedMs >= SlowNotifyThresholdMs)
        {
            _profileNextCall = true;
        }

        if (_profileThisCall || Stats.Count > 0)
        {
            foreach (var pair in Stats.OrderByDescending(pair => pair.Value.Ticks).Take(12))
            {
                FreezeDetector.RecordNavigationDetail(
                    $"EntityListener.{ShortName(pair.Key)}[{pair.Value.Calls}]",
                    pair.Value.Ticks);
            }
        }

        _callStarted = 0;
        _listenerCounter = 0;
        _profileThisCall = false;
        Stats.Clear();
        return __exception;
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions)
    {
        foreach (var instruction in instructions)
        {
            if (instruction.operand is MethodInfo method &&
                method.DeclaringType?.FullName == "Timberborn.Navigation.INavMeshListener" &&
                method.Name == "OnNavMeshUpdated" &&
                method.GetParameters().Length == 1)
            {
                var replacement = AccessTools.Method(
                        typeof(NavigationEntityListenerProfiler),
                        nameof(NotifyListener))!
                    .MakeGenericMethod(
                        method.DeclaringType,
                        method.GetParameters()[0].ParameterType);

                instruction.opcode = System.Reflection.Emit.OpCodes.Call;
                instruction.operand = replacement;
            }

            yield return instruction;
        }
    }

    private static void NotifyListener<TListener, TUpdate>(
        TListener listener,
        TUpdate update)
    {
        if (_depth <= 0 || Runtime.IsBenchmarking)
        {
            ListenerInvoker<TListener, TUpdate>.Invoke(listener, update);
            return;
        }

        if (!_profileThisCall)
        {
            _listenerCounter++;
            if ((_listenerCounter % ThresholdCheckInterval) == 0)
            {
                var elapsed =
                    System.Diagnostics.Stopwatch.GetTimestamp() - _callStarted;
                var elapsedMs =
                    elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (elapsedMs >= SlowNotifyThresholdMs)
                {
                    _profileThisCall = true;
                    Stats.Clear();
                }
            }

            if (!_profileThisCall)
            {
                ListenerInvoker<TListener, TUpdate>.Invoke(listener, update);
                return;
            }
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            ListenerInvoker<TListener, TUpdate>.Invoke(listener, update);
        }
        finally
        {
            var elapsed =
                System.Diagnostics.Stopwatch.GetTimestamp() - started;
            var key = listener?.GetType().FullName ?? typeof(TListener).FullName ?? "UnknownListener";
            if (!Stats.TryGetValue(key, out var stat))
            {
                stat = new ListenerStat();
                Stats[key] = stat;
            }

            stat.Ticks += elapsed;
            stat.Calls++;
        }
    }

    private static class ListenerInvoker<TListener, TUpdate>
    {
        public static readonly Action<TListener, TUpdate> Invoke = Create();

        private static Action<TListener, TUpdate> Create()
        {
            var method = typeof(TListener)
                .GetMethods()
                .First(candidate =>
                    candidate.Name == "OnNavMeshUpdated" &&
                    candidate.GetParameters().Length == 1 &&
                    candidate.GetParameters()[0].ParameterType == typeof(TUpdate));

            return (Action<TListener, TUpdate>)Delegate.CreateDelegate(
                typeof(Action<TListener, TUpdate>),
                method);
        }
    }

    private static string ShortName(string value)
    {
        var index = value.LastIndexOf('.');
        return index >= 0 ? value[(index + 1)..] : value;
    }
}


internal static class PreviewServiceMemberDetailProfiler
{
    private static readonly Dictionary<MethodBase, string> Labels = new();

    public static void Patch(Harmony harmony)
    {
        var iface = AccessTools.TypeByName("Timberborn.BlockSystem.IPreviewServiceMember");
        if (iface is null)
        {
            Runtime.Log("warning: preview-service-member profiler unavailable: interface missing");
            return;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || !iface.IsAssignableFrom(type))
                {
                    continue;
                }

                foreach (var methodName in new[] { "AddToPreviewService", "RemoveFromPreviewService" })
                {
                    var method = AccessTools.Method(type, methodName, Type.EmptyTypes);
                    if (method is null || Labels.ContainsKey(method))
                    {
                        continue;
                    }

                    try
                    {
                        Labels[method] =
                            $"Block.PreviewMember.{ShortName(type)}.{(methodName.StartsWith("Add") ? "Add" : "Remove")}";
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(
                                AccessTools.Method(typeof(PreviewServiceMemberDetailProfiler), nameof(Prefix)))
                            { priority = Priority.First },
                            finalizer: new HarmonyMethod(
                                AccessTools.Method(typeof(PreviewServiceMemberDetailProfiler), nameof(Finalizer)))
                            { priority = Priority.Last });
                    }
                    catch
                    {
                        Labels.Remove(method);
                    }
                }
            }
        }

        Runtime.Log(
            $"preview-service-member profiler installed: {Labels.Count} add/remove method(s)");
    }

    private static void Prefix(MethodBase __originalMethod, out long __state)
    {
        __state = HotInputDetailProfiler.BlockScopeActive &&
                  Labels.ContainsKey(__originalMethod)
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
    }

    private static Exception? Finalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        long __state)
    {
        if (__state != 0 && Labels.TryGetValue(__originalMethod, out var label))
        {
            FreezeDetector.RecordInputDetail(
                label,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }

        return __exception;
    }

    private static string ShortName(Type type)
    {
        var name = type.FullName ?? type.Name;
        var index = name.LastIndexOf('.');
        return index >= 0 ? name[(index + 1)..] : name;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Cast<Type>();
        }
        catch { return Array.Empty<Type>(); }
    }
}

internal static class BlockPlacementDetailProfiler
{
    private static readonly Dictionary<MethodBase, string> Labels = new();

    public static void Patch(Harmony harmony)
    {
        PatchInterfaceImplementations(
            harmony,
            "Timberborn.BlockObjectTools.IBlockObjectPlacer",
            "Place",
            "Block.Placer");

        PatchNamed(harmony, "Timberborn.ConstructionSites.ConstructionFactory", "CreateAsUnfinished", "Block.Construction.CreateUnfinished");
        PatchNamed(harmony, "Timberborn.ConstructionSites.ConstructionFactory", "CreateAsFinished", "Block.Construction.CreateFinished");
        PatchNamed(harmony, "Timberborn.BlockSystem.BlockObjectFactory", "CreateUnfinished", "Block.Factory.CreateUnfinished");
        PatchNamed(harmony, "Timberborn.BlockSystem.BlockObjectFactory", "CreateFinished", "Block.Factory.CreateFinished");
        PatchNamed(harmony, "Timberborn.EntitySystem.EntityService", "Instantiate", "Block.EntityService.Instantiate");
        PatchNamed(harmony, "Timberborn.TemplateInstantiation.TemplateInstantiator", "Instantiate", "Block.TemplateInstantiator.Instantiate");
        PatchNamed(harmony, "Timberborn.TemplateInstantiation.TemplateInstantiator", "GetCachedTemplate", "Block.TemplateInstantiator.GetCachedTemplate");
        PatchNamed(harmony, "Timberborn.TemplateInstantiation.TemplateInstantiator", "GetInstanceComponents", "Block.TemplateInstantiator.GetInstanceComponents");
        PatchNamed(harmony, "Timberborn.PrefabOptimization.OptimizedPrefabInstantiator", "InstantiateInactive", "Block.OptimizedPrefabInstantiator.InstantiateInactive");
        PatchNamed(harmony, "Timberborn.PrefabOptimization.PrefabOptimizationChain", "Process", "Block.PrefabOptimizationChain.Process");
        PatchNamed(harmony, "Timberborn.PrefabOptimization.PrefabOptimizationChain", "ProcessPrefab", "Block.PrefabOptimizationChain.ProcessPrefab");
        PatchNamed(harmony, "Timberborn.BlueprintPrefabSystem.BlueprintPrefabConverter", "Convert", "Block.BlueprintPrefabConverter.Convert");
        PatchNamed(harmony, "Timberborn.BaseComponentSystem.BaseInstantiator", "InstantiateInactive", "Block.BaseInstantiator.InstantiateInactive");
        PatchNamed(harmony, "Timberborn.BaseComponentSystem.BaseInstantiator", "InstantiateComponents", "Block.BaseInstantiator.InstantiateComponents");
        PatchNamed(harmony, "Timberborn.BaseComponentSystem.BaseInstantiator", "InstantiateComponent", "Block.BaseInstantiator.InstantiateComponent");
        PatchNamed(harmony, "Timberborn.BaseComponentSystem.ComponentCache", "Initialize", "Block.ComponentCache.Initialize");
        PatchNamed(harmony, "Timberborn.BlockSystem.BlockObject", "Reposition", "Block.BlockObject.Reposition");
        PatchNamed(harmony, "Timberborn.ConstructionSites.ConstructionSite", "FinishNow", "Block.ConstructionSite.FinishNow");

        Runtime.Log(
            $"block-placement detail profiler installed: {Labels.Count} method(s)");
    }

    private static void PatchInterfaceImplementations(
        Harmony harmony,
        string interfaceName,
        string methodName,
        string prefix)
    {
        var iface = AccessTools.TypeByName(interfaceName);
        if (iface is null)
        {
            return;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || !iface.IsAssignableFrom(type))
                {
                    continue;
                }

                var method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(candidate => candidate.Name == methodName);
                if (method is not null)
                {
                    PatchMethod(harmony, method, $"{prefix}.{ShortName(type)}");
                }
            }
        }
    }

    private static void PatchNamed(
        Harmony harmony,
        string typeName,
        string methodName,
        string label)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            return;
        }

        foreach (var method in type.GetMethods(
                     BindingFlags.Instance | BindingFlags.Static |
                     BindingFlags.Public | BindingFlags.NonPublic |
                     BindingFlags.DeclaredOnly)
                 .Where(candidate => candidate.Name == methodName))
        {
            PatchMethod(harmony, method, label);
        }
    }

    private static void PatchMethod(Harmony harmony, MethodInfo method, string label)
    {
        if (method.IsAbstract || method.ContainsGenericParameters || Labels.ContainsKey(method))
        {
            return;
        }

        try
        {
            Labels[method] = label;
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(BlockPlacementDetailProfiler), nameof(Prefix)))
                { priority = Priority.First },
                finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(BlockPlacementDetailProfiler), nameof(Finalizer)))
                { priority = Priority.Last });
        }
        catch
        {
            Labels.Remove(method);
        }
    }

    private static void Prefix(MethodBase __originalMethod, out long __state)
    {
        __state = HotInputDetailProfiler.BlockScopeActive &&
                  Labels.ContainsKey(__originalMethod)
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
    }

    private static Exception? Finalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        long __state)
    {
        if (__state != 0 && Labels.TryGetValue(__originalMethod, out var label))
        {
            FreezeDetector.RecordInputDetail(
                label,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }

        return __exception;
    }

    private static string ShortName(Type type)
    {
        var name = type.FullName ?? type.Name;
        var index = name.LastIndexOf('.');
        return index >= 0 ? name[(index + 1)..] : name;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Cast<Type>();
        }
        catch { return Array.Empty<Type>(); }
    }
}


internal static class LevelVisibilityHandlerProfiler
{
    private static readonly Dictionary<MethodBase, string> Labels = new();

    public static void Patch(Harmony harmony)
    {
        var eventType = AccessTools.TypeByName(
            "Timberborn.LevelVisibilitySystem.MaxVisibleLevelChangedEvent");
        if (eventType is null)
        {
            Runtime.Log("warning: LevelVisibility handler profiler unavailable: event type missing");
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                foreach (var method in type.GetMethods(flags))
                {
                    if (method.IsAbstract ||
                        method.ContainsGenericParameters ||
                        method.Name != "OnMaxVisibleLevelChanged")
                    {
                        continue;
                    }

                    var parameters = method.GetParameters();
                    if (parameters.Length != 1 ||
                        !parameters[0].ParameterType.IsAssignableFrom(eventType) &&
                        !eventType.IsAssignableFrom(parameters[0].ParameterType))
                    {
                        continue;
                    }

                    try
                    {
                        Labels[method] =
                            $"Level.Handler.{type.FullName ?? type.Name}";
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(
                                AccessTools.Method(typeof(LevelVisibilityHandlerProfiler), nameof(Prefix)))
                            {
                                priority = Priority.First
                            },
                            finalizer: new HarmonyMethod(
                                AccessTools.Method(typeof(LevelVisibilityHandlerProfiler), nameof(Finalizer)))
                            {
                                priority = Priority.Last
                            });
                    }
                    catch
                    {
                        Labels.Remove(method);
                    }
                }
            }
        }

        Runtime.Log(
            $"LevelVisibility event-handler profiler installed: {Labels.Count} handler(s)");
    }

    private static void Prefix(MethodBase __originalMethod, out long __state)
    {
        __state = HotInputDetailProfiler.LevelScopeActive &&
                  Labels.ContainsKey(__originalMethod)
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
    }

    private static Exception? Finalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        long __state)
    {
        if (__state != 0 && Labels.TryGetValue(__originalMethod, out var label))
        {
            FreezeDetector.RecordInputDetail(
                label,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
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


internal static class SuperCursorRefreshSmoother
{
    private const double NormalRefreshMs = 50.0;
    private const double ModerateRefreshMs = 100.0;
    private const double SlowRefreshMs = 250.0;

    private sealed class State
    {
        public long NextAllowed;
    }

    private static readonly ConditionalWeakTable<object, State> States = new();
    private static bool _installed;

    public static void Patch(Harmony harmony)
    {
        var type = AccessTools.TypeByName("SuperCursor.Services.SuperCursorTool");
        var method = type?
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                candidate.Name == "ProcessInfo" &&
                candidate.ReturnType == typeof(void) &&
                candidate.GetParameters().Length == 0);

        if (method is null)
        {
            Runtime.Log("SuperCursor refresh smoother not installed: ProcessInfo not found");
            return;
        }

        harmony.Patch(
            method,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(SuperCursorRefreshSmoother), nameof(Prefix)))
            {
                priority = Priority.First
            },
            finalizer: new HarmonyMethod(
                AccessTools.Method(typeof(SuperCursorRefreshSmoother), nameof(Finalizer)))
            {
                priority = Priority.Last
            });

        _installed = true;
        Runtime.Log(
            "SuperCursor refresh smoother installed: info-only refresh capped at 20Hz, " +
            "with 10Hz/4Hz adaptive backoff after expensive refreshes; input/tool actions unchanged");
    }

    private static bool Prefix(object __instance, out long __state)
    {
        __state = 0;
        if (!_installed || Runtime.IsBenchmarking || __instance is null)
        {
            return true;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = States.GetOrCreateValue(__instance);
        if (now < state.NextAllowed)
        {
            return false;
        }

        __state = now;
        return true;
    }

    private static Exception? Finalizer(
        Exception? __exception,
        object __instance,
        long __state)
    {
        if (__state == 0 || __instance is null)
        {
            return __exception;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = now - __state;
        var elapsedMs =
            elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        FreezeDetector.RecordInputDetail("SuperCursor.ProcessInfo", elapsed);

        var refreshMs = elapsedMs >= 100.0
            ? SlowRefreshMs
            : elapsedMs >= 30.0
                ? ModerateRefreshMs
                : NormalRefreshMs;

        States.GetOrCreateValue(__instance).NextAllowed =
            now + (long)(refreshMs / 1000.0 * System.Diagnostics.Stopwatch.Frequency);

        return __exception;
    }
}


internal static class InputProcessorDetailProfiler
{
    [ThreadStatic] private static int _dispatchDepth;
    private static readonly Dictionary<MethodBase, string> Labels = new();

    public static void Patch(Harmony harmony)
    {
        var inputService = AccessTools.TypeByName("Timberborn.InputSystem.InputService");
        var root = inputService is null
            ? null
            : AccessTools.DeclaredMethod(inputService, "CallInputProcessors", Type.EmptyTypes);

        if (root is not null)
        {
            harmony.Patch(
                root,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(InputProcessorDetailProfiler), nameof(RootPrefix)))
                { priority = Priority.First },
                finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(InputProcessorDetailProfiler), nameof(RootFinalizer)))
                { priority = Priority.Last });
        }

        PatchInterface(harmony, "Timberborn.InputSystem.IPriorityInputProcessor", "Priority");
        PatchInterface(harmony, "Timberborn.InputSystem.IInputProcessor", "Normal");

        Runtime.Log(
            $"input-processor detail profiler installed: {Labels.Count} effective ProcessInput method(s)");
    }

    private static void PatchInterface(Harmony harmony, string interfaceName, string kind)
    {
        var iface = AccessTools.TypeByName(interfaceName);
        if (iface is null)
        {
            return;
        }

        var ifaceMethod = iface.GetMethods()
            .FirstOrDefault(method =>
                method.Name == "ProcessInput" &&
                method.GetParameters().Length == 0);
        if (ifaceMethod is null)
        {
            return;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || !iface.IsAssignableFrom(type))
                {
                    continue;
                }

                MethodInfo? target = null;
                try
                {
                    var map = type.GetInterfaceMap(iface);
                    for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        if (map.InterfaceMethods[i].Name == ifaceMethod.Name &&
                            map.InterfaceMethods[i].GetParameters().Length == 0)
                        {
                            target = map.TargetMethods[i];
                            break;
                        }
                    }
                }
                catch
                {
                    // Some generated runtime types cannot expose an interface map.
                }

                target ??= AccessTools.Method(type, "ProcessInput", Type.EmptyTypes);
                if (target is null || target.IsAbstract || target.ContainsGenericParameters ||
                    Labels.ContainsKey(target))
                {
                    continue;
                }

                var owner = target.DeclaringType ?? type;
                Labels[target] = $"InputProcessor.{kind}.{ShortName(owner)}";
                try
                {
                    harmony.Patch(
                        target,
                        prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(InputProcessorDetailProfiler), nameof(Prefix)))
                        { priority = Priority.First },
                        finalizer: new HarmonyMethod(
                            AccessTools.Method(typeof(InputProcessorDetailProfiler), nameof(Finalizer)))
                        { priority = Priority.Last });
                }
                catch
                {
                    Labels.Remove(target);
                }
            }
        }
    }

    private static void RootPrefix()
    {
        if (!Runtime.IsBenchmarking)
        {
            _dispatchDepth++;
        }
    }

    private static Exception? RootFinalizer(Exception? __exception)
    {
        if (_dispatchDepth > 0)
        {
            _dispatchDepth--;
        }
        return __exception;
    }

    private static void Prefix(MethodBase __originalMethod, out long __state)
    {
        __state = _dispatchDepth > 0 &&
                  !Runtime.IsBenchmarking &&
                  Labels.ContainsKey(__originalMethod)
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
    }

    private static Exception? Finalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        long __state)
    {
        if (__state != 0 && Labels.TryGetValue(__originalMethod, out var label))
        {
            FreezeDetector.RecordInputDetail(
                label,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }
        return __exception;
    }

    private static string ShortName(Type type)
    {
        var name = type.FullName ?? type.Name;
        var index = name.LastIndexOf('.');
        return index >= 0 ? name[(index + 1)..] : name;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(type => type is not null).Cast<Type>();
        }
        catch { return Array.Empty<Type>(); }
    }
}

internal static class HotInputDetailProfiler
{
    [ThreadStatic] private static int _blockDepth;
    [ThreadStatic] private static int _levelDepth;

    private enum Scope
    {
        Always,
        Block,
        Level,
    }

    private sealed record DetailTarget(string Label, Scope Scope);
    private static readonly Dictionary<MethodBase, DetailTarget> Details = new();

    public static bool BlockScopeActive => _blockDepth > 0 && !Runtime.IsBenchmarking;
    public static bool LevelScopeActive => _levelDepth > 0 && !Runtime.IsBenchmarking;

    public static void Patch(Harmony harmony)
    {
        PatchRoot(
            harmony,
            AccessTools.TypeByName("Timberborn.BlockObjectTools.BlockObjectTool"),
            "ProcessInput",
            nameof(BlockRootPrefix),
            nameof(BlockRootFinalizer));

        PatchDetail(harmony, "Timberborn.BlockObjectTools.BlockObjectTool", "PreviewCallback", "Block.PreviewCallback", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.BlockObjectTool", "ActionCallback", "Block.ActionCallback", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.BlockObjectTool", "ShowPreviews", "Block.ShowPreviews", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.BlockObjectTool", "Place", "Block.Place", Scope.Block);
        PatchDetail(harmony, "Timberborn.AreaSelectionSystem.AreaPicker", "PickBlockObjectArea", "Block.AreaPicker", Scope.Block);
        PatchDetail(harmony, "Timberborn.AreaSelectionSystem.AreaPicker", "GetBlocks", "Block.GetBlocks", Scope.Block);
        PatchDetail(harmony, "Timberborn.AreaSelectionSystem.AreaPicker", "GetBlocksForLayout", "Block.GetBlocksForLayout", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.PreviewPlacer", "ShowPreviews", "Block.PreviewPlacer.ShowPreviews", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.PreviewPlacer", "PopulateBuildablePreviewsAndAddToBlockServices", "Block.PreviewPlacer.Populate", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.PreviewPlacer", "GetPositionedPreviews", "Block.PreviewPlacer.Position", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.PreviewPlacer", "ShowBuildablePreviews", "Block.PreviewPlacer.ShowBuildable", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.PreviewPlacer", "UpdateModels", "Block.PreviewPlacer.UpdateModels", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.PreviewPlacer", "RemovePreviewsFromServices", "Block.PreviewPlacer.RemoveServices", Scope.Block);
        PatchDetail(harmony, "Timberborn.BlockObjectTools.BlockObjectValidationService", "AreValid", "Block.Validation.AreValid", Scope.Block);

        PatchRoot(
            harmony,
            AccessTools.TypeByName("Timberborn.LevelVisibilitySystemUI.LevelVisibilitySelector"),
            "ProcessInput",
            nameof(LevelRootPrefix),
            nameof(LevelRootFinalizer));
        PatchDetail(harmony, "Timberborn.LevelVisibilitySystemUI.LevelVisibilitySelector", "ProcessMouseMovement", "Level.ProcessMouseMovement", Scope.Level);
        PatchDetail(harmony, "Timberborn.LevelVisibilitySystem.LevelVisibilityService", "SetMaxVisibleLevel", "Level.SetMaxVisibleLevel", Scope.Level);
        PatchDetail(harmony, "Timberborn.LevelVisibilitySystem.LevelVisibilityService", "InternalSetMaxVisibleLevel", "Level.InternalSetMaxVisibleLevel", Scope.Level);

        PatchDetail(harmony, "SuperCursor.Services.SuperCursorTool", "ProcessInfo", "SuperCursor.ProcessInfo.Detail", Scope.Always);
        PatchDetail(harmony, "SuperCursor.Services.SuperCursorTool", "ProcessObject", "SuperCursor.ProcessObject", Scope.Always);
        PatchDetail(harmony, "SuperCursor.Services.SuperCursorTool", "ProcessCoords", "SuperCursor.ProcessCoords", Scope.Always);
        PatchDetail(harmony, "SuperCursor.Services.SuperCursorTool", "MoveLabel", "SuperCursor.MoveLabel", Scope.Always);

        Runtime.Log(
            $"focused hot-input profiler installed: {Details.Count} inner method(s); " +
            "broad 109-method input profiler disabled");
    }

    private static void PatchRoot(
        Harmony harmony,
        Type? type,
        string methodName,
        string prefixName,
        string finalizerName)
    {
        var method = type?
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == 0);

        if (method is null)
        {
            Runtime.Log($"warning: focused input root missing: {type?.FullName ?? "<type>"}.{methodName}");
            return;
        }

        harmony.Patch(
            method,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(HotInputDetailProfiler), prefixName))
            {
                priority = Priority.First
            },
            finalizer: new HarmonyMethod(
                AccessTools.Method(typeof(HotInputDetailProfiler), finalizerName))
            {
                priority = Priority.Last
            });
    }

    private static void PatchDetail(
        Harmony harmony,
        string typeName,
        string methodName,
        string label,
        Scope scope)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(flags).Where(candidate => candidate.Name == methodName))
        {
            if (method.IsAbstract || method.ContainsGenericParameters || Details.ContainsKey(method))
            {
                continue;
            }

            try
            {
                Details[method] = new DetailTarget(label, scope);
                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(HotInputDetailProfiler), nameof(DetailPrefix)))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(HotInputDetailProfiler), nameof(DetailFinalizer)))
                    {
                        priority = Priority.Last
                    });
            }
            catch
            {
                Details.Remove(method);
            }
        }
    }

    private static void BlockRootPrefix(out long __state)
    {
        __state = 0;
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        _blockDepth++;
        __state = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static Exception? BlockRootFinalizer(Exception? __exception, long __state)
    {
        if (__state != 0)
        {
            FreezeDetector.RecordInputDetail(
                "Block.ProcessInput",
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }

        if (_blockDepth > 0)
        {
            _blockDepth--;
        }

        return __exception;
    }

    private static void LevelRootPrefix(out long __state)
    {
        __state = 0;
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        _levelDepth++;
        __state = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static Exception? LevelRootFinalizer(Exception? __exception, long __state)
    {
        if (__state != 0)
        {
            FreezeDetector.RecordInputDetail(
                "Level.ProcessInput",
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }

        if (_levelDepth > 0)
        {
            _levelDepth--;
        }

        return __exception;
    }

    private static void DetailPrefix(MethodBase __originalMethod, out long __state)
    {
        __state = 0;
        if (Runtime.IsBenchmarking ||
            !Details.TryGetValue(__originalMethod, out var target))
        {
            return;
        }

        if (target.Scope == Scope.Block && _blockDepth <= 0)
        {
            return;
        }

        if (target.Scope == Scope.Level && _levelDepth <= 0)
        {
            return;
        }

        __state = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    private static Exception? DetailFinalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        long __state)
    {
        if (__state != 0 &&
            Details.TryGetValue(__originalMethod, out var target))
        {
            FreezeDetector.RecordInputDetail(
                target.Label,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }

        return __exception;
    }
}


internal static class InputProcessorProfiler
{
    private static readonly HashSet<MethodBase> PatchedMethods = new();
    private static readonly Dictionary<string, long> FrameTicks = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static int _inputDepth;

    private struct Sample
    {
        public long Started;
        public string? TypeName;
    }

    public static void Patch(Harmony harmony)
    {
        var interfaces = new[]
        {
            AccessTools.TypeByName("Timberborn.InputSystem.IInputProcessor"),
            AccessTools.TypeByName("Timberborn.InputSystem.IPriorityInputProcessor"),
        }.Where(type => type is not null).Cast<Type>().ToArray();

        var inputService = AccessTools.TypeByName("Timberborn.InputSystem.InputService");
        var callProcessors = inputService is null
            ? null
            : AccessTools.Method(inputService, "CallInputProcessors");

        if (interfaces.Length == 0 || callProcessors is null)
        {
            Runtime.Log(
                "warning: per-input-processor profiler unavailable: interfaces or CallInputProcessors missing");
            return;
        }

        harmony.Patch(
            callProcessors,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(InputProcessorProfiler), nameof(CallPrefix)))
            {
                priority = Priority.First
            },
            finalizer: new HarmonyMethod(
                AccessTools.Method(typeof(InputProcessorProfiler), nameof(CallFinalizer)))
            {
                priority = Priority.Last
            });

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface)
                {
                    continue;
                }

                foreach (var iface in interfaces)
                {
                    if (!iface.IsAssignableFrom(type))
                    {
                        continue;
                    }

                    try
                    {
                        var map = type.GetInterfaceMap(iface);
                        for (var i = 0; i < map.InterfaceMethods.Length; i++)
                        {
                            if (map.InterfaceMethods[i].Name != "ProcessInput")
                            {
                                continue;
                            }

                            var target = map.TargetMethods[i];
                            if (target.IsAbstract || !PatchedMethods.Add(target))
                            {
                                continue;
                            }

                            harmony.Patch(
                                target,
                                prefix: new HarmonyMethod(
                                    AccessTools.Method(typeof(InputProcessorProfiler), nameof(ProcessorPrefix)))
                                {
                                    priority = Priority.First
                                },
                                finalizer: new HarmonyMethod(
                                    AccessTools.Method(typeof(InputProcessorProfiler), nameof(ProcessorFinalizer)))
                                {
                                    priority = Priority.Last
                                });
                        }
                    }
                    catch
                    {
                        // Some generated/proxy types cannot produce an interface map.
                    }
                }
            }
        }

        Runtime.Log(
            $"per-input-processor profiler installed: {PatchedMethods.Count} effective ProcessInput method(s)");
    }

    private static void CallPrefix()
    {
        if (!Runtime.IsBenchmarking)
        {
            _inputDepth++;
        }
    }

    private static Exception? CallFinalizer(Exception? __exception)
    {
        if (!Runtime.IsBenchmarking && _inputDepth > 0)
        {
            _inputDepth--;
        }

        return __exception;
    }

    private static void ProcessorPrefix(object __instance, out Sample __state)
    {
        __state = default;
        if (Runtime.IsBenchmarking || _inputDepth <= 0 || __instance is null)
        {
            return;
        }

        var type = __instance.GetType();
        __state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        __state.TypeName = type.FullName ?? type.Name;
    }

    private static Exception? ProcessorFinalizer(Exception? __exception, Sample __state)
    {
        if (__state.Started == 0 || __state.TypeName is null)
        {
            return __exception;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started;
        if (elapsed > 0)
        {
            FrameTicks.TryGetValue(__state.TypeName, out var existing);
            FrameTicks[__state.TypeName] = existing + elapsed;
        }

        return __exception;
    }

    public static void FlushFrame()
    {
        foreach (var pair in FrameTicks)
        {
            FreezeDetector.RecordInputDetail(
                $"Processor.{ShortName(pair.Key)}",
                pair.Value);
        }

        FrameTicks.Clear();
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

    private static string ShortName(string name)
    {
        var index = name.LastIndexOf('.');
        return index >= 0 ? name[(index + 1)..] : name;
    }
}

internal static class LateComponentProfiler
{
    private const long ArmHeapBytes = 8L * 1024L * 1024L * 1024L;
    private const double MinimumRecordedMs = 0.25;

    private static readonly HashSet<MethodBase> PatchedMethods = new();
    private static readonly Dictionary<string, long> FrameTicks = new(StringComparer.Ordinal);
    private static bool _armed;

    private struct Sample
    {
        public long Started;
        public string? TypeName;
    }

    public static void Patch(Harmony harmony)
    {
        var iface = AccessTools.TypeByName(
            "Timberborn.BaseComponentSystem.ILateUpdatableComponent");
        if (iface is null)
        {
            Runtime.Log("warning: armed late-component profiler unavailable: interface missing");
            return;
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract || type.IsInterface || !iface.IsAssignableFrom(type))
                {
                    continue;
                }

                try
                {
                    var map = type.GetInterfaceMap(iface);
                    for (var i = 0; i < map.InterfaceMethods.Length; i++)
                    {
                        if (map.InterfaceMethods[i].Name != "LateUpdate")
                        {
                            continue;
                        }

                        var target = map.TargetMethods[i];
                        if (target.IsAbstract || !PatchedMethods.Add(target))
                        {
                            continue;
                        }

                        harmony.Patch(
                            target,
                            prefix: new HarmonyMethod(
                                AccessTools.Method(typeof(LateComponentProfiler), nameof(Prefix)))
                            {
                                priority = Priority.First
                            },
                            finalizer: new HarmonyMethod(
                                AccessTools.Method(typeof(LateComponentProfiler), nameof(Finalizer)))
                            {
                                priority = Priority.Last
                            });
                    }
                }
                catch
                {
                    // Generated/proxy types may not expose a usable interface map.
                }
            }
        }

        Runtime.Log(
            $"armed late-component profiler installed: {PatchedMethods.Count} effective LateUpdate method(s); " +
            "timing activates only when managed heap is >=8GiB");
    }

    public static void ArmForUpcomingFrame(long heapBytes)
    {
        _armed = heapBytes >= ArmHeapBytes;
    }

    private static void Prefix(object __instance, out Sample __state)
    {
        __state = default;
        if (!_armed || Runtime.IsBenchmarking || __instance is null)
        {
            return;
        }

        var type = __instance.GetType();
        __state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        __state.TypeName = type.FullName ?? type.Name;
    }

    private static Exception? Finalizer(Exception? __exception, Sample __state)
    {
        if (__state.Started == 0 || __state.TypeName is null)
        {
            return __exception;
        }

        var elapsed =
            System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started;
        var elapsedMs =
            elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        if (elapsedMs >= MinimumRecordedMs)
        {
            FrameTicks.TryGetValue(__state.TypeName, out var existing);
            FrameTicks[__state.TypeName] = existing + elapsed;
        }

        return __exception;
    }

    public static void FlushFrame()
    {
        foreach (var pair in FrameTicks)
        {
            FreezeDetector.RecordLateBehaviour(pair.Key, pair.Value);
        }

        FrameTicks.Clear();
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


internal static class MonoBehaviourLateUpdateProfiler
{
    private static readonly HashSet<MethodBase> PatchedMethods = new();
    private static readonly Dictionary<string, long> FrameTicks = new(StringComparer.Ordinal);

    private struct Sample
    {
        public long Started;
        public string? TypeName;
    }

    public static void Patch(Harmony harmony)
    {
        var monoBehaviour = AccessTools.TypeByName("UnityEngine.MonoBehaviour");
        if (monoBehaviour is null)
        {
            Runtime.Log("warning: MonoBehaviour LateUpdate profiler unavailable: MonoBehaviour not found");
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                if (type.IsAbstract ||
                    type.IsInterface ||
                    !monoBehaviour.IsAssignableFrom(type))
                {
                    continue;
                }

                MethodInfo? lateUpdate;
                try
                {
                    lateUpdate = type
                        .GetMethods(flags)
                        .FirstOrDefault(method =>
                            method.Name == "LateUpdate" &&
                            !method.IsAbstract &&
                            method.ReturnType == typeof(void) &&
                            method.GetParameters().Length == 0);
                }
                catch
                {
                    continue;
                }

                if (lateUpdate is null || !PatchedMethods.Add(lateUpdate))
                {
                    continue;
                }

                try
                {
                    harmony.Patch(
                        lateUpdate,
                        prefix: new HarmonyMethod(
                            AccessTools.Method(typeof(MonoBehaviourLateUpdateProfiler), nameof(Prefix)))
                        {
                            priority = Priority.First
                        },
                        finalizer: new HarmonyMethod(
                            AccessTools.Method(typeof(MonoBehaviourLateUpdateProfiler), nameof(Finalizer)))
                        {
                            priority = Priority.Last
                        });
                }
                catch
                {
                    PatchedMethods.Remove(lateUpdate);
                }
            }
        }

        Runtime.Log(
            $"MonoBehaviour LateUpdate profiler installed: {PatchedMethods.Count} managed LateUpdate method(s)");
    }

    private static void Prefix(object __instance, out Sample __state)
    {
        __state = default;
        if (Runtime.IsBenchmarking || __instance is null)
        {
            return;
        }

        var type = __instance.GetType();
        __state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        __state.TypeName = type.FullName ?? type.Name;
    }

    private static Exception? Finalizer(Exception? __exception, Sample __state)
    {
        if (__state.Started == 0 || __state.TypeName is null)
        {
            return __exception;
        }

        var elapsed = System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started;
        if (elapsed > 0)
        {
            FrameTicks.TryGetValue(__state.TypeName, out var existing);
            FrameTicks[__state.TypeName] = existing + elapsed;
        }

        return __exception;
    }

    public static void FlushFrame()
    {
        foreach (var pair in FrameTicks)
        {
            FreezeDetector.RecordLateBehaviour(pair.Key, pair.Value);
        }

        FrameTicks.Clear();
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


internal static class KeystoneFaunaTemplatePrewarmer
{
    private static readonly string[] TargetBlueprintNames =
    {
        "KeystoneCow",
        "KeystoneBull",
        "KeystoneDeer",
    };

    private sealed class State
    {
        public bool InProgress;
        public bool Done;
    }

    private static readonly ConditionalWeakTable<object, State> States = new();
    private static WeakReference<object>? _templateInstantiator;
    private static WeakReference<object>? _templateCollectionService;
    private static bool _installed;

    public static void Patch(Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        try
        {
            var instantiatorType =
                AccessTools.TypeByName("Timberborn.TemplateInstantiation.TemplateInstantiator");
            var collectionType =
                AccessTools.TypeByName("Timberborn.TemplateCollectionSystem.TemplateCollectionService");

            if (instantiatorType is null || collectionType is null)
            {
                Runtime.Log(
                    "Keystone fauna template prewarm not installed: " +
                    "TemplateInstantiator/TemplateCollectionService unavailable");
                return;
            }

            foreach (var constructor in instantiatorType.GetConstructors(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                harmony.Patch(
                    constructor,
                    postfix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(KeystoneFaunaTemplatePrewarmer),
                            nameof(TemplateInstantiatorConstructed)))
                    {
                        priority = Priority.Last
                    });
            }

            var load = AccessTools.Method(collectionType, "Load", Type.EmptyTypes);
            var gameInitializerType = AccessTools.TypeByName("Timberborn.GameStartup.GameInitializer");
            var showPrimaryUi = gameInitializerType is null
                ? null
                : AccessTools.DeclaredMethod(gameInitializerType, "ShowPrimaryUI", Type.EmptyTypes);

            if (load is null || showPrimaryUi is null)
            {
                Runtime.Log(
                    "Keystone fauna template prewarm not installed: " +
                    "TemplateCollectionService.Load/GameInitializer.ShowPrimaryUI unavailable");
                return;
            }

            harmony.Patch(
                load,
                postfix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(KeystoneFaunaTemplatePrewarmer),
                        nameof(TemplateCollectionLoaded)))
                {
                    priority = Priority.Last
                });

            harmony.Patch(
                showPrimaryUi,
                prefix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(KeystoneFaunaTemplatePrewarmer),
                        nameof(BeforePrimaryUi)))
                {
                    priority = Priority.First
                });

            _installed = true;
            Runtime.Log(
                "Keystone fauna template prewarm installed: " +
                "Cow/Bull/Deer are deferred until GameInitializer.ShowPrimaryUI, " +
                "then cached through vanilla TemplateInstantiator.CacheInstance");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: Keystone fauna template prewarm installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TemplateInstantiatorConstructed(object __instance)
    {
        if (__instance is null)
        {
            return;
        }

        _templateInstantiator = new WeakReference<object>(__instance);
    }

    private static void TemplateCollectionLoaded(object __instance)
    {
        if (__instance is null)
        {
            return;
        }

        _templateCollectionService = new WeakReference<object>(__instance);
    }

    private static void BeforePrimaryUi()
    {
        Runtime.Log(
            "Keystone fauna template prewarm starting at end of game initialization, before primary UI");
        TryPrewarm();
    }

    private static void TryPrewarm()
    {
        if (_templateInstantiator is null ||
            !_templateInstantiator.TryGetTarget(out var instantiator) ||
            _templateCollectionService is null ||
            !_templateCollectionService.TryGetTarget(out var collectionService))
        {
            return;
        }

        var state = States.GetOrCreateValue(instantiator);
        if (state.Done || state.InProgress)
        {
            return;
        }

        state.InProgress = true;
        try
        {
            var allTemplatesProperty =
                AccessTools.Property(collectionService.GetType(), "AllTemplates");
            var allTemplates = allTemplatesProperty?.GetValue(collectionService) as IEnumerable;
            if (allTemplates is null)
            {
                return;
            }

            var byName = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var blueprint in allTemplates)
            {
                if (blueprint is null)
                {
                    continue;
                }

                var blueprintType = blueprint.GetType();
                var nameProperty = AccessTools.Property(blueprintType, "Name");
                var nameField = AccessTools.Field(blueprintType, "Name");
                var name =
                    nameProperty?.GetValue(blueprint) as string ??
                    nameField?.GetValue(blueprint) as string;

                if (!string.IsNullOrEmpty(name) &&
                    TargetBlueprintNames.Contains(name))
                {
                    byName[name] = blueprint;
                }
            }

            var cacheInstance = instantiator.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "CacheInstance" &&
                    method.GetParameters().Length == 1);

            if (cacheInstance is null)
            {
                Runtime.Log(
                    "warning: Keystone fauna template prewarm skipped: " +
                    "TemplateInstantiator.CacheInstance unavailable");
                state.Done = true;
                return;
            }

            var warmed = 0;
            foreach (var targetName in TargetBlueprintNames)
            {
                if (!byName.TryGetValue(targetName, out var blueprint))
                {
                    Runtime.Log(
                        $"warning: Keystone fauna template prewarm could not find '{targetName}' " +
                        "in TemplateCollectionService.AllTemplates");
                    continue;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                var allocationBefore = AllocationCounter.Read();

                cacheInstance.Invoke(instantiator, new[] { blueprint });

                var elapsedMs =
                    (System.Diagnostics.Stopwatch.GetTimestamp() - started) *
                    1000.0 / System.Diagnostics.Stopwatch.Frequency;
                var allocationDelta =
                    Math.Max(0, AllocationCounter.Read() - allocationBefore);

                warmed++;
                Runtime.Log(
                    $"Keystone fauna template prewarmed: {targetName}; " +
                    $"elapsed={elapsedMs:F1}ms, allocationDelta={FormatBytes(allocationDelta)}");
            }

            state.Done = true;
            Runtime.Log(
                $"Keystone fauna template prewarm complete: {warmed}/{TargetBlueprintNames.Length}; " +
                "uses vanilla template cache, so no duplicate retained prefab cache is created");
        }
        catch (TargetInvocationException ex)
        {
            Runtime.Log(
                $"warning: Keystone fauna template prewarm failed at deferred game-init stage; " +
                $"cache remains retryable: " +
                $"{ex.InnerException?.GetType().Name ?? ex.GetType().Name}: " +
                $"{ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: Keystone fauna template prewarm failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            state.InProgress = false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0:F1}MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:F1}KB";
        }

        return $"{bytes}B";
    }
}

internal static class FaunaInstantiationDetailProfiler
{
    [ThreadStatic] private static int _spawnDepth;
    [ThreadStatic] private static string? _currentBlueprint;
    private static readonly Dictionary<MethodBase, string> Labels = new();

    public static void Patch(Harmony harmony)
    {
        var drainer = AccessTools.TypeByName("Keystone.Mod.Fauna.FaunaSpawnDrainer");
        var spawn = drainer?
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(method =>
                method.Name == "Spawn" &&
                method.ReturnType == typeof(bool));

        if (spawn is null)
        {
            Runtime.Log("fauna instantiation profiler not installed: Spawn not found");
            return;
        }

        harmony.Patch(
            spawn,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(FaunaInstantiationDetailProfiler), nameof(SpawnPrefix)))
            {
                priority = Priority.First
            },
            finalizer: new HarmonyMethod(
                AccessTools.Method(typeof(FaunaInstantiationDetailProfiler), nameof(SpawnFinalizer)))
            {
                priority = Priority.Last
            });

        PatchMethods(harmony, "Timberborn.TemplateInstantiation.TemplateInstantiator", "Instantiate",
            method => true, _ => "Fauna.TemplateInstantiator.Instantiate");
        PatchMethods(harmony, "Timberborn.TemplateInstantiation.TemplateInstantiator", "GetCachedTemplate",
            method => true, _ => "Fauna.TemplateInstantiator.GetCachedTemplate");
        PatchMethods(harmony, "Timberborn.TemplateInstantiation.TemplateInstantiator", "GetInstanceComponents",
            method => true, _ => "Fauna.TemplateInstantiator.GetInstanceComponents");
        PatchMethods(harmony, "Timberborn.PrefabOptimization.OptimizedPrefabInstantiator", "InstantiateInactive",
            method => true, _ => "Fauna.OptimizedPrefabInstantiator.InstantiateInactive");
        PatchMethods(harmony, "Timberborn.PrefabOptimization.PrefabOptimizationChain", "Process",
            method => true, method => $"Fauna.PrefabOptimizationChain.Process/{method.GetParameters().FirstOrDefault()?.ParameterType.Name ?? "none"}");
        PatchMethods(harmony, "Timberborn.PrefabOptimization.PrefabOptimizationChain", "ProcessPrefab",
            method => true, method => $"Fauna.PrefabOptimizationChain.ProcessPrefab/{method.GetParameters().FirstOrDefault()?.ParameterType.Name ?? "none"}");
        PatchMethods(harmony, "Timberborn.BlueprintPrefabSystem.BlueprintPrefabConverter", "Convert",
            method => true, _ => "Fauna.BlueprintPrefabConverter.Convert");
        PatchMethods(harmony, "Timberborn.BaseComponentSystem.BaseInstantiator", "InstantiateInactive",
            method => true, _ => "Fauna.BaseInstantiator.InstantiateInactive");
        PatchMethods(harmony, "Timberborn.BaseComponentSystem.BaseInstantiator", "InstantiateComponents",
            method => true, _ => "Fauna.BaseInstantiator.InstantiateComponents");
        PatchMethods(harmony, "Timberborn.BaseComponentSystem.BaseInstantiator", "InstantiateComponent",
            method => true, _ => "Fauna.BaseInstantiator.InstantiateComponent");
        PatchMethods(harmony, "Timberborn.BaseComponentSystem.ComponentCache", "Initialize",
            method => true, _ => "Fauna.ComponentCache.Initialize");

        Runtime.Log(
            $"fauna instantiation profiler installed: {Labels.Count} nested method(s); " +
            "cache-miss path now includes prefab optimization/conversion; misleading outer EntityService total removed");
    }

    private static void PatchMethods(
        Harmony harmony,
        string typeName,
        string methodName,
        Func<MethodInfo, bool> predicate,
        Func<MethodInfo, string> label)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type is null)
        {
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly;

        foreach (var method in type.GetMethods(flags)
                     .Where(candidate =>
                         candidate.Name == methodName &&
                         !candidate.IsAbstract &&
                         !candidate.ContainsGenericParameters &&
                         predicate(candidate)))
        {
            try
            {
                Labels[method] = label(method);
                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(FaunaInstantiationDetailProfiler), nameof(InnerPrefix)))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(FaunaInstantiationDetailProfiler), nameof(InnerFinalizer)))
                    {
                        priority = Priority.Last
                    });
            }
            catch
            {
                Labels.Remove(method);
            }
        }
    }

    private static void SpawnPrefix(object[] __args)
    {
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        _spawnDepth++;
        if (_spawnDepth == 1)
        {
            _currentBlueprint = TryReadBlueprintName(__args);
        }
    }

    private static Exception? SpawnFinalizer(Exception? __exception)
    {
        if (!Runtime.IsBenchmarking && _spawnDepth > 0)
        {
            _spawnDepth--;
            if (_spawnDepth == 0)
            {
                _currentBlueprint = null;
            }
        }

        return __exception;
    }

    private static string? TryReadBlueprintName(object[] args)
    {
        if (args is null)
        {
            return null;
        }

        foreach (var arg in args)
        {
            if (arg is null)
            {
                continue;
            }

            var type = arg.GetType();
            var member = (MemberInfo?)AccessTools.Property(type, "BlueprintName")
                ?? AccessTools.Field(type, "BlueprintName");
            if (member is PropertyInfo property &&
                property.GetIndexParameters().Length == 0 &&
                property.GetValue(arg) is string propertyValue &&
                !string.IsNullOrWhiteSpace(propertyValue))
            {
                return propertyValue;
            }

            if (member is FieldInfo field &&
                field.GetValue(arg) is string fieldValue &&
                !string.IsNullOrWhiteSpace(fieldValue))
            {
                return fieldValue;
            }
        }

        return null;
    }

    private static void InnerPrefix(MethodBase __originalMethod, out long __state)
    {
        __state = 0;
        if (!Runtime.IsBenchmarking &&
            _spawnDepth > 0 &&
            Labels.ContainsKey(__originalMethod))
        {
            __state = System.Diagnostics.Stopwatch.GetTimestamp();
        }
    }

    private static Exception? InnerFinalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        long __state)
    {
        if (__state != 0 && Labels.TryGetValue(__originalMethod, out var label))
        {
            var taggedLabel = string.IsNullOrWhiteSpace(_currentBlueprint)
                ? label
                : $"{label}[{_currentBlueprint}]";
            FreezeDetector.RecordFaunaDetail(
                taggedLabel,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }

        return __exception;
    }
}


internal static class FaunaSpawnInnerProfiler
{
    [ThreadStatic]
    private static int _spawnDepth;

    private static readonly Dictionary<MethodBase, string> Labels = new();

    public static void Patch(Harmony harmony)
    {
        var drainer = AccessTools.TypeByName("Keystone.Mod.Fauna.FaunaSpawnDrainer");
        var spawn = drainer is null
            ? null
            : drainer.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "Spawn");

        if (spawn is null)
        {
            Runtime.Log("warning: fauna inner profiler unavailable: FaunaSpawnDrainer.Spawn not found");
            return;
        }

        harmony.Patch(
            spawn,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(FaunaSpawnInnerProfiler), nameof(SpawnPrefix)))
            {
                priority = Priority.First
            },
            finalizer: new HarmonyMethod(
                AccessTools.Method(typeof(FaunaSpawnInnerProfiler), nameof(SpawnFinalizer)))
            {
                priority = Priority.Last
            });

        var entityService = AccessTools.TypeByName("Timberborn.EntitySystem.EntityService");
        PatchNamedOverload(
            harmony,
            entityService,
            "Instantiate",
            method => method.GetParameters().Length == 1,
            "Fauna.Instantiate");

        PatchNamedOverload(
            harmony,
            AccessTools.TypeByName("Keystone.Mod.Fauna.KeystoneFaunaAgent"),
            "ConfigureFromRecipe",
            _ => true,
            "Fauna.ConfigureLand");
        PatchNamedOverload(
            harmony,
            AccessTools.TypeByName("Keystone.Mod.Fauna.KeystoneAquaticAgent"),
            "ConfigureFromRecipe",
            _ => true,
            "Fauna.ConfigureAquatic");
        PatchNamedOverload(
            harmony,
            AccessTools.TypeByName("Keystone.Core.Fauna.KeystoneFaunaRegistry") ??
                AccessTools.TypeByName("Keystone.Mod.Fauna.KeystoneFaunaRegistry"),
            "Add",
            _ => true,
            "Fauna.RegistryAdd");

        Runtime.Log(
            $"fauna Spawn inner profiler installed: {Labels.Count} inner method(s)");
    }

    private static void PatchNamedOverload(
        Harmony harmony,
        Type? type,
        string name,
        Func<MethodInfo, bool> predicate,
        string label)
    {
        if (type is null)
        {
            Runtime.Log($"warning: fauna inner profiler target type missing for {label}");
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        foreach (var method in type.GetMethods(flags)
                     .Where(method => method.Name == name && predicate(method)))
        {
            try
            {
                Labels[method] = label;
                harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(FaunaSpawnInnerProfiler), nameof(InnerPrefix)))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(FaunaSpawnInnerProfiler), nameof(InnerFinalizer)))
                    {
                        priority = Priority.Last
                    });
            }
            catch (Exception ex)
            {
                Labels.Remove(method);
                Runtime.Log(
                    $"warning: fauna inner profiler could not patch {type.FullName}.{name}: " +
                    $"{ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private static void SpawnPrefix()
    {
        if (!Runtime.IsBenchmarking)
        {
            _spawnDepth++;
        }
    }

    private static Exception? SpawnFinalizer(Exception? __exception)
    {
        if (!Runtime.IsBenchmarking && _spawnDepth > 0)
        {
            _spawnDepth--;
        }

        return __exception;
    }

    private static void InnerPrefix(out long __state)
    {
        __state = !Runtime.IsBenchmarking && _spawnDepth > 0
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0;
    }

    private static Exception? InnerFinalizer(
        Exception? __exception,
        MethodBase __originalMethod,
        long __state)
    {
        if (__state != 0 && Labels.TryGetValue(__originalMethod, out var label))
        {
            FreezeDetector.RecordFaunaDetail(
                label,
                System.Diagnostics.Stopwatch.GetTimestamp() - __state);
        }

        return __exception;
    }
}

internal static class SoilMoistureProfiler
{
    private static FieldInfo? _terrainMaterialMapField;
    private static FieldInfo? _desertQueueField;
    private static FieldInfo? _desertQueueArrayField;
    private static long _updateCalls;
    private static long _updateAllocatedTotal;
    private static long _updateAllocatedMax;

    private struct Sample
    {
        public bool Active;
        public long Started;
        public long AllocatedBytes;
        public int Gc0;
        public int Gc1;
        public int Gc2;
    }

    private struct UpdateLevelsSample
    {
        public long Started;
        public long ThreadAllocatedBefore;
        public int Gc0;
        public int Gc1;
        public int Gc2;
        public int QueueCountBefore;
        public int QueueCapacityBefore;
        public ManagedHeapSampler.Sample HeapSample;
    }

    public static void Patch(Harmony harmony)
    {
        var type = AccessTools.TypeByName("Timberborn.SoilMoistureSystem.SoilMoistureService");
        var tick = type is null ? null : AccessTools.Method(type, "Tick", Type.EmptyTypes);
        var updateLevels = type is null
            ? null
            : AccessTools.DeclaredMethod(type, "UpdateMoistureLevels", Type.EmptyTypes);
        _terrainMaterialMapField = type is null ? null : AccessTools.Field(type, "_terrainMaterialMap");
        var terrainMaterialMapType = _terrainMaterialMapField?.FieldType;
        _desertQueueField = terrainMaterialMapType is null
            ? null
            : AccessTools.Field(terrainMaterialMapType, "_desertMapChanges");
        if (tick is null)
        {
            Runtime.Log("warning: SoilMoisture profiler unavailable: SoilMoistureService.Tick not found");
            return;
        }

        try
        {
            harmony.Patch(
                tick,
                prefix: new HarmonyMethod(
                    AccessTools.Method(typeof(SoilMoistureProfiler), nameof(Prefix)))
                {
                    priority = Priority.First
                },
                finalizer: new HarmonyMethod(
                    AccessTools.Method(typeof(SoilMoistureProfiler), nameof(Finalizer)))
                {
                    priority = Priority.Last
                });

            if (updateLevels is not null)
            {
                harmony.Patch(
                    updateLevels,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(SoilMoistureProfiler), nameof(UpdateLevelsPrefix)))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(SoilMoistureProfiler), nameof(UpdateLevelsFinalizer)))
                    {
                        priority = Priority.Last
                    });
            }

            Runtime.Log(
                "SoilMoisture allocation/GC profiler installed: outer Tick + sampled UpdateMoistureLevels heap scope");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: SoilMoisture profiler installation failed: " +
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
        __state.AllocatedBytes = AllocationCounter.Read();
        __state.Gc0 = GC.CollectionCount(0);
        __state.Gc1 = GC.CollectionCount(1);
        __state.Gc2 = GC.CollectionCount(2);
    }

    private static Exception? Finalizer(Exception? __exception, Sample __state)
    {
        if (!__state.Active || __state.Started == 0)
        {
            return __exception;
        }

        var allocated =
            AllocationCounter.Read() - __state.AllocatedBytes;
        if (allocated < 0)
        {
            allocated = 0;
        }

        FreezeDetector.RecordSoilMoistureProfile(
            System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started,
            allocated,
            GC.CollectionCount(0) - __state.Gc0,
            GC.CollectionCount(1) - __state.Gc1,
            GC.CollectionCount(2) - __state.Gc2);

        return __exception;
    }

    private static void UpdateLevelsPrefix(object __instance, out UpdateLevelsSample __state)
    {
        __state = default;
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        __state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        __state.ThreadAllocatedBefore = AllocationCounter.Read();
        __state.Gc0 = GC.CollectionCount(0);
        __state.Gc1 = GC.CollectionCount(1);
        __state.Gc2 = GC.CollectionCount(2);
        ReadQueueStats(
            __instance,
            out __state.QueueCountBefore,
            out __state.QueueCapacityBefore);
        __state.HeapSample = ManagedHeapSampler.BeginNamedScopeSample();
    }

    private static Exception? UpdateLevelsFinalizer(
        Exception? __exception,
        object __instance,
        UpdateLevelsSample __state)
    {
        if (__state.Started != 0)
        {
            ManagedHeapSampler.EndNamedScopeSample(
                "SoilMoisture.UpdateLevels",
                __state.HeapSample);

            var elapsed =
                System.Diagnostics.Stopwatch.GetTimestamp() - __state.Started;
            var allocated = Math.Max(
                0,
                AllocationCounter.Read() - __state.ThreadAllocatedBefore);
            _updateCalls++;
            _updateAllocatedTotal += allocated;
            _updateAllocatedMax = Math.Max(_updateAllocatedMax, allocated);

            ReadQueueStats(__instance, out var queueCountAfter, out var queueCapacityAfter);
            var gcChanged =
                GC.CollectionCount(0) != __state.Gc0 ||
                GC.CollectionCount(1) != __state.Gc1 ||
                GC.CollectionCount(2) != __state.Gc2;
            var capacityGrew = queueCapacityAfter > __state.QueueCapacityBefore;

            if ((_updateCalls % 300) == 0 ||
                allocated >= 8L * 1024L * 1024L ||
                gcChanged ||
                capacityGrew)
            {
                Runtime.Log(
                    $"SoilMoisture exact allocation: calls={_updateCalls}, " +
                    $"last={allocated / 1024.0:F1}KiB, " +
                    $"avg={(_updateAllocatedTotal / Math.Max(1.0, _updateCalls)) / 1024.0:F1}KiB, " +
                    $"max={_updateAllocatedMax / 1024.0:F1}KiB, " +
                    $"queue={__state.QueueCountBefore}->{queueCountAfter}, " +
                    $"capacity={__state.QueueCapacityBefore}->{queueCapacityAfter}, " +
                    $"elapsed={elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms, " +
                    $"GC={(gcChanged ? "yes" : "no")}");
            }
        }

        return __exception;
    }

    private static void ReadQueueStats(object instance, out int count, out int capacity)
    {
        count = -1;
        capacity = -1;
        try
        {
            var materialMap = _terrainMaterialMapField?.GetValue(instance);
            var queue = materialMap is null
                ? null
                : _desertQueueField?.GetValue(materialMap);
            if (queue is null)
            {
                return;
            }

            if (queue is ICollection collection)
            {
                count = collection.Count;
            }

            _desertQueueArrayField ??=
                queue.GetType().GetField("_array", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_desertQueueArrayField?.GetValue(queue) is Array array)
            {
                capacity = array.Length;
            }
        }
        catch
        {
            // Diagnostics only.
        }
    }
}

internal static class PlayerLoopPhaseProfiler
{
    private static readonly string[] PhaseNames =
    {
        "UnityEngine.PlayerLoop.EarlyUpdate",
        "UnityEngine.PlayerLoop.FixedUpdate",
        "UnityEngine.PlayerLoop.PreUpdate",
        "UnityEngine.PlayerLoop.Update",
        "UnityEngine.PlayerLoop.PreLateUpdate",
        "UnityEngine.PlayerLoop.PostLateUpdate",
    };

    private static Type? _systemType;
    private static FieldInfo? _typeField;
    private static FieldInfo? _subSystemsField;
    private static FieldInfo? _updateDelegateField;
    private static bool _installed;
    private static int _preLateChildCount;

    private sealed class NamedPlayerLoopMarker
    {
        private readonly string _name;

        public NamedPlayerLoopMarker(string name)
        {
            _name = name;
        }

        public void Invoke() => Mark(_name);
    }

    private sealed class EarlyUpdateStartMarker { }
    private sealed class EarlyUpdateEndMarker { }
    private sealed class FixedUpdateStartMarker { }
    private sealed class FixedUpdateEndMarker { }
    private sealed class PreUpdateStartMarker { }
    private sealed class PreUpdateEndMarker { }
    private sealed class UpdateStartMarker { }
    private sealed class UpdateEndMarker { }
    private sealed class PreLateUpdateStartMarker { }
    private sealed class PreLateUpdateEndMarker { }
    private sealed class PostLateUpdateStartMarker { }
    private sealed class PostLateUpdateEndMarker { }

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        try
        {
            var playerLoopType = AccessTools.TypeByName("UnityEngine.LowLevel.PlayerLoop");
            _systemType = AccessTools.TypeByName("UnityEngine.LowLevel.PlayerLoopSystem");
            if (playerLoopType is null || _systemType is null)
            {
                Runtime.Log(
                    "warning: PlayerLoop phase profiler unavailable: UnityEngine.LowLevel.PlayerLoop types not found");
                return;
            }

            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            _typeField = _systemType.GetField("type", flags);
            _subSystemsField = _systemType.GetField("subSystemList", flags);
            _updateDelegateField = _systemType.GetField("updateDelegate", flags);

            var getCurrent = AccessTools.Method(playerLoopType, "GetCurrentPlayerLoop");
            var setCurrent = playerLoopType
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "SetPlayerLoop" &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == _systemType);

            if (_typeField is null ||
                _subSystemsField is null ||
                _updateDelegateField is null ||
                getCurrent is null ||
                setCurrent is null)
            {
                Runtime.Log(
                    "warning: PlayerLoop phase profiler unavailable: required fields/methods not found");
                return;
            }

            var root = getCurrent.Invoke(null, null);
            if (root is null)
            {
                Runtime.Log(
                    "warning: PlayerLoop phase profiler unavailable: GetCurrentPlayerLoop returned null");
                return;
            }

            var installedPhases = 0;
            _preLateChildCount = 0;
            root = Rewrite(root, ref installedPhases);
            if (installedPhases == 0)
            {
                Runtime.Log(
                    "warning: PlayerLoop phase profiler found no requested phases; leaving loop unchanged");
                return;
            }

            setCurrent.Invoke(null, new[] { root });
            _installed = true;

            Runtime.Log(
                $"PlayerLoop phase profiler installed: {installedPhases}/{PhaseNames.Length} " +
                $"major phases marked; PreLateUpdate children={_preLateChildCount} individually timed");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: PlayerLoop phase profiler installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static object Rewrite(object system, ref int installedPhases)
    {
        var subs = _subSystemsField!.GetValue(system) as Array;
        if (subs is null)
        {
            return system;
        }

        for (var i = 0; i < subs.Length; i++)
        {
            var child = subs.GetValue(i);
            if (child is null)
            {
                continue;
            }

            var childType = _typeField!.GetValue(child) as Type;
            var fullName = childType?.FullName;

            if (fullName is not null && PhaseNames.Contains(fullName))
            {
                child = AddMarkers(child, fullName);
                installedPhases++;
            }
            else
            {
                child = Rewrite(child, ref installedPhases);
            }

            subs.SetValue(child, i);
        }

        _subSystemsField.SetValue(system, subs);
        return system;
    }

    private static object AddMarkers(object phase, string phaseName)
    {
        var original = _subSystemsField!.GetValue(phase) as Array;
        var content = original;

        if (phaseName == "UnityEngine.PlayerLoop.PreLateUpdate" && original is not null)
        {
            content = AddPreLateChildMarkers(original);
        }

        var contentLength = content?.Length ?? 0;
        var replacement = Array.CreateInstance(_systemType!, contentLength + 2);

        var start = CreateMarker(phaseName, isStart: true);
        var end = CreateMarker(phaseName, isStart: false);

        replacement.SetValue(start, 0);
        if (content is not null)
        {
            for (var i = 0; i < contentLength; i++)
            {
                replacement.SetValue(content.GetValue(i), i + 1);
            }
        }
        replacement.SetValue(end, contentLength + 1);

        _subSystemsField.SetValue(phase, replacement);
        return phase;
    }

    private static Array AddPreLateChildMarkers(Array original)
    {
        var replacement = Array.CreateInstance(_systemType!, original.Length * 3);
        var output = 0;

        for (var i = 0; i < original.Length; i++)
        {
            var child = original.GetValue(i);
            if (child is null)
            {
                continue;
            }

            var childType = _typeField!.GetValue(child) as Type;
            var childName = childType?.FullName ?? $"Child{i}";
            var key = $"PreLateChild[{i}].{childName}";

            replacement.SetValue(CreateNamedMarker(key + ".Start"), output++);
            replacement.SetValue(child, output++);
            replacement.SetValue(CreateNamedMarker(key + ".End"), output++);
            _preLateChildCount++;
        }

        if (output == replacement.Length)
        {
            return replacement;
        }

        var trimmed = Array.CreateInstance(_systemType!, output);
        for (var i = 0; i < output; i++)
        {
            trimmed.SetValue(replacement.GetValue(i), i);
        }
        return trimmed;
    }

    private static object CreateNamedMarker(string name)
    {
        var marker = Activator.CreateInstance(_systemType!)!;
        var target = new NamedPlayerLoopMarker(name);
        var invoke = AccessTools.Method(typeof(NamedPlayerLoopMarker), nameof(NamedPlayerLoopMarker.Invoke))!;
        var callback = Delegate.CreateDelegate(_updateDelegateField!.FieldType, target, invoke);

        _typeField!.SetValue(marker, typeof(NamedPlayerLoopMarker));
        _updateDelegateField.SetValue(marker, callback);
        return marker;
    }

    private static object CreateMarker(string phaseName, bool isStart)
    {
        var marker = Activator.CreateInstance(_systemType!)!;
        var methodName = MarkerMethodName(phaseName, isStart);
        var method = AccessTools.Method(typeof(PlayerLoopPhaseProfiler), methodName)!;
        var callback = Delegate.CreateDelegate(_updateDelegateField!.FieldType, method);

        _typeField!.SetValue(marker, MarkerType(phaseName, isStart));
        _updateDelegateField.SetValue(marker, callback);
        return marker;
    }

    private static string MarkerMethodName(string phaseName, bool isStart)
    {
        var shortName = phaseName[(phaseName.LastIndexOf('.') + 1)..];
        return shortName + (isStart ? "Start" : "End");
    }

    private static Type MarkerType(string phaseName, bool isStart)
    {
        var shortName = phaseName[(phaseName.LastIndexOf('.') + 1)..];
        return (shortName, isStart) switch
        {
            ("EarlyUpdate", true) => typeof(EarlyUpdateStartMarker),
            ("EarlyUpdate", false) => typeof(EarlyUpdateEndMarker),
            ("FixedUpdate", true) => typeof(FixedUpdateStartMarker),
            ("FixedUpdate", false) => typeof(FixedUpdateEndMarker),
            ("PreUpdate", true) => typeof(PreUpdateStartMarker),
            ("PreUpdate", false) => typeof(PreUpdateEndMarker),
            ("Update", true) => typeof(UpdateStartMarker),
            ("Update", false) => typeof(UpdateEndMarker),
            ("PreLateUpdate", true) => typeof(PreLateUpdateStartMarker),
            ("PreLateUpdate", false) => typeof(PreLateUpdateEndMarker),
            ("PostLateUpdate", true) => typeof(PostLateUpdateStartMarker),
            _ => typeof(PostLateUpdateEndMarker),
        };
    }

    private static void Mark(string name)
    {
        FreezeDetector.RecordPlayerLoopMarker(
            name,
            System.Diagnostics.Stopwatch.GetTimestamp());
        ManagedHeapSampler.MarkPlayerLoop(name);
        AllocationTracker.MarkPlayerLoop(name);
    }

    private static void EarlyUpdateStart() => Mark("EarlyUpdate.Start");
    private static void EarlyUpdateEnd() => Mark("EarlyUpdate.End");
    private static void FixedUpdateStart() => Mark("FixedUpdate.Start");
    private static void FixedUpdateEnd() => Mark("FixedUpdate.End");
    private static void PreUpdateStart() => Mark("PreUpdate.Start");
    private static void PreUpdateEnd() => Mark("PreUpdate.End");
    private static void UpdateStart() => Mark("Update.Start");
    private static void UpdateEnd() => Mark("Update.End");
    private static void PreLateUpdateStart() => Mark("PreLateUpdate.Start");
    private static void PreLateUpdateEnd() => Mark("PreLateUpdate.End");
    private static void PostLateUpdateStart() => Mark("PostLateUpdate.Start");
    private static void PostLateUpdateEnd() => Mark("PostLateUpdate.End");
}

internal static class SoilContaminationResetOptimizer
{
    private static bool _installed;
    private static FieldInfo? _levelsField;
    private static FieldInfo? _verticalStrideField;
    private static FieldInfo? _mapIndexServiceField;
    private static FieldInfo? _terrainServiceField;
    private static FieldInfo? _terrainMaterialMapField;

    private static Func<object, int, int>? _getColumnCeiling;
    private static Func<object, int, int, object>? _indexToCoordinates;
    private static Func<object, object, object?>? _getContaminatedObjectAt;
    private static Action<object>? _exitContaminatedState;
    private static Action<object>? _resetContaminationMap;
    private static bool _loggedFirstSuccess;

    public static void Patch(Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        try
        {
            var soilType = AccessTools.TypeByName(
                "Timberborn.SoilContaminationSystem.SoilContaminationService");
            var reset = soilType is null
                ? null
                : AccessTools.DeclaredMethod(soilType, "Reset", Type.EmptyTypes);

            if (soilType is null || reset is null)
            {
                Runtime.Log(
                    "SoilContamination reset optimizer not installed: service/Reset not found");
                return;
            }

            const BindingFlags instanceFlags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            _levelsField = soilType.GetField("_threadSafeContaminationLevels", instanceFlags);
            _verticalStrideField = soilType.GetField("_verticalStride", instanceFlags);
            _mapIndexServiceField = soilType.GetField("_mapIndexService", instanceFlags);
            _terrainServiceField = soilType.GetField("_terrainService", instanceFlags);
            _terrainMaterialMapField = soilType.GetField("_terrainMaterialMap", instanceFlags);

            var getObject = soilType.GetMethod(
                "GetContaminatedObjectAt",
                instanceFlags);
            var mapIndexType = _mapIndexServiceField?.FieldType;
            var terrainType = _terrainServiceField?.FieldType;
            var materialType = _terrainMaterialMapField?.FieldType;

            var getColumnCeiling = terrainType?
                .GetMethods(instanceFlags)
                .FirstOrDefault(method =>
                    method.Name == "GetColumnCeiling" &&
                    method.ReturnType == typeof(int) &&
                    method.GetParameters().Length == 1 &&
                    method.GetParameters()[0].ParameterType == typeof(int));

            var indexToCoordinates = mapIndexType?
                .GetMethods(instanceFlags)
                .FirstOrDefault(method =>
                    method.Name == "IndexToCoordinates" &&
                    method.GetParameters().Length == 2 &&
                    method.GetParameters()[0].ParameterType == typeof(int) &&
                    method.GetParameters()[1].ParameterType == typeof(int));

            var resetMap = materialType?
                .GetMethods(instanceFlags)
                .FirstOrDefault(method =>
                    method.Name == "ResetContaminationMap" &&
                    method.GetParameters().Length == 0);

            var exit = getObject?.ReturnType
                .GetMethods(instanceFlags)
                .FirstOrDefault(method =>
                    method.Name == "ExitContaminatedState" &&
                    method.GetParameters().Length == 0);

            if (_levelsField is null ||
                _verticalStrideField is null ||
                _mapIndexServiceField is null ||
                _terrainServiceField is null ||
                _terrainMaterialMapField is null ||
                getColumnCeiling is null ||
                indexToCoordinates is null ||
                getObject is null ||
                exit is null ||
                resetMap is null)
            {
                Runtime.Log(
                    "warning: SoilContamination reset optimizer unavailable: " +
                    "one or more v1.1 Reset members could not be resolved; vanilla Reset retained");
                return;
            }

            _getColumnCeiling = CompileOneIntCall(getColumnCeiling);
            _indexToCoordinates = CompileTwoIntObjectCall(indexToCoordinates);
            _getContaminatedObjectAt = CompileObjectArgObjectCall(getObject);
            _exitContaminatedState = CompileVoidCall(exit);
            _resetContaminationMap = CompileVoidCall(resetMap);

            harmony.Patch(
                reset,
                prefix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(SoilContaminationResetOptimizer),
                        nameof(Prefix)))
                {
                    priority = Priority.First
                });

            _installed = true;
            Runtime.Log(
                "SoilContamination reset optimizer installed: on simulation reset, " +
                "only previously-contaminated cells receive object-state cleanup; " +
                "the backing array is cleared and TerrainMaterialMap is reset once");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: SoilContamination reset optimizer installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static bool Prefix(object __instance)
    {
        if (!_installed || Runtime.IsBenchmarking || __instance is null)
        {
            return true;
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var cleaned = 0;

        try
        {
            var levels = _levelsField!.GetValue(__instance) as float[];
            var stride = (int)(_verticalStrideField!.GetValue(__instance) ?? 0);
            var mapIndex = _mapIndexServiceField!.GetValue(__instance);
            var terrain = _terrainServiceField!.GetValue(__instance);
            var materialMap = _terrainMaterialMapField!.GetValue(__instance);

            if (levels is null ||
                stride <= 0 ||
                mapIndex is null ||
                terrain is null ||
                materialMap is null)
            {
                return true;
            }

            for (var index3D = 0; index3D < levels.Length; index3D++)
            {
                if (levels[index3D] <= 0f)
                {
                    continue;
                }

                var index2D = index3D % stride;
                var ceiling = _getColumnCeiling!(terrain, index3D);
                var coordinates = _indexToCoordinates!(
                    mapIndex,
                    index2D,
                    ceiling);
                var contaminatedObject = _getContaminatedObjectAt!(
                    __instance,
                    coordinates);

                if (contaminatedObject is not null)
                {
                    _exitContaminatedState!(contaminatedObject);
                }

                levels[index3D] = 0f;
                cleaned++;
            }

            Array.Clear(levels, 0, levels.Length);
            _resetContaminationMap!(materialMap);

            var elapsed =
                System.Diagnostics.Stopwatch.GetTimestamp() - started;
            FreezeDetector.RecordSoilDetail("ResetOptimized", elapsed);

            if (!_loggedFirstSuccess ||
                elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= 50.0)
            {
                _loggedFirstSuccess = true;
                Runtime.Log(
                    $"SoilContamination optimized reset: cleaned={cleaned}, " +
                    $"slots={levels.Length}, elapsed=" +
                    $"{elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms");
            }

            return false;
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: SoilContamination optimized reset failed after " +
                $"{cleaned} contaminated cell(s); falling back to vanilla Reset: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static Func<object, int, int> CompileOneIntCall(MethodInfo method)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var value = Expression.Parameter(typeof(int), "value");
        var call = Expression.Call(
            Expression.Convert(target, method.DeclaringType!),
            method,
            value);
        return Expression.Lambda<Func<object, int, int>>(
            call,
            target,
            value).Compile();
    }

    private static Func<object, int, int, object> CompileTwoIntObjectCall(MethodInfo method)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var first = Expression.Parameter(typeof(int), "first");
        var second = Expression.Parameter(typeof(int), "second");
        var call = Expression.Call(
            Expression.Convert(target, method.DeclaringType!),
            method,
            first,
            second);
        return Expression.Lambda<Func<object, int, int, object>>(
            Expression.Convert(call, typeof(object)),
            target,
            first,
            second).Compile();
    }

    private static Func<object, object, object?> CompileObjectArgObjectCall(MethodInfo method)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var argument = Expression.Parameter(typeof(object), "argument");
        var parameterType = method.GetParameters()[0].ParameterType;
        var call = Expression.Call(
            Expression.Convert(target, method.DeclaringType!),
            method,
            Expression.Convert(argument, parameterType));
        return Expression.Lambda<Func<object, object, object?>>(
            Expression.Convert(call, typeof(object)),
            target,
            argument).Compile();
    }

    private static Action<object> CompileVoidCall(MethodInfo method)
    {
        var target = Expression.Parameter(typeof(object), "target");
        var call = Expression.Call(
            Expression.Convert(target, method.DeclaringType!),
            method);
        return Expression.Lambda<Action<object>>(call, target).Compile();
    }
}

internal static class SoilContaminationDeepProfiler
{
    private const double SlowSoilLogMs = 100.0;
    private const long LargeAllocationBytes = 4L * 1024L * 1024L;

    private static Type? _soilType;
    private static MethodInfo? _soilTick;
    private static MethodInfo? _updateLevels;
    private static FieldInfo? _terrainMaterialMapField;
    private static FieldInfo? _contaminationQueueField;
    private static FieldInfo? _contaminationQueueArrayField;
    private static long _updateCalls;
    private static long _updateAllocatedTotal;
    private static long _updateAllocatedMax;

    private struct SoilTickSample
    {
        public bool Active;
        public long Started;
        public long AllocatedBytes;
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
                Runtime.Log("warning: SoilContamination profiler unavailable: type not found");
                return;
            }

            _soilTick = AccessTools.Method(_soilType, "Tick", Type.EmptyTypes);
            _updateLevels = AccessTools.DeclaredMethod(
                _soilType,
                "UpdateContaminationLevels",
                Type.EmptyTypes);
            _terrainMaterialMapField = AccessTools.Field(_soilType, "_terrainMaterialMap");
            var terrainMaterialMapType = _terrainMaterialMapField?.FieldType;
            _contaminationQueueField = terrainMaterialMapType is null
                ? null
                : AccessTools.Field(terrainMaterialMapType, "_contaminationMapChanges");

            if (_soilTick is null)
            {
                Runtime.Log("warning: SoilContamination profiler unavailable: Tick not found");
                return;
            }

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

            if (_updateLevels is not null)
            {
                harmony.Patch(
                    _updateLevels,
                    prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(SoilContaminationDeepProfiler), nameof(BranchPrefix)))
                    {
                        priority = Priority.First
                    },
                    finalizer: new HarmonyMethod(
                        AccessTools.Method(typeof(SoilContaminationDeepProfiler), nameof(UpdateLevelsFinalizer)))
                    {
                        priority = Priority.Last
                    });
            }

            Runtime.Log(
                $"SoilContamination profiler installed: type={_soilType.FullName}; " +
                "outer Tick allocation/GC + high-level UpdateContaminationLevels timing only");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: SoilContamination profiler installation failed: " +
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
        __state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        __state.AllocatedBytes = AllocationCounter.Read();
        __state.Gen0 = GC.CollectionCount(0);
        __state.Gen1 = GC.CollectionCount(1);
        __state.Gen2 = GC.CollectionCount(2);
    }

    private static Exception? SoilTickFinalizer(Exception? __exception, SoilTickSample __state)
    {
        if (!__state.Active)
        {
            return __exception;
        }

        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var elapsed = Math.Max(0, now - __state.Started);
        var allocated = Math.Max(
            0,
            AllocationCounter.Read() - __state.AllocatedBytes);
        var gen0 = GC.CollectionCount(0) - __state.Gen0;
        var gen1 = GC.CollectionCount(1) - __state.Gen1;
        var gen2 = GC.CollectionCount(2) - __state.Gen2;

        FreezeDetector.RecordSoilDetail("Tick", elapsed);
        FreezeDetector.RecordSoilGc(gen0, gen1, gen2);

        var elapsedMs =
            elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (elapsedMs >= SlowSoilLogMs ||
            allocated >= LargeAllocationBytes ||
            gen0 != 0 ||
            gen1 != 0 ||
            gen2 != 0)
        {
            Runtime.Log(
                $"SoilContamination tick profile: elapsed={elapsedMs:F1}ms, " +
                $"allocated={allocated / 1024.0:F1}KiB, GC={gen0}/{gen1}/{gen2}");
        }

        return __exception;
    }

    private struct BranchSample
    {
        public long Started;
        public long ThreadAllocatedBefore;
        public int Gc0;
        public int Gc1;
        public int Gc2;
        public int QueueCountBefore;
        public int QueueCapacityBefore;
        public ManagedHeapSampler.Sample HeapSample;
    }

    private static void BranchPrefix(object __instance, out BranchSample __state)
    {
        __state = default;
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        __state.Started = System.Diagnostics.Stopwatch.GetTimestamp();
        __state.ThreadAllocatedBefore = AllocationCounter.Read();
        __state.Gc0 = GC.CollectionCount(0);
        __state.Gc1 = GC.CollectionCount(1);
        __state.Gc2 = GC.CollectionCount(2);
        ReadQueueStats(
            __instance,
            out __state.QueueCountBefore,
            out __state.QueueCapacityBefore);
        __state.HeapSample = ManagedHeapSampler.BeginNamedScopeSample();
    }

    private static Exception? UpdateLevelsFinalizer(
        Exception? __exception,
        object __instance,
        BranchSample __state)
    {
        if (__state.Started != 0)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            var elapsed = now - __state.Started;
            FreezeDetector.RecordSoilDetail("UpdateContaminationLevels", elapsed);
            ManagedHeapSampler.EndNamedScopeSample(
                "SoilContamination.UpdateLevels",
                __state.HeapSample);

            var allocated = Math.Max(
                0,
                AllocationCounter.Read() - __state.ThreadAllocatedBefore);
            _updateCalls++;
            _updateAllocatedTotal += allocated;
            _updateAllocatedMax = Math.Max(_updateAllocatedMax, allocated);

            ReadQueueStats(__instance, out var queueCountAfter, out var queueCapacityAfter);
            var gcChanged =
                GC.CollectionCount(0) != __state.Gc0 ||
                GC.CollectionCount(1) != __state.Gc1 ||
                GC.CollectionCount(2) != __state.Gc2;
            var capacityGrew = queueCapacityAfter > __state.QueueCapacityBefore;

            if ((_updateCalls % 300) == 0 ||
                allocated >= 8L * 1024L * 1024L ||
                gcChanged ||
                capacityGrew)
            {
                Runtime.Log(
                    $"SoilContamination exact allocation: calls={_updateCalls}, " +
                    $"last={allocated / 1024.0:F1}KiB, " +
                    $"avg={(_updateAllocatedTotal / Math.Max(1.0, _updateCalls)) / 1024.0:F1}KiB, " +
                    $"max={_updateAllocatedMax / 1024.0:F1}KiB, " +
                    $"queue={__state.QueueCountBefore}->{queueCountAfter}, " +
                    $"capacity={__state.QueueCapacityBefore}->{queueCapacityAfter}, " +
                    $"elapsed={elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms, " +
                    $"GC={(gcChanged ? "yes" : "no")}");
            }
        }

        return __exception;
    }

    private static void ReadQueueStats(object instance, out int count, out int capacity)
    {
        count = -1;
        capacity = -1;
        try
        {
            var materialMap = _terrainMaterialMapField?.GetValue(instance);
            var queue = materialMap is null
                ? null
                : _contaminationQueueField?.GetValue(materialMap);
            if (queue is null)
            {
                return;
            }

            if (queue is ICollection collection)
            {
                count = collection.Count;
            }

            _contaminationQueueArrayField ??=
                queue.GetType().GetField("_array", BindingFlags.Instance | BindingFlags.NonPublic);
            if (_contaminationQueueArrayField?.GetValue(queue) is Array array)
            {
                capacity = array.Length;
            }
        }
        catch
        {
            // Diagnostics only.
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
}


internal static class AllocationCounter
{
    private static string _mode = "uninitialized";
    private static bool _cumulative;
    private static readonly Func<long> Reader = Resolve();

    public static string Mode => _mode;
    public static bool IsCumulative => _cumulative;

    public static long Read()
    {
        try { return Reader(); }
        catch { return GC.GetTotalMemory(false); }
    }

    private static Func<long> Resolve()
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public;
        try
        {
            var method = typeof(GC).GetMethod(
                "GetTotalAllocatedBytes",
                flags,
                binder: null,
                types: new[] { typeof(bool) },
                modifiers: null);
            if (method is not null && method.ReturnType == typeof(long))
            {
                var del = (Func<bool, long>)Delegate.CreateDelegate(typeof(Func<bool, long>), method);
                _mode = "GC.GetTotalAllocatedBytes(false) global cumulative";
                _cumulative = true;
                return () => del(false);
            }

            method = typeof(GC).GetMethod(
                "GetTotalAllocatedBytes",
                flags,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (method is not null && method.ReturnType == typeof(long))
            {
                var del = (Func<long>)Delegate.CreateDelegate(typeof(Func<long>), method);
                _mode = "GC.GetTotalAllocatedBytes() global cumulative";
                _cumulative = true;
                return del;
            }
        }
        catch
        {
        }

        _mode = "GC.GetTotalMemory(false) live-heap fallback";
        _cumulative = false;
        return () => GC.GetTotalMemory(false);
    }
}

internal static class AllocationTracker
{
    private const int ReportEveryFrames = 300;
    private const int TopCount = 12;
    private const long SpikeBytes = 1024L * 1024L;

    private static readonly Dictionary<string, long> SingletonAllocations = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> LoopAllocations = new(StringComparer.Ordinal);
    private static int _frames;
    private static string? _lastLoopMarker;
    private static long _lastLoopAllocated;

    public static void Record(string phase, string typeName, long bytes)
    {
        if (Runtime.IsBenchmarking || bytes <= 0)
        {
            return;
        }

        var key = $"{phase}.{ShortName(typeName)}";
        SingletonAllocations.TryGetValue(key, out var existing);
        SingletonAllocations[key] = existing + bytes;

        if (bytes >= SpikeBytes)
        {
            Runtime.Log($"exact allocation spike: {key}={FormatBytes(bytes)} in one call");
        }
    }

    public static void MarkPlayerLoop(string marker)
    {
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        var allocated = AllocationCounter.Read();
        if (_lastLoopMarker is not null)
        {
            var delta = allocated - _lastLoopAllocated;
            if (delta > 0)
            {
                var key = $"{_lastLoopMarker}->{marker}";
                LoopAllocations.TryGetValue(key, out var existing);
                LoopAllocations[key] = existing + delta;
            }
        }

        _lastLoopMarker = marker;
        _lastLoopAllocated = allocated;
    }

    public static void FrameBoundary()
    {
        if (Runtime.IsBenchmarking)
        {
            return;
        }

        _lastLoopMarker = null;
        _lastLoopAllocated = 0;
        _frames++;
        if (_frames < ReportEveryFrames)
        {
            return;
        }

        _frames = 0;
        Report();
        SingletonAllocations.Clear();
        LoopAllocations.Clear();
    }

    private static void Report()
    {
        var singletonTop = SingletonAllocations
            .OrderByDescending(pair => pair.Value)
            .Take(TopCount)
            .Select(pair => $"{pair.Key}={FormatBytes(pair.Value)}")
            .ToArray();

        var loopTop = LoopAllocations
            .OrderByDescending(pair => pair.Value)
            .Take(TopCount)
            .Select(pair => $"{pair.Key}={FormatBytes(pair.Value)}")
            .ToArray();

        Runtime.Log(
            "exact allocation profiler: top singleton allocators: " +
            (singletonTop.Length == 0 ? "none" : string.Join(", ", singletonTop)) +
            "; top PlayerLoop segments: " +
            (loopTop.Length == 0 ? "none" : string.Join(", ", loopTop)));
    }

    private static string ShortName(string name)
    {
        var index = name.LastIndexOf('.');
        return index >= 0 ? name[(index + 1)..] : name;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0:F1}MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:F1}KB";
        }

        return $"{bytes}B";
    }
}

internal static class ManagedHeapSampler
{
    private const long ArmHeapBytes = 6L * 1024L * 1024L * 1024L;
    private const int SampleEveryFrames = 30;
    private const int ReportEveryFrames = 300;
    private const int TopCount = 10;

    public readonly struct Sample
    {
        public readonly bool Active;
        public readonly long HeapBefore;
        public readonly int Gc0;
        public readonly int Gc1;
        public readonly int Gc2;

        public Sample(bool active, long heapBefore, int gc0, int gc1, int gc2)
        {
            Active = active;
            HeapBefore = heapBefore;
            Gc0 = gc0;
            Gc1 = gc1;
            Gc2 = gc2;
        }
    }

    private static readonly Dictionary<string, long> SingletonGrowth = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> PlayerLoopGrowth = new(StringComparer.Ordinal);
    private static int _frame;
    private static int _sampledFrames;
    private static int _framesSinceReport;
    private static bool _sampleThisFrame;
    private static string? _lastLoopMarker;
    private static long _lastLoopHeap;
    private static int _lastLoopGc0;
    private static int _lastLoopGc1;
    private static int _lastLoopGc2;

    public static Sample BeginSingletonSample()
    {
        if (!_sampleThisFrame || Runtime.IsBenchmarking)
        {
            return default;
        }

        return new Sample(
            true,
            GC.GetTotalMemory(false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));
    }

    public static Sample BeginNamedScopeSample() => BeginSingletonSample();

    public static void EndNamedScopeSample(string scopeName, Sample sample)
    {
        if (!sample.Active)
        {
            return;
        }

        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        if (gc0 != sample.Gc0 || gc1 != sample.Gc1 || gc2 != sample.Gc2)
        {
            return;
        }

        var delta = GC.GetTotalMemory(false) - sample.HeapBefore;
        if (delta <= 0)
        {
            return;
        }

        var key = $"Scope.{scopeName}";
        SingletonGrowth.TryGetValue(key, out var existing);
        SingletonGrowth[key] = existing + delta;
    }

    public static void EndSingletonSample(string phase, string typeName, Sample sample)
    {
        if (!sample.Active)
        {
            return;
        }

        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        if (gc0 != sample.Gc0 || gc1 != sample.Gc1 || gc2 != sample.Gc2)
        {
            return;
        }

        var delta = GC.GetTotalMemory(false) - sample.HeapBefore;
        if (delta <= 0)
        {
            return;
        }

        var key = $"{phase}.{ShortName(typeName)}";
        SingletonGrowth.TryGetValue(key, out var existing);
        SingletonGrowth[key] = existing + delta;
    }

    public static void MarkPlayerLoop(string marker)
    {
        if (!_sampleThisFrame || Runtime.IsBenchmarking)
        {
            return;
        }

        var heap = GC.GetTotalMemory(false);
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);

        if (_lastLoopMarker is not null &&
            gc0 == _lastLoopGc0 &&
            gc1 == _lastLoopGc1 &&
            gc2 == _lastLoopGc2)
        {
            var delta = heap - _lastLoopHeap;
            if (delta > 0)
            {
                var key = $"{_lastLoopMarker}->{marker}";
                PlayerLoopGrowth.TryGetValue(key, out var existing);
                PlayerLoopGrowth[key] = existing + delta;
            }
        }

        _lastLoopMarker = marker;
        _lastLoopHeap = heap;
        _lastLoopGc0 = gc0;
        _lastLoopGc1 = gc1;
        _lastLoopGc2 = gc2;
    }

    public static void FrameBoundary(long heapBytes)
    {
        if (_sampleThisFrame)
        {
            _sampledFrames++;
        }

        _lastLoopMarker = null;
        _lastLoopHeap = 0;

        _frame++;
        _framesSinceReport++;

        if (_framesSinceReport >= ReportEveryFrames)
        {
            Report(heapBytes);
            _framesSinceReport = 0;
            _sampledFrames = 0;
            SingletonGrowth.Clear();
            PlayerLoopGrowth.Clear();
        }

        _sampleThisFrame =
            heapBytes >= ArmHeapBytes &&
            (_frame % SampleEveryFrames) == 0;
    }

    private static void Report(long heapBytes)
    {
        if (SingletonGrowth.Count == 0 && PlayerLoopGrowth.Count == 0)
        {
            Runtime.Log(
                $"managed-heap sampler: heap={FormatBytes(heapBytes)}, sampledFrames={_sampledFrames}; " +
                "no positive managed-heap deltas captured in sampled singleton/player-loop scopes");
            return;
        }

        var singletonTop = SingletonGrowth
            .OrderByDescending(pair => pair.Value)
            .Take(TopCount)
            .Select(pair => $"{pair.Key}={FormatBytes(pair.Value)}");
        var loopTop = PlayerLoopGrowth
            .OrderByDescending(pair => pair.Value)
            .Take(TopCount)
            .Select(pair => $"{pair.Key}={FormatBytes(pair.Value)}");

        Runtime.Log(
            $"managed-heap sampler: heap={FormatBytes(heapBytes)}, sampledFrames={_sampledFrames}; " +
            $"top singleton growth: {(SingletonGrowth.Count == 0 ? "none" : string.Join(", ", singletonTop))}; " +
            $"top player-loop growth: {(PlayerLoopGrowth.Count == 0 ? "none" : string.Join(", ", loopTop))}");
    }

    private static string ShortName(string name)
    {
        var index = name.LastIndexOf('.');
        return index >= 0 ? name[(index + 1)..] : name;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0:F1}MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:F1}KB";
        }

        return $"{bytes}B";
    }
}


internal static class IncrementalGcSmoother
{
    private const long StartHeapBytes = 7L * 1024L * 1024L * 1024L;
    private const long HighHeapBytes = 8500L * 1024L * 1024L;
    private const long VeryHighHeapBytes = 9500L * 1024L * 1024L;
    private const ulong NormalBudgetNanoseconds = 1_000_000UL;
    private const ulong HighBudgetNanoseconds = 2_000_000UL;
    private const ulong VeryHighBudgetNanoseconds = 3_000_000UL;

    private static Func<ulong, bool>? _collectIncremental;
    private static Func<bool>? _isIncremental;
    private static bool _disabled;
    private static bool _loggedActive;
    private static bool _loggedUnavailable;

    public static void Initialize()
    {
        try
        {
            var type = AccessTools.TypeByName("UnityEngine.Scripting.GarbageCollector");
            if (type is null)
            {
                LogUnavailable("Unity incremental GC type not found");
                return;
            }

            var collect = type.GetMethod(
                "CollectIncremental",
                BindingFlags.Static | BindingFlags.Public,
                binder: null,
                types: new[] { typeof(ulong) },
                modifiers: null);
            var incrementalProperty = type.GetProperty(
                "isIncremental",
                BindingFlags.Static | BindingFlags.Public);

            if (collect is null || collect.ReturnType != typeof(bool))
            {
                LogUnavailable("CollectIncremental(ulong) not found");
                return;
            }

            _collectIncremental =
                (Func<ulong, bool>)collect.CreateDelegate(typeof(Func<ulong, bool>));

            var getter = incrementalProperty?.GetGetMethod();
            if (getter is not null && getter.ReturnType == typeof(bool))
            {
                _isIncremental =
                    (Func<bool>)getter.CreateDelegate(typeof(Func<bool>));
            }

            Runtime.Log(
                "incremental GC smoother installed: no forced full collections; " +
                "when managed heap exceeds 7GiB, request bounded 1/2/3ms incremental slices");
        }
        catch (Exception ex)
        {
            LogUnavailable($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public static void Pulse(long heapBytes)
    {
        if (_disabled || _collectIncremental is null || heapBytes < StartHeapBytes)
        {
            return;
        }

        try
        {
            if (_isIncremental is not null && !_isIncremental())
            {
                _disabled = true;
                LogUnavailable("Unity reports incremental GC disabled");
                return;
            }

            var budget = heapBytes >= VeryHighHeapBytes
                ? VeryHighBudgetNanoseconds
                : heapBytes >= HighHeapBytes
                    ? HighBudgetNanoseconds
                    : NormalBudgetNanoseconds;

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            _collectIncremental(budget);
            var elapsed =
                System.Diagnostics.Stopwatch.GetTimestamp() - started;
            var elapsedMs =
                elapsed * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

            if (!_loggedActive)
            {
                _loggedActive = true;
                Runtime.Log(
                    $"incremental GC smoother active at {heapBytes / 1024.0 / 1024.0:F0}MiB heap; " +
                    $"requested budget={budget / 1_000_000.0:F1}ms");
            }

            if (elapsedMs >= 20.0)
            {
                Runtime.Log(
                    $"incremental GC slice exceeded soft budget: " +
                    $"requested={budget / 1_000_000.0:F1}ms actual={elapsedMs:F1}ms");
            }
        }
        catch (Exception ex)
        {
            _disabled = true;
            LogUnavailable($"runtime call failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void LogUnavailable(string reason)
    {
        if (_loggedUnavailable)
        {
            return;
        }

        _loggedUnavailable = true;
        Runtime.Log($"incremental GC smoother unavailable; vanilla GC retained: {reason}");
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
    private static readonly Dictionary<string, long> EntityComponentTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> BfrDetailTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> NavigationDetailTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> InputDetailTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> FaunaDetailTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> LateBehaviourTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, KeystoneProfileRecord> KeystoneProfiles = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> PlayerLoopMarkers = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> PhaseGapTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> SoilDetailTicks = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, long> TargetDetailTicks = new(StringComparer.Ordinal);

    private static bool _initialized;
    private static long _lastPhaseEndTicks;
    private static string? _lastPhaseName;
    private static int _soilGc0;
    private static int _soilGc1;
    private static int _soilGc2;
    private static long _soilMoistureTicks;
    private static long _soilMoistureAllocatedBytes;
    private static int _soilMoistureGc0;
    private static int _soilMoistureGc1;
    private static int _soilMoistureGc2;
    private static long _frameStartTicks;
    private static long _frameNumber;

    private sealed class KeystoneProfileRecord
    {
        public long TotalTicks;
        public long MaxTicks;
        public long TotalAllocatedBytes;
        public long MaxAllocatedBytes;
        public int Calls;
        public int Gc0;
        public int Gc1;
        public int Gc2;
    }

    private static int _gc0;
    private static int _gc1;
    private static int _gc2;
    private static long _totalAllocatedBytes;

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
            _totalAllocatedBytes = AllocationCounter.Read();
            SectionTicks.Clear();
            SystemTicks.Clear();
            TickSingletonTicks.Clear();
            UpdateSingletonTicks.Clear();
            LateUpdateSingletonTicks.Clear();
            EntityDispatcherTicks.Clear();
            SlowEntityTicks.Clear();
            EntityComponentTicks.Clear();
            BfrDetailTicks.Clear();
            NavigationDetailTicks.Clear();
            InputDetailTicks.Clear();
            FaunaDetailTicks.Clear();
            LateBehaviourTicks.Clear();
            KeystoneProfiles.Clear();
            PlayerLoopMarkers.Clear();
            PhaseGapTicks.Clear();
            SoilDetailTicks.Clear();
            TargetDetailTicks.Clear();
            _lastPhaseEndTicks = 0;
            _lastPhaseName = null;
            _soilGc0 = 0;
            _soilGc1 = 0;
            _soilGc2 = 0;
            _soilMoistureTicks = 0;
            _soilMoistureAllocatedBytes = 0;
            _soilMoistureGc0 = 0;
            _soilMoistureGc1 = 0;
            _soilMoistureGc2 = 0;
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
                var nextAllocatedBytes = AllocationCounter.Read();
                var allocatedBytesDelta = Math.Max(0, nextAllocatedBytes - _totalAllocatedBytes);

                if (elapsedMs >= SlowFrameMs)
                {
                    var dispatcherTicks =
                        SectionTicks.GetValueOrDefault("UpdateSingletons") +
                        SectionTicks.GetValueOrDefault("LateUpdateSingletons") +
                        SectionTicks.GetValueOrDefault("TickSingletons");
                    var externalOrLoadGap =
                        elapsedMs >= 30000.0 &&
                        ToMs(dispatcherTicks) < 1000.0;

                    if (!externalOrLoadGap)
                    {
                        freezeLine = BuildFreezeLine(
                            elapsedMs,
                            nextGc0 - _gc0,
                            nextGc1 - _gc1,
                            nextGc2 - _gc2,
                            allocatedBytesDelta);
                    }
                    else
                    {
                        Runtime.Log(
                            $"freeze detector skipped external/load gap: " +
                            $"elapsed={elapsedMs:F1}ms, dispatcher={ToMs(dispatcherTicks):F1}ms");
                    }
                }

                _gc0 = nextGc0;
                _gc1 = nextGc1;
                _gc2 = nextGc2;
                _totalAllocatedBytes = nextAllocatedBytes;
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
            EntityComponentTicks.Clear();
            BfrDetailTicks.Clear();
            NavigationDetailTicks.Clear();
            InputDetailTicks.Clear();
            FaunaDetailTicks.Clear();
            LateBehaviourTicks.Clear();
            KeystoneProfiles.Clear();
            PlayerLoopMarkers.Clear();
            PhaseGapTicks.Clear();
            SoilDetailTicks.Clear();
            TargetDetailTicks.Clear();
            _soilGc0 = 0;
            _soilGc1 = 0;
            _soilGc2 = 0;
            _soilMoistureTicks = 0;
            _soilMoistureAllocatedBytes = 0;
            _soilMoistureGc0 = 0;
            _soilMoistureGc1 = 0;
            _soilMoistureGc2 = 0;
            _lastPhaseEndTicks = 0;
            _lastPhaseName = null;
        }

        if (freezeLine is not null)
        {
            Runtime.LogFreeze(freezeLine);
        }

        var heapBytes = GC.GetTotalMemory(false);
        ManagedHeapSampler.FrameBoundary(heapBytes);
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

    public static void RecordEntityComponent(string name, long elapsedTicks) =>
        RecordNamedTicks(EntityComponentTicks, name, elapsedTicks);

    public static void RecordBfrDetail(string name, long elapsedTicks) =>
        RecordNamedTicks(BfrDetailTicks, name, elapsedTicks);

    public static void RecordNavigationDetail(string name, long elapsedTicks) =>
        RecordNamedTicks(NavigationDetailTicks, name, elapsedTicks);

    public static void RecordInputDetail(string name, long elapsedTicks) =>
        RecordNamedTicks(InputDetailTicks, name, elapsedTicks);

    public static void RecordFaunaDetail(string name, long elapsedTicks) =>
        RecordNamedTicks(FaunaDetailTicks, name, elapsedTicks);

    public static void RecordLateBehaviour(string name, long elapsedTicks) =>
        RecordNamedTicks(LateBehaviourTicks, name, elapsedTicks);

    public static void RecordSoilMoistureProfile(
        long elapsedTicks,
        long allocatedBytes,
        int gc0,
        int gc1,
        int gc2)
    {
        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            _soilMoistureTicks += Math.Max(0, elapsedTicks);
            _soilMoistureAllocatedBytes += Math.Max(0, allocatedBytes);
            _soilMoistureGc0 += gc0;
            _soilMoistureGc1 += gc1;
            _soilMoistureGc2 += gc2;
        }
    }

    public static void RecordKeystoneProfile(
        string name,
        long totalTicks,
        long maxTicks,
        long totalAllocatedBytes,
        long maxAllocatedBytes,
        int calls,
        int gc0,
        int gc1,
        int gc2)
    {
        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0)
            {
                return;
            }

            if (!KeystoneProfiles.TryGetValue(name, out var record))
            {
                record = new KeystoneProfileRecord();
                KeystoneProfiles[name] = record;
            }

            record.TotalTicks += Math.Max(0, totalTicks);
            record.MaxTicks = Math.Max(record.MaxTicks, maxTicks);
            record.TotalAllocatedBytes += Math.Max(0, totalAllocatedBytes);
            record.MaxAllocatedBytes = Math.Max(record.MaxAllocatedBytes, maxAllocatedBytes);
            record.Calls += Math.Max(0, calls);
            record.Gc0 += gc0;
            record.Gc1 += gc1;
            record.Gc2 += gc2;
        }
    }

    public static void RecordPlayerLoopMarker(string name, long timestamp)
    {
        lock (Gate)
        {
            if (!_initialized || _frameStartTicks == 0 || timestamp <= 0)
            {
                return;
            }

            PlayerLoopMarkers[name] = timestamp;
        }
    }

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

    public static void RecordTargetDetail(string name, long elapsedTicks) =>
        RecordNamedTicks(TargetDetailTicks, name, elapsedTicks);

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
        int gc2Delta,
        long allocatedBytesDelta)
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

        var componentSamples = EntityComponentTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{ShortName(x.Key)}={ToMs(x.Value):F1}ms")
            .ToArray();
        var componentSampleText = componentSamples.Length == 0
            ? "none"
            : string.Join(", ", componentSamples);

        var bfrDetails = BfrDetailTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{x.Key}={ToMs(x.Value):F1}ms")
            .ToArray();
        var bfrDetailText = bfrDetails.Length == 0
            ? "none"
            : string.Join(", ", bfrDetails);

        var navigationDetails = NavigationDetailTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{x.Key}={ToMs(x.Value):F1}ms")
            .ToArray();
        var navigationDetailText = navigationDetails.Length == 0
            ? "none"
            : string.Join(", ", navigationDetails);

        var inputDetails = InputDetailTicks
            .OrderByDescending(x => x.Value)
            .Take(20)
            .Select(x => $"{x.Key}={ToMs(x.Value):F1}ms")
            .ToArray();
        var inputDetailText = inputDetails.Length == 0
            ? "none"
            : string.Join(", ", inputDetails);

        var faunaDetails = FaunaDetailTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{x.Key}={ToMs(x.Value):F1}ms")
            .ToArray();
        var faunaDetailText = faunaDetails.Length == 0
            ? "none"
            : string.Join(", ", faunaDetails);

        var lateBehaviours = LateBehaviourTicks
            .OrderByDescending(x => x.Value)
            .Take(TopSystemCount)
            .Select(x => $"{ShortName(x.Key)}={ToMs(x.Value):F1}ms")
            .ToArray();
        var lateBehaviourText = lateBehaviours.Length == 0
            ? "none"
            : string.Join(", ", lateBehaviours);

        var keystoneProfiles = KeystoneProfiles
            .OrderByDescending(pair => pair.Value.MaxTicks)
            .Take(TopSystemCount)
            .Select(pair =>
                $"{ShortName(pair.Key)}:calls={pair.Value.Calls}," +
                $"total={ToMs(pair.Value.TotalTicks):F1}ms,max={ToMs(pair.Value.MaxTicks):F1}ms," +
                $"alloc={FormatBytes(pair.Value.TotalAllocatedBytes)},maxAlloc={FormatBytes(pair.Value.MaxAllocatedBytes)}," +
                $"GC={pair.Value.Gc0}/{pair.Value.Gc1}/{pair.Value.Gc2}")
            .ToArray();
        var keystoneProfileText = keystoneProfiles.Length == 0
            ? "none"
            : string.Join(", ", keystoneProfiles);

        var playerLoop = BuildPlayerLoopText();

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

        var targetDetails = TargetDetailTicks.OrderByDescending(x => x.Value).Take(TopSystemCount)
            .Select(x => $"{x.Key}={ToMs(x.Value):F1}ms").ToArray();
        var targetDetailText = targetDetails.Length == 0 ? "none" : string.Join(", ", targetDetails);

        return
            $"{severity} frame={_frameNumber} elapsed={elapsedMs:F1}ms; " +
            $"dispatch: UpdateSingletons={ToMs(updateTicks):F1}ms, " +
            $"LateUpdateSingletons={ToMs(lateTicks):F1}ms, " +
            $"TickSingletons={ToMs(tickTicks):F1}ms, " +
            $"unattributed={ToMs(unattributedTicks):F1}ms; " +
            $"physics={ToMs(physicsTicks):F1}ms; " +
            $"heap={FormatBytes(GC.GetTotalMemory(false))}; " +
            $"GC delta=[gen0:{gc0Delta}, gen1:{gc1Delta}, gen2:{gc2Delta}]; " +
            $"phase gaps: {phaseGapText}; " +
            $"top update singletons: {topUpdateText}; " +
            $"top late-update singletons: {topLateUpdateText}; " +
            $"entity tick dispatchers: {entityDispatcherText}; " +
            $"slow entity ticks: {slowEntityText}; " +
            $"sampled entity components: {componentSampleText}; " +
            $"BFR detail: {bfrDetailText}; " +
            $"navigation detail: {navigationDetailText}; " +
            $"input detail: {inputDetailText}; " +
            $"fauna detail: {faunaDetailText}; " +
            $"target detail: {targetDetailText}; " +
            $"LateUpdate behaviours: {lateBehaviourText}; " +
            $"Keystone profile: {keystoneProfileText}; " +
            $"PlayerLoop: {playerLoop}; " +
            $"top tick singletons: {topTickText}; " +
            $"soil detail: {soilText}; " +
            $"soil GC=[gen0:{_soilGc0}, gen1:{_soilGc1}, gen2:{_soilGc2}]; " +
            $"soil moisture=[time:{ToMs(_soilMoistureTicks):F1}ms, alloc:{FormatBytes(_soilMoistureAllocatedBytes)}, " +
            $"GC:{_soilMoistureGc0}/{_soilMoistureGc1}/{_soilMoistureGc2}]; " +
            $"top systems: {topText}";
    }

    private static string BuildPlayerLoopText()
    {
        if (PlayerLoopMarkers.Count == 0)
        {
            return "none";
        }

        var ordered = new[]
        {
            "EarlyUpdate",
            "FixedUpdate",
            "PreUpdate",
            "Update",
            "PreLateUpdate",
            "PostLateUpdate",
        };

        var parts = new List<string>();
        foreach (var phase in ordered)
        {
            if (PlayerLoopMarkers.TryGetValue(phase + ".Start", out var start) &&
                PlayerLoopMarkers.TryGetValue(phase + ".End", out var end) &&
                end >= start)
            {
                parts.Add($"{phase}={ToMs(end - start):F1}ms");
            }
        }

        for (var i = 0; i < ordered.Length - 1; i++)
        {
            var left = ordered[i];
            var right = ordered[i + 1];
            if (PlayerLoopMarkers.TryGetValue(left + ".End", out var leftEnd) &&
                PlayerLoopMarkers.TryGetValue(right + ".Start", out var rightStart) &&
                rightStart >= leftEnd)
            {
                var gap = rightStart - leftEnd;
                if (ToMs(gap) >= 1.0)
                {
                    parts.Add($"{left}->{right}={ToMs(gap):F1}ms");
                }
            }
        }

        var childSpans = PlayerLoopMarkers
            .Where(pair =>
                pair.Key.StartsWith("PreLateChild[", StringComparison.Ordinal) &&
                pair.Key.EndsWith(".Start", StringComparison.Ordinal))
            .Select(pair =>
            {
                var baseKey = pair.Key[..^6];
                return PlayerLoopMarkers.TryGetValue(baseKey + ".End", out var end) && end >= pair.Value
                    ? (Name: baseKey, Ticks: end - pair.Value)
                    : (Name: baseKey, Ticks: 0L);
            })
            .Where(item => item.Ticks > 0)
            .OrderByDescending(item => item.Ticks)
            .Take(8)
            .ToArray();

        foreach (var child in childSpans)
        {
            var name = child.Name;
            var separator = name.LastIndexOf('.');
            if (separator >= 0)
            {
                name = name[(separator + 1)..];
            }
            parts.Add($"PreLate.{name}={ToMs(child.Ticks):F1}ms");
        }

        return parts.Count == 0 ? "markers present, no complete spans" : string.Join(", ", parts);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024.0 * 1024.0):F1}MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:F1}KB";
        }

        return $"{bytes}B";
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
    private const double MaxPhysicsWallBudgetMs = 50.0;

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
                $"{FixedDeltaTime:F2}s PhysX substeps per UpdateSingleton, " +
                $"adaptive PhysX wall budget ~{MaxPhysicsWallBudgetMs:F0}ms");
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
        long stepAllTicks = 0;
        long simulateTicks = 0;

        while (timer >= FixedDeltaTime && substeps < MaxSubstepsPerUpdate)
        {
            timer -= FixedDeltaTime;

            // Match the original method's state ordering: the timer is decremented
            // before StepAll/Physics.Simulate. If either throws, no stale backlog is
            // left in the simulator field.
            _setTimer(__instance, timer);

            var stepStarted = !Runtime.IsBenchmarking
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0;
            _stepAll(registry, FixedDeltaTime);
            if (stepStarted != 0)
            {
                stepAllTicks += System.Diagnostics.Stopwatch.GetTimestamp() - stepStarted;
            }

            var simulateStarted = !Runtime.IsBenchmarking
                ? System.Diagnostics.Stopwatch.GetTimestamp()
                : 0;
            _simulate(FixedDeltaTime);
            if (simulateStarted != 0)
            {
                simulateTicks += System.Diagnostics.Stopwatch.GetTimestamp() - simulateStarted;
            }

            substeps++;

            if (!Runtime.IsBenchmarking &&
                simulateTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency >= MaxPhysicsWallBudgetMs)
            {
                break;
            }
        }

        if (!Runtime.IsBenchmarking && substeps > 0)
        {
            FreezeDetector.RecordTargetDetail($"TimberPhysics.StepAll[{substeps}]", stepAllTicks);
            FreezeDetector.RecordTargetDetail($"TimberPhysics.PhysX[{substeps}]", simulateTicks);
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
    private const int ChunkSize = 32;

    private static readonly ConditionalWeakTable<object, TerrainState> States = new();
    private static readonly List<WeakReference<object>> TrackedServices = new();

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

            var physicsSimulatorType = AccessTools.TypeByName("TimberPhysics.Core.PhysicsSimulator");
            var physicsUpdate = physicsSimulatorType is null
                ? null
                : AccessTools.Method(physicsSimulatorType, "UpdateSingleton", Type.EmptyTypes);

            if (physicsUpdate is null)
            {
                Runtime.Log(
                    "warning: TimberPhysics terrain merger could not install batched terrain flush; " +
                    "PhysicsSimulator.UpdateSingleton not found. Leaving vanilla terrain colliders.");
                return;
            }

            harmony.Patch(
                physicsUpdate,
                prefix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(TimberPhysicsTerrainColliderMergerPatcher),
                        nameof(FlushDirtyChunksBeforePhysics)))
                { priority = Priority.First });

            _patched = true;
            Runtime.Log(
                $"TimberPhysics terrain collider merger installed: {ChunkSize}x{ChunkSize} chunks, " +
                "exact floor/ceiling interval merging; terrain edits are batched per dirty chunk");
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
            MarkChunkDirty(__instance, __0);
            return false;
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: TimberPhysics merged terrain dirty-mark failed: " +
                $"{ex.GetType().Name}: {ex.Message}; attempting transactional vanilla restore");

            TryDisableMergerAndRestoreVanilla(
                __instance,
                "terrain dirty-mark",
                ex,
                skipNextSpawnXY: false);
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
            // Do not destroy/rebuild the 16x16 merged collider chunk here. One
            // explosion can edit several cells in the same chunk; doing the work
            // once per cell caused the blast-time FPS collapse. SpawnCollidersXY
            // will mark the same chunk dirty, and we rebuild it once before the
            // next physics step.
            MarkChunkDirty(__instance, __0);
            return false;
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: TimberPhysics merged terrain dirty-mark failed during removal: " +
                $"{ex.GetType().Name}: {ex.Message}; attempting transactional vanilla restore");

            TryDisableMergerAndRestoreVanilla(
                __instance,
                "terrain removal dirty-mark",
                ex,
                skipNextSpawnXY: true);
            return false;
        }
    }

    private static void FlushDirtyChunksBeforePhysics()
    {
        if (_disabled)
        {
            return;
        }

        foreach (var pair in TrackedServices.ToArray())
        {
            if (!pair.TryGetTarget(out var service))
            {
                TrackedServices.Remove(pair);
                continue;
            }

            if (!States.TryGetValue(service, out var state) || state.DirtyChunks.Count == 0)
            {
                continue;
            }

            try
            {
                FlushDirtyChunks(service, state);
            }
            catch (Exception ex)
            {
                Runtime.Log(
                    $"warning: TimberPhysics batched terrain chunk rebuild failed: " +
                    $"{ex.GetType().Name}: {ex.Message}; attempting transactional vanilla restore");

                state.DirtyChunks.Clear();
                TryDisableMergerAndRestoreVanilla(
                    service,
                    "batched chunk rebuild",
                    ex,
                    skipNextSpawnXY: false);
                return;
            }
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
        TrackService(service, state);
        ClearState(state);
        state.DirtyChunks.Clear();

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

    private static void MarkChunkDirty(object service, object coordinates)
    {
        var state = States.GetOrCreateValue(service);
        TrackService(service, state);

        var x = Convert.ToInt32(ReadMember(coordinates, _vector2X!));
        var y = Convert.ToInt32(ReadMember(coordinates, _vector2Y!));
        var originX = x / ChunkSize * ChunkSize;
        var originY = y / ChunkSize * ChunkSize;

        state.DirtyChunks.Add(ChunkKey(originX, originY));
        TerrainRecoveryTickLimiter.NotifyTerrainEdit();
    }

    private static void TrackService(object service, TerrainState state)
    {
        if (state.Tracked)
        {
            return;
        }

        state.Tracked = true;
        TrackedServices.Add(new WeakReference<object>(service));
    }

    private static void FlushDirtyChunks(object service, TerrainState state)
    {
        if (state.DirtyChunks.Count == 0)
        {
            return;
        }

        var terrainSize = ReadMember(_mapSizeField!.GetValue(service)!, _terrainSizeMember!)!;
        var sizeX = Convert.ToInt32(ReadMember(terrainSize, _vector2X!));
        var sizeY = Convert.ToInt32(ReadMember(terrainSize, _vector2Y!));

        var dirty = state.DirtyChunks.ToArray();
        state.DirtyChunks.Clear();

        foreach (var key in dirty)
        {
            var originX = unchecked((int)(key >> 32));
            var originY = unchecked((int)(uint)key);

            // Rebuild back-to-back immediately before physics. There is no PhysX
            // simulation between removing the old merged chunk and creating the
            // replacement, so a terrain edit cannot leave a persistent collider gap.
            RemoveChunk(state, key);
            BuildChunk(service, state, originX, originY, sizeX, sizeY);
        }

        if (dirty.Length > 1)
        {
            Runtime.Log(
                $"TimberPhysics terrain edits batched: rebuilt {dirty.Length} dirty chunk(s) once before physics");
        }
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

        state.DirtyChunks.Clear();
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
        public HashSet<long> DirtyChunks { get; } = new();
        public bool Tracked { get; set; }
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

    public static bool TryProfileTickableComponents(object owner)
    {
        try
        {
            var listField = AccessTools.Field(owner.GetType(), "_tickableComponents");
            if (listField?.GetValue(owner) is not IList list)
            {
                return false;
            }

            var plan = GetDispatchPlan(
                owner,
                list,
                "_tickableComponents",
                "_tickableComponent",
                "StartAndTick",
                "Enabled");

            if (plan is null)
            {
                return false;
            }

            // Invoke the same wrapper methods Timberborn normally uses. Do not call
            // ShouldRun() here: any configured direct component throttles remain in
            // force through their existing Harmony prefixes, so sampling does not
            // change component cadence.
            foreach (var entry in plan.Entries)
            {
                if (!entry.Adapter.IsEnabled(entry.Item))
                {
                    continue;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();
                entry.Adapter.Invoke(entry.Item);
                FreezeDetector.RecordEntityComponent(
                    entry.ActualType.FullName ?? entry.ActualType.Name,
                    System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }

            return true;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw;
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
