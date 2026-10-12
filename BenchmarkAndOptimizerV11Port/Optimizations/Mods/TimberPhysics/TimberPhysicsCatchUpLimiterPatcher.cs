using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

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
