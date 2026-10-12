using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

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
