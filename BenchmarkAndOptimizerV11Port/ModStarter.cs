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

        return false;
    }
}


internal static class TimberPhysicsTerrainColliderMergerPatcher
{
    private const string ServiceTypeName = "TimberPhysics.Terrain.TerrainColliderService";
    private const string CoordinateSystemTypeName = "Timberborn.Coordinates.CoordinateSystem";
    private const string Vector2IntTypeName = "UnityEngine.Vector2Int";
    private const string Vector3TypeName = "UnityEngine.Vector3";
    private const string BoxColliderTypeName = "UnityEngine.BoxCollider";
    private const string UnityObjectTypeName = "UnityEngine.Object";
    private const int ChunkSize = 16;

    private static readonly ConditionalWeakTable<object, TerrainState> States = new();

    private static bool _patched;
    private static Type? _serviceType;
    private static Type? _vector2IntType;
    private static Type? _vector3Type;
    private static Type? _boxColliderType;
    private static FieldInfo? _rootObjectField;
    private static FieldInfo? _columnTerrainMapField;
    private static FieldInfo? _mapSizeField;
    private static FieldInfo? _mapIndexServiceField;
    private static PropertyInfo? _terrainSizeProperty;
    private static PropertyInfo? _columnCountProperty;
    private static MethodInfo? _getColumnMethod;
    private static PropertyInfo? _verticalStrideProperty;
    private static MethodInfo? _cellToIndexMethod;
    private static MethodInfo? _gridToWorldCenteredMethod;
    private static MethodInfo? _addComponentByTypeMethod;
    private static MethodInfo? _destroyMethod;
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
            _serviceType = AccessTools.TypeByName(ServiceTypeName);
            _vector2IntType = AccessTools.TypeByName(Vector2IntTypeName);
            _vector3Type = AccessTools.TypeByName(Vector3TypeName);
            _boxColliderType = AccessTools.TypeByName(BoxColliderTypeName);
            var coordinateSystemType = AccessTools.TypeByName(CoordinateSystemTypeName);
            var unityObjectType = AccessTools.TypeByName(UnityObjectTypeName);

            if (_serviceType is null || _vector2IntType is null || _vector3Type is null ||
                _boxColliderType is null || coordinateSystemType is null || unityObjectType is null)
            {
                Runtime.Log("TimberPhysics terrain merger not installed: required types are not loaded");
                return;
            }

            _rootObjectField = AccessTools.Field(_serviceType, "_rootObject");
            _columnTerrainMapField = AccessTools.Field(_serviceType, "_columnTerrainMap");
            _mapSizeField = AccessTools.Field(_serviceType, "_mapSize");
            _mapIndexServiceField = AccessTools.Field(_serviceType, "_mapIndexService");

            var spawnAll = AccessTools.Method(_serviceType, "SpawnColliders", Type.EmptyTypes);
            var spawnXY = AccessTools.Method(_serviceType, "SpawnCollidersXY", new[] { _vector2IntType });
            var removeXY = AccessTools.Method(_serviceType, "RemoveCollidersXY", new[] { _vector2IntType });

            if (_rootObjectField is null || _columnTerrainMapField is null ||
                _mapSizeField is null || _mapIndexServiceField is null ||
                spawnAll is null || spawnXY is null || removeXY is null)
            {
                Runtime.Log(
                    "warning: TimberPhysics terrain merger unavailable; " +
                    "TerrainColliderService does not match expected v1.1.1.0 layout");
                return;
            }

            var mapSizeType = _mapSizeField.FieldType;
            var columnTerrainMapType = _columnTerrainMapField.FieldType;
            var mapIndexServiceType = _mapIndexServiceField.FieldType;

            _terrainSizeProperty = AccessTools.Property(mapSizeType, "TerrainSize");
            _columnCountProperty = AccessTools.Property(columnTerrainMapType, "ColumnCount");
            _getColumnMethod = AccessTools.Method(columnTerrainMapType, "GetColumn", new[] { typeof(int) });
            _verticalStrideProperty = AccessTools.Property(mapIndexServiceType, "VerticalStride");
            _cellToIndexMethod = AccessTools.Method(mapIndexServiceType, "CellToIndex", new[] { _vector2IntType });
            _gridToWorldCenteredMethod = AccessTools.Method(
                coordinateSystemType, "GridToWorldCentered", new[] { _vector3Type });

            var gameObjectType = _rootObjectField.FieldType;
            _addComponentByTypeMethod = AccessTools.Method(gameObjectType, "AddComponent", new[] { typeof(Type) });
            _destroyMethod = AccessTools.Method(unityObjectType, "Destroy", new[] { unityObjectType });

            _vector2X = FindMember(_vector2IntType, "x");
            _vector2Y = FindMember(_vector2IntType, "y");
            _vector3X = FindMember(_vector3Type, "x");
            _vector3Y = FindMember(_vector3Type, "y");
            _vector3Z = FindMember(_vector3Type, "z");
            _colliderCenter = FindMember(_boxColliderType, "center");
            _colliderSize = FindMember(_boxColliderType, "size");

            var terrainColumnType = _getColumnMethod?.ReturnType;
            if (terrainColumnType is not null)
            {
                _terrainFloor = FindMember(terrainColumnType, "Floor");
                _terrainCeiling = FindMember(terrainColumnType, "Ceiling");
            }

            if (_terrainSizeProperty is null || _columnCountProperty is null ||
                _getColumnMethod is null || _verticalStrideProperty is null ||
                _cellToIndexMethod is null || _gridToWorldCenteredMethod is null ||
                _addComponentByTypeMethod is null || _destroyMethod is null ||
                _vector2X is null || _vector2Y is null ||
                _vector3X is null || _vector3Y is null || _vector3Z is null ||
                _terrainFloor is null || _terrainCeiling is null ||
                _colliderCenter is null || _colliderSize is null)
            {
                Runtime.Log(
                    "warning: TimberPhysics terrain merger could not resolve all required members; " +
                    "leaving vanilla terrain colliders");
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

    private static bool SpawnAllPrefix(object __instance)
    {
        try
        {
            BuildAll(__instance);
            return false;
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: TimberPhysics merged terrain initial build failed: " +
                $"{ex.GetType().Name}: {ex.Message}; falling back to vanilla terrain colliders");
            States.Remove(__instance);
            return true;
        }
    }

    private static bool SpawnXYPrefix(object __instance, object __0)
    {
        try
        {
            RebuildChunk(__instance, __0);
            return false;
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: TimberPhysics merged terrain chunk rebuild failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static bool RemoveXYPrefix(object __instance, object __0)
    {
        try
        {
            RemoveChunkForCoordinates(__instance, __0);
            return false;
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: TimberPhysics merged terrain chunk removal failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return true;
        }
    }

    private static void BuildAll(object service)
    {
        var state = States.GetOrCreateValue(service);
        ClearState(state);

        var terrainSize = _terrainSizeProperty!.GetValue(_mapSizeField!.GetValue(service)!)!;
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

        var terrainSize = _terrainSizeProperty!.GetValue(_mapSizeField!.GetValue(service)!)!;
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
        var columnCounts = _columnCountProperty!.GetValue(columnTerrainMap)!;
        var verticalStride = Convert.ToInt32(_verticalStrideProperty!.GetValue(mapIndexService));

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
                    var terrainColumn = _getColumnMethod!.Invoke(columnTerrainMap, new object[] { index3D })!;
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
        var collider = _addComponentByTypeMethod!.Invoke(
            rootObject,
            new object[] { _boxColliderType! })!;

        WriteMember(collider, _colliderCenter!, center);
        WriteMember(collider, _colliderSize!, size);
        return collider;
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

    private static MemberInfo? FindMember(Type type, string name) =>
        (MemberInfo?)AccessTools.Field(type, name) ?? AccessTools.Property(type, name);

    private static object? ReadMember(object instance, MemberInfo member) =>
        member switch
        {
            FieldInfo field => field.GetValue(instance),
            PropertyInfo property => property.GetValue(instance),
            _ => throw new InvalidOperationException($"Unsupported member: {member}")
        };

    private static void WriteMember(object instance, MemberInfo member, object value)
    {
        switch (member)
        {
            case FieldInfo field:
                field.SetValue(instance, value);
                break;
            case PropertyInfo property:
                property.SetValue(instance, value);
                break;
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
            try
            {
                _destroyMethod!.Invoke(null, new[] { collider });
            }
            catch
            {
                // A destroyed collider should not prevent rebuilding the rest.
            }
        }

        state.Chunks.Remove(key);
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

    public static void Initialize(string modPath, Harmony harmony)
    {
        _harmony = harmony;
        ModPath = modPath;
        Directory.CreateDirectory(ModPath);
        RotateOversizedLog();

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

            if (method.DeclaringType != type)
            {
                LogOnce(
                    $"inherited-direct-method:{type.FullName}",
                    $"warning: {type.FullName} inherits {method.Name} from {method.DeclaringType?.FullName}; cannot throttle safely without affecting sibling types");
                continue;
            }

            try
            {
                var prefix = AccessTools.Method(typeof(Runtime), nameof(DirectThrottlePrefix));
                _harmony.Patch(
                    method,
                    prefix: new HarmonyMethod(prefix) { priority = Priority.First });

                DirectPatchedMethods.Add(method);
                DirectMethodsByType[pair.Key] = method;
                Log($"direct throttle patch installed: {type.FullName}.{method.Name} interval={GetInterval(type)}");
            }
            catch (Exception ex)
            {
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

    private static void RotateOversizedLog()
    {
        try
        {
            var log = Path.Combine(ModPath, "optimizer-v11.log");
            if (!File.Exists(log) || new FileInfo(log).Length < 5 * 1024 * 1024)
            {
                return;
            }

            var previous = Path.Combine(ModPath, "optimizer-v11.previous.log");
            if (File.Exists(previous))
            {
                File.Delete(previous);
            }
            File.Move(log, previous);
        }
        catch
        {
            // Never block startup because an old diagnostic log cannot be rotated.
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
