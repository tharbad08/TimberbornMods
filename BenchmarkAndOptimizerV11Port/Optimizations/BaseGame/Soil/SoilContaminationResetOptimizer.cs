using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

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
