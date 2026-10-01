using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        Runtime.Initialize(modEnvironment.ModPath);

        var harmony = new Harmony("shay.BenchmarkAndOptimizerV11");
        DispatcherPatcher.Patch(harmony);

        Runtime.Log($"loaded; config={Runtime.ConfigPath}");
    }
}

internal static class DispatcherPatcher
{
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
            Runtime.Log($"patched {target.TypeName}.{target.MethodName}");
        }
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

internal static class Runtime
{
    private static readonly object Sync = new();
    private static readonly ConditionalWeakTable<object, CounterState> Counters = new();
    private static readonly Dictionary<string, ItemAdapter> AdapterCache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> UnsupportedAdapters = new(StringComparer.Ordinal);
    private static readonly HashSet<string> LoggedWarnings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, BenchStat> BenchStats = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Discovered = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> OriginByType = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> OriginByAssemblyPath = new(StringComparer.OrdinalIgnoreCase);

    private static Settings _settings = new();
    private static DateTime _nextReloadUtc = DateTime.MinValue;
    private static DateTime _configWriteUtc = DateTime.MinValue;
    private static long _benchmarkStart;
    private static bool _benchmarkCompleted;

    public static string ModPath { get; private set; } = "";
    public static string ConfigPath => Path.Combine(ModPath, "optimizer-v11.json");
    private static string DiscoveredPath => Path.Combine(ModPath, "optimizer-v11-discovered.txt");

    public const int BenchmarkDurationSeconds = 120;

    public static bool Enabled => _settings.Enabled;
    public static int DefaultInterval => _settings.DefaultInterval;
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

    public static bool IsBenchmarking => BenchmarkActive;
    public static double BenchmarkRemainingSeconds
    {
        get
        {
            if (!BenchmarkActive) return 0;
            var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - _benchmarkStart)
                          / (double)System.Diagnostics.Stopwatch.Frequency;
            return Math.Max(0, _settings.BenchmarkSeconds - elapsed);
        }
    }

    public static void SetEnabled(bool value)
    {
        _settings.Enabled = value;
        SaveSettings(_settings);
    }

    public static void SetDefaultInterval(int value)
    {
        _settings.DefaultInterval = Math.Clamp(value, 1, 1000);
        SaveSettings(_settings);
    }

    public static int GetInterval(string typeName)
    {
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
        value = Math.Clamp(value, 1, 1000);
        var key = Discovered.FirstOrDefault(x => ShortTypeName(x) == typeName) ?? typeName;
        _settings.Intervals[key] = value;
        SaveSettings(_settings);
    }

    public static void ResetIntervals()
    {
        _settings.DefaultInterval = 1;
        _settings.Intervals.Clear();
        SaveSettings(_settings);
        Log("all optimizer intervals reset to vanilla (1)");
    }

    public static void StartBenchmark()
    {
        lock (Sync)
        {
            BenchStats.Clear();
        }

        _settings.BenchmarkSeconds = BenchmarkDurationSeconds;
        _benchmarkStart = System.Diagnostics.Stopwatch.GetTimestamp();
        _benchmarkCompleted = false;
        Log($"benchmark started for {BenchmarkDurationSeconds}s; output will be written under {ModPath}");
    }

    private static string ShortTypeName(string value)
    {
        var i = value.LastIndexOf('.');
        return i < 0 ? value : value[(i + 1)..];
    }

    public static void Initialize(string modPath)
    {
        ModPath = modPath;
        Directory.CreateDirectory(ModPath);
        RotateOversizedLog();

        if (!File.Exists(ConfigPath))
        {
            _settings = Settings.CreateDefault();
            SaveSettings(_settings);
        }

        ReloadConfig(force: true);
        _benchmarkStart = System.Diagnostics.Stopwatch.GetTimestamp();
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

        // Zero-cost normal mode: when all intervals are vanilla (1), do not replace
        // Timberborn's dispatcher at all. Reflection dispatch is used only while an
        // actual throttle is configured or while benchmarking.
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

            if (list.Count == 0)
            {
                FinishBenchmarkIfNeeded();
                return false;
            }

            // Resolve reflection metadata before invoking anything. If 1.1.x changes
            // the wrapper layout, we fall back to vanilla without double-running items.
            var adapters = new ItemAdapter?[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                if (item is null)
                {
                    continue;
                }

                adapters[i] = GetAdapter(item.GetType(), actualFieldName, invokeMethodName, enabledMemberName);
                if (adapters[i] is null)
                {
                    LogOnce(
                        $"unsupported:{item.GetType().FullName}:{listFieldName}",
                        $"warning: cannot adapt {item.GetType().FullName} in {listFieldName}; falling back to vanilla");
                    return true;
                }
            }

            for (var i = 0; i < list.Count; i++)
            {
                var item = list[i];
                var adapter = adapters[i];
                if (item is null || adapter is null)
                {
                    continue;
                }

                var actual = adapter.GetActual(item);
                if (actual is null)
                {
                    continue;
                }

                Discover(actual.GetType());

                if (!adapter.IsEnabled(item))
                {
                    continue;
                }

                if (!ShouldRun(actual))
                {
                    continue;
                }

                if (BenchmarkActive)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    Invoke(adapter.Method, item);
                    RecordBenchmark(actual.GetType(), System.Diagnostics.Stopwatch.GetTimestamp() - started);
                }
                else
                {
                    Invoke(adapter.Method, item);
                }
            }

            FinishBenchmarkIfNeeded();
            return false;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Preserve the game's original exception rather than hiding failures inside
            // gameplay code. This is not a compatibility/reflection failure.
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

    private static void Invoke(MethodInfo method, object target)
    {
        method.Invoke(target, null);
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

            // Timberborn 1.1 renamed MeteredTickableComponent.StartAndTick() to Tick().
            // Prefer the old name for compatibility, then use the 1.1 name.
            if (method is null && methodName == "StartAndTick")
            {
                method = AccessTools.Method(itemType, "Tick", Type.EmptyTypes);
            }

            if (method is null)
            {
                UnsupportedAdapters.Add(cacheKey);
                return null;
            }

            Func<object, bool> enabled = _ => true;
            if (!string.IsNullOrEmpty(enabledMemberName))
            {
                var prop = AccessTools.Property(itemType, enabledMemberName);
                var field = AccessTools.Field(itemType, enabledMemberName);

                if (prop?.PropertyType == typeof(bool) && prop.GetMethod is not null)
                {
                    enabled = item => (bool)(prop.GetValue(item) ?? true);
                }
                else if (field?.FieldType == typeof(bool))
                {
                    enabled = item => (bool)(field.GetValue(item) ?? true);
                }
                // If 1.1's wrapper no longer exposes Enabled, let its own Tick()
                // perform that check internally instead of disabling the entire path.
            }

            var adapter = new ItemAdapter(actualFieldName, enabledMemberName, actualField, method, enabled);
            AdapterCache[cacheKey] = adapter;
            return adapter;
        }
    }

    private static bool ShouldRun(object actual)
    {
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
        var origin = ResolveOrigin(type);

        lock (Sync)
        {
            OriginByType[name] = origin;

            if (!Discovered.Add(name))
            {
                return;
            }

            if (_settings.WriteDiscoveredTypes)
            {
                File.AppendAllText(
                    DiscoveredPath,
                    $"{name}\t{origin}{Environment.NewLine}");
            }
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
            return "UNKNOWN";
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
        !_benchmarkCompleted && _settings.BenchmarkSeconds > 0;

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

    private static void FinishBenchmarkIfNeeded()
    {
        if (!BenchmarkActive)
        {
            return;
        }

        var elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - _benchmarkStart)
                      / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsed < _settings.BenchmarkSeconds)
        {
            return;
        }

        _benchmarkCompleted = true;

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
            _settings = loaded;
            _configWriteUtc = writeUtc;
            Log($"config reloaded: {_settings.Intervals.Count} explicit interval(s)");
        }
        catch (Exception ex)
        {
            Log($"warning: config reload failed: {ex.GetType().Name}: {ex.Message}; keeping previous settings");
        }
    }

    private static void SaveSettings(Settings settings)
    {
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
        FieldInfo? ActualField,
        MethodInfo Method,
        Func<object, bool> Enabled)
    {
        public object? GetActual(object item) => ActualField?.GetValue(item) ?? item;
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
        BenchmarkSeconds = Math.Clamp(BenchmarkSeconds, 0, 3600);
        Intervals ??= new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var key in Intervals.Keys.ToArray())
        {
            Intervals[key] = Math.Clamp(Intervals[key], 1, 1000);
        }
    }
}
