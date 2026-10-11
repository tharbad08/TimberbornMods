using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using Bindito.Core;
using HarmonyLib;
using Timberborn.SingletonSystem;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Read-only FPS and simulation-throughput logger. No camera access, no
/// rendering toggle, and no game-speed or simulation-timing mutations.
/// Tick count is collected from the pre-existing lifecycle postfix.
/// </summary>
public sealed class SimulationThroughputService(IContainer container)
    : ILoadableSingleton, IUpdatableSingleton, IUnloadableSingleton
{
    private const long ProbeIntervalMs = 15000;
    private static SimulationThroughputService? _current;
    private readonly IContainer _container = container;
    private long _startedTicks;
    private long _previousLogTicks;
    private int _previousFrameCount;
    private int _fallbackFrameCount;
    private PropertyInfo? _frameCountProperty;
    private bool _useUnityFrameCount;
    private object? _speedManager;
    private PropertyInfo? _currentSpeed;
    private string _windowStartSpeed = "unknown";
    private long _completedSimulationTicks;
    private long _previousCompletedSimulationTicks;
    private object? _dayNightCycle;
    private PropertyInfo? _partialDayNumber;
    private double? _previousGameDay;
    private bool _clockAvailabilityLogged;

    internal static void RecordCompletedSimulationTick()
    {
        var active = _current;
        if (active is not null)
            Interlocked.Increment(ref active._completedSimulationTicks);
    }

    public void Load()
    {
        _current = this;
        _startedTicks = Stopwatch.GetTimestamp();
        _previousLogTicks = _startedTicks;
        _previousFrameCount = ReadFrameCount();
        ResolveSpeedManager();
        _windowStartSpeed = ReadGameSpeed();
        ResolveDayNightCycle();
        _previousGameDay = ReadPartialDayNumber();
        _completedSimulationTicks = 0;
        _previousCompletedSimulationTicks = 0;
        Runtime.Log("SIM.PROGRESS logger loaded: read-only 15s FPS, selected game speed, " +
            "completed tick dispatches/sec and IDayNightCycle game-time advancement; " +
            "UI-only render mask and toggle removed");
    }

    public void UpdateSingleton()
    {
        _fallbackFrameCount++;
        var now = Stopwatch.GetTimestamp();
        if ((now - _previousLogTicks) * 1000.0 / Stopwatch.Frequency < ProbeIntervalMs)
            return;

        var elapsed = (now - _previousLogTicks) / (double)Stopwatch.Frequency;
        var currentFrame = ReadFrameCount();
        var frames = currentFrame - _previousFrameCount;
        if (!_useUnityFrameCount || frames <= 0)
        {
            frames = _fallbackFrameCount;
            _useUnityFrameCount = false;
        }

        var fps = elapsed > 0 ? frames / elapsed : 0;
        var speedEnd = ReadGameSpeed();
        var currentTicks = Interlocked.Read(ref _completedSimulationTicks);
        var ticks = Math.Max(0, currentTicks - _previousCompletedSimulationTicks);
        var ticksPerRealSec = elapsed > 0 ? ticks / elapsed : 0;
        var gameDayEnd = ReadPartialDayNumber();
        double? dayDelta = _previousGameDay.HasValue && gameDayEnd.HasValue
            ? gameDayEnd.Value - _previousGameDay.Value : null;
        if (dayDelta < 0) dayDelta = null; // World reload or clock reset.
        var daysPerRealMin = dayDelta.HasValue && elapsed > 0
            ? (dayDelta.Value * 60.0 / elapsed).ToString("F5", System.Globalization.CultureInfo.InvariantCulture)
            : "na";
        var hoursPerRealMin = dayDelta.HasValue && elapsed > 0
            ? (dayDelta.Value * 1440.0 / elapsed).ToString("F3", System.Globalization.CultureInfo.InvariantCulture)
            : "na";

        Runtime.Log($"SIM.PROGRESS interval={elapsed:F2}s fps={fps:F2} frames={frames} " +
            $"speedStart={_windowStartSpeed} speedEnd={speedEnd} " +
            $"simTicks={ticks} simTicksPerRealSecond={ticksPerRealSec:F3} " +
            $"gameDaysPerRealMinute={daysPerRealMin} gameHoursPerRealMinute={hoursPerRealMin} " +
            $"clock={(gameDayEnd.HasValue ? "IDayNightCycle.PartialDayNumber" : "unavailable")} " +
            $"source={(_useUnityFrameCount ? "Unity.Time.frameCount" : "UpdateSingleton-count")} " +
            $"upTime={(now - _startedTicks) / (double)Stopwatch.Frequency:F0}s; " +
            "tick metric counts completed singleton-tick dispatcher calls, not entity ticks");

        _previousCompletedSimulationTicks = currentTicks;
        _previousGameDay = gameDayEnd;
        _windowStartSpeed = speedEnd;
        _previousLogTicks = now;
        _previousFrameCount = currentFrame;
        _fallbackFrameCount = 0;
    }

    public void Unload()
    {
        if (_current == this)
            _current = null;
        _speedManager = null;
        _currentSpeed = null;
        _dayNightCycle = null;
        _partialDayNumber = null;
    }

    private int ReadFrameCount()
    {
        try
        {
            if (_frameCountProperty is null)
                _frameCountProperty = AccessTools.TypeByName("UnityEngine.Time")
                    ?.GetProperty("frameCount", BindingFlags.Public | BindingFlags.Static);
            if (_frameCountProperty is not null)
            {
                _useUnityFrameCount = true;
                return (int)(_frameCountProperty.GetValue(null) ?? 0);
            }
        }
        catch { }
        _useUnityFrameCount = false;
        return _fallbackFrameCount;
    }

    private void ResolveSpeedManager()
    {
        try
        {
            var type = AccessTools.TypeByName("Timberborn.TimeSystem.SpeedManager")
                ?? AccessTools.TypeByName("SpeedManager");
            if (type is null) return;
            var get = _container.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetInstance" &&
                    m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (get is null) return;
            _speedManager = get.MakeGenericMethod(type).Invoke(_container, null);
            _currentSpeed = type.GetProperty("CurrentSpeed",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Runtime.Log("SIM.PROGRESS speed probe=" +
                (_speedManager is not null && _currentSpeed is not null
                    ? "SpeedManager.CurrentSpeed" : "unavailable"));
        }
        catch (Exception e)
        {
            Runtime.Log("SIM.PROGRESS speed probe unavailable: " + e.GetType().Name);
        }
    }

    private string ReadGameSpeed()
    {
        try
        {
            if (_speedManager is null || _currentSpeed is null) ResolveSpeedManager();
            var value = _currentSpeed?.GetValue(_speedManager);
            return value is IFormattable formatted
                ? formatted.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
                : value?.ToString() ?? "unknown";
        }
        catch { return "unknown"; }
    }

    private void ResolveDayNightCycle()
    {
        if (_dayNightCycle is not null && _partialDayNumber is not null) return;
        try
        {
            var type = AccessTools.TypeByName("Timberborn.TimeSystem.IDayNightCycle");
            if (type is null) return;
            var get = _container.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetInstance" &&
                    m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (get is null) return;
            var cycle = get.MakeGenericMethod(type).Invoke(_container, null);
            var prop = type.GetProperty("PartialDayNumber",
                BindingFlags.Public | BindingFlags.Instance);
            if (cycle is null || prop is null || !prop.CanRead) return;
            _dayNightCycle = cycle;
            _partialDayNumber = prop;
            if (!_clockAvailabilityLogged)
            {
                _clockAvailabilityLogged = true;
                Runtime.Log("SIM.PROGRESS clock resolved IDayNightCycle.PartialDayNumber");
            }
        }
        catch (Exception e)
        {
            if (!_clockAvailabilityLogged)
            {
                _clockAvailabilityLogged = true;
                Runtime.Log("SIM.PROGRESS game clock unavailable: " + e.GetType().Name);
            }
        }
    }

    private double? ReadPartialDayNumber()
    {
        try
        {
            if (_partialDayNumber is null || _dayNightCycle is null) ResolveDayNightCycle();
            if (_dayNightCycle is null || _partialDayNumber is null) return null;
            var value = _partialDayNumber.GetValue(_dayNightCycle);
            if (value is null) return null;
            var number = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
            return double.IsNaN(number) || double.IsInfinity(number) ? null : number;
        }
        catch { return null; }
    }
}
