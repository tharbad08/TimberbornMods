using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

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
