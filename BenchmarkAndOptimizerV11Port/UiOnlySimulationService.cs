using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Bindito.Core;
using HarmonyLib;
using Timberborn.SingletonSystem;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// UI-only graphics EXPERIMENT: alter only the render-layer mask of the
/// active gameplay Camera. We intentionally never disable GameObjects,
/// renderers/MonoBehaviours/scene roots, or simulator/AI/tick schedules.
/// Unity UI Toolkit is screen-overlay and still updates (including FPS).
/// All mutations are reversible; when unload occurs the old camera mask
/// is restored if nobody else has changed it in the meantime.
/// </summary>
public sealed class UiOnlySimulationService(IContainer container)
    : ILoadableSingleton, IUpdatableSingleton, IUnloadableSingleton
{
    private const long ProbeIntervalMs = 15000;
    private static UiOnlySimulationService? _current;
    private static bool _enabled;
    private static string _status = "Not in an active game. UI-only is off.";

    private readonly IContainer _container = container;
    private object? _camera;
    private int _originalMask;
    private int _appliedMask;
    private long _startedTicks;
    private long _previousLogTicks;
    private int _previousFrameCount;
    private int _fallbackFrameCount;
    private PropertyInfo? _cullingMask;
    private PropertyInfo? _frameCountProperty;
    private bool _useUnityFrameCount;
    private bool _cameraLookupWarned;
    private object? _speedManager;
    private PropertyInfo? _currentSpeed;
    private string _windowStartSpeed = "unknown";

    public static bool Enabled => _enabled;
    public static string Status => _status;

    public void Load()
    {
        _current = this;
        _enabled = false;
        _status = "UI-only OFF — 3D world visible. FPS logging active.";
        _startedTicks = Stopwatch.GetTimestamp();
        _previousLogTicks = _startedTicks;
        _previousFrameCount = ReadFrameCount();
        ResolveSpeedManager();
        _windowStartSpeed = ReadGameSpeed();
        Runtime.Log("UIONLY service loaded: disabled by default; records FPS " +
            "every 15s in BOTH modes using Unity Time.frameCount where available; " +
            "normal gameplay, native FramesPerSecondPanel, TopBar and all UI retained");
    }

    public static bool TrySetEnabled(bool enabled)
    {
        if (_current is null)
        {
            _status = "UI-only unavailable in main menu or before gameplay initializes.";
            return false;
        }

        return _current.SetEnabled(enabled);
    }

    private bool SetEnabled(bool enabled)
    {
        if (enabled == _enabled) return true;
        if (enabled)
        {
            try
            {
                var camera = ResolveGameCamera();
                if (camera is null) return Unavailable("No gameplay camera found");
                var maskProp = camera.GetType().GetProperty("cullingMask",
                    BindingFlags.Public | BindingFlags.Instance);
                if (maskProp is null || !maskProp.CanRead || !maskProp.CanWrite ||
                    maskProp.PropertyType != typeof(int))
                    return Unavailable("Camera.cullingMask API unavailable");

                var oldMask = (int)(maskProp.GetValue(camera) ?? 0);
                // Named UI layer is preserved if present. Unity UI Toolkit
                // panels render as overlays independently from the 3D camera.
                // A missing UI layer yields an empty world mask (0).
                int uiLayer = ReadUiLayer();
                int keep = uiLayer is >= 0 and < 32 ? 1 << uiLayer : 0;
                var newMask = oldMask & keep;
                if (newMask == oldMask)
                    return Unavailable("Camera has no 3D layers to hide");

                maskProp.SetValue(camera, newMask);
                _camera = camera;
                _cullingMask = maskProp;
                _originalMask = oldMask;
                _appliedMask = newMask;
                _enabled = true;
                _status = "UI-only ON — 3D world hidden; normal simulation and UI continue.";
                Runtime.Log($"UIONLY ON: main gameplay camera cullingMask " +
                    $"{oldMask:X8}->{newMask:X8}; preservedUILayer={uiLayer}; " +
                    "no GameObject/Renderer/Animator enabled states changed");
                ResetFpsWindow();
                return true;
            }
            catch (Exception e)
            {
                return Unavailable("Enable failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        RestoreCameraMask("checkbox OFF");
        _enabled = false;
        _status = "UI-only OFF — 3D world visible. FPS logging active.";
        ResetFpsWindow();
        return true;
    }

    public void UpdateSingleton()
    {
        // Exactly one stopwatch check per frame, and no allocations on the
        // normal path. Slow disk I/O logging happens only once per 15 seconds.
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
        var mode = _enabled ? "UI_ONLY" : "NORMAL";
        var speedEnd = ReadGameSpeed();
        Runtime.Log($"UIONLY.FPS mode={mode} interval={elapsed:F2}s " +
            $"fps={fps:F2} frames={frames} speedStart={_windowStartSpeed} speedEnd={speedEnd} " +
            $"source={(_useUnityFrameCount ? "Unity.Time.frameCount" : "UpdateSingleton-count")} " +
            $"upTime={(now - _startedTicks) / (double)Stopwatch.Frequency:F0}s; " +
            "values are overall frame throughput, not in-game simulation ticks/sec");

        _windowStartSpeed = speedEnd;
        _previousLogTicks = now;
        _previousFrameCount = currentFrame;
        _fallbackFrameCount = 0;

        // Do not force-read Camera every frame. If another mod changes the
        // mask, do not overwrite its state; report and leave it untouched.
        if (_enabled && _camera is not null && _cullingMask is not null)
        {
            try
            {
                if ((int)(_cullingMask.GetValue(_camera) ?? -1) != _appliedMask)
                {
                    _status = "Other mod changed camera rendering while UI-only active.";
                    Runtime.Log("UIONLY warning: camera mask changed externally; " +
                        "not overwriting external state");
                }
            }
            catch (Exception e)
            {
                Runtime.Log($"UIONLY camera check unavailable: {e.GetType().Name}");
            }
        }
    }

    public void Unload()
    {
        if (_current != this) return;
        RestoreCameraMask("game scene unload");
        _enabled = false;
        _camera = null;
        _cullingMask = null;
        _current = null;
        _status = "Not in an active game. UI-only is off.";
        Runtime.Log("UIONLY restored/off during scene unload");
    }

    private bool Unavailable(string reason)
    {
        _status = "UI-only OFF: " + reason;
        if (!_cameraLookupWarned)
        {
            _cameraLookupWarned = true;
            Runtime.Log("UIONLY unavailable: " + reason);
        }
        return false;
    }

    private void RestoreCameraMask(string reason)
    {
        if (_camera is not null && _cullingMask is not null)
        {
            try
            {
                // Respect any newer mask applied by another mod. Avoid
                // undoing someone else's camera changes on toggle-off.
                var currentMask = (int)(_cullingMask.GetValue(_camera) ?? -1);
                if (currentMask == _appliedMask)
                {
                    _cullingMask.SetValue(_camera, _originalMask);
                    Runtime.Log($"UIONLY OFF ({reason}): restored original cullingMask " +
                        $"{_originalMask:X8}");
                }
                else
                    Runtime.Log($"UIONLY OFF ({reason}): camera mask changed externally; " +
                        "did not clobber it");
            }
            catch (Exception e)
            {
                Runtime.Log($"UIONLY warning: camera restoration exception " +
                    $"{e.GetType().Name}: {e.Message}");
            }
        }
        _camera = null;
        _cullingMask = null;
    }

    private object? ResolveGameCamera()
    {
        try
        {
            // Prefer Timberborn CameraService's actual camera, not the
            // thumbnail/minimap camera or an untagged Camera.main guess.
            var serviceType = AccessTools.TypeByName("Timberborn.CameraSystem.CameraService");
            var getInstance = _container.GetType().GetMethods(
                    BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetInstance" &&
                    m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (serviceType is not null && getInstance is not null)
            {
                var service = getInstance.MakeGenericMethod(serviceType).Invoke(_container, null);
                var field = AccessTools.Field(serviceType, "_camera");
                var camera = field?.GetValue(service);
                if (camera is not null) return camera;
            }
        }
        catch (Exception e)
        {
            Runtime.Log("UIONLY CameraService resolution fallback: " + e.GetType().Name);
        }
        // Camera.main is a fallback; the primary game camera is preferable.
        try
        {
            var cameraType = AccessTools.TypeByName("UnityEngine.Camera");
            return cameraType?.GetProperty("main",
                BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
        }
        catch { return null; }
    }

    private static int ReadUiLayer()
    {
        try
        {
            var layerMask = AccessTools.TypeByName("UnityEngine.LayerMask");
            var nameToLayer = layerMask?.GetMethod("NameToLayer",
                BindingFlags.Public | BindingFlags.Static,
                null, new[] { typeof(string) }, null);
            return (int)(nameToLayer?.Invoke(null, new object[] { "UI" }) ?? -1);
        }
        catch { return -1; }
    }

    private int ReadFrameCount()
    {
        try
        {
            if (_frameCountProperty is null)
            {
                var timeType = AccessTools.TypeByName("UnityEngine.Time");
                _frameCountProperty = timeType?.GetProperty("frameCount",
                    BindingFlags.Public | BindingFlags.Static);
            }
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

    // SpeedManager.CurrentSpeed is the same setting used by the game's speed UI.
    // Read only at window boundaries; never change speed or simulation ticks.
    private void ResolveSpeedManager()
    {
        try
        {
            var type = AccessTools.TypeByName("Timberborn.TimeSystem.SpeedManager")
                ?? AccessTools.TypeByName("SpeedManager");
            if (type is null) return;
            var getter = _container.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(m => m.Name == "GetInstance" &&
                    m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (getter is null) return;
            _speedManager = getter.MakeGenericMethod(type).Invoke(_container, null);
            _currentSpeed = type.GetProperty("CurrentSpeed",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Runtime.Log("UIONLY.FPS speed probe=" +
                (_speedManager is not null && _currentSpeed is not null ? "SpeedManager.CurrentSpeed" : "unavailable"));
        }
        catch (Exception e)
        {
            Runtime.Log("UIONLY.FPS speed probe unavailable: " + e.GetType().Name);
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

    private void ResetFpsWindow()
    {
        _windowStartSpeed = ReadGameSpeed();
        _previousLogTicks = Stopwatch.GetTimestamp();
        _previousFrameCount = ReadFrameCount();
        _fallbackFrameCount = 0;
    }
}
