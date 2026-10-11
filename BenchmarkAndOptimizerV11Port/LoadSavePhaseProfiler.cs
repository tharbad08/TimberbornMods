using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace BenchmarkAndOptimizerV11;

/// <summary>
/// Startup-safe, event-driven observational load/save profiler. Nothing here
/// changes a save, creates game objects, drives scene coroutines or schedules work.
/// Timers are attached to methods called during loading/saving, not Update.
/// Historical decompilation only guides method discovery; unsupported APIs
/// are logged and skipped on the running game version.
/// </summary>
internal static class LoadSavePhaseProfiler
{
    private enum Category { Load, Save }

    private sealed class Target
    {
        public readonly string Name;
        public readonly Category Kind;
        public readonly bool Begin;
        public readonly bool Request;
        public readonly bool End;
        public readonly bool Milestone;
        public readonly bool Detail;

        public Target(string name, Category kind, bool begin = false,
            bool request = false, bool end = false, bool milestone = false,
            bool detail = false)
        {
            Name = name;
            Kind = kind;
            Begin = begin;
            Request = request;
            End = end;
            Milestone = milestone;
            Detail = detail;
        }
    }

    private sealed class Metric
    {
        public int Count;
        public long Total;
        public long Max;
        public int Errors;
    }

    private sealed class Session
    {
        public readonly long Started = Stopwatch.GetTimestamp();
        public long LastMarker;
        public string LastName;
        public readonly string Name;
        public readonly Dictionary<string, Metric> Entries = new(StringComparer.Ordinal);
        public int Errors;
        public int HandlersObserved;
        public bool WriterOnly;
        public Session(string name)
        {
            Name = name;
            LastMarker = Started;
            LastName = "request";
        }
    }

    // Exact-method hooks only: never scan dynamic assemblies during startup.
    private static readonly object Sync = new();
    private static readonly Dictionary<MethodBase, Target> Targets = new();
    private static Session? _loading;
    private static Session? _saving;
    private static int _installed;
    private static int _skipped;

    public static void Patch(Harmony harmony)
    {
        // Scene request starts before asynchronous loading; the monotonic
        // time until ShowPrimaryUI includes coroutine yields and native gaps.
        Add(harmony, "Timberborn.GameSaveRepositorySystemUI.ValidatingGameLoader",
            "LoadGame", Category.Load, "ValidateLoadRequest", 1, begin: true, request: true);
        Add(harmony, "Timberborn.GameSceneLoading.GameSceneLoader",
            "StartSaveGame", Category.Load, "RequestSaveScene", 1, begin: true, request: true);
        Add(harmony, "Timberborn.GameSceneLoading.GameSceneLoader",
            "StartSaveGameInstantly", Category.Load, "RequestSaveSceneInstantly", 1, begin: true, request: true);
        Add(harmony, "Timberborn.GameScene.GameSceneSerializedWorldSupplier",
            "Load", Category.Load, "SerializedWorldSupplier.Load", 0, begin: true, milestone: true);
        Add(harmony, "Timberborn.GameSaveRuntimeSystem.GameLoader",
            "Load", Category.Load, "GameLoader.Load", 1, begin: true, milestone: true);
        Add(harmony, "Timberborn.GameSaveRepositorySystem.GameSaveDeserializer",
            "Load", Category.Load, "GameSaveDeserializer.Load", 1, milestone: true);
        Add(harmony, "Timberborn.GameSaveRepositorySystem.GameSaveRepository",
            "OpenSave", Category.Load, "OpenSaveStream", 1);
        Add(harmony, "Timberborn.WorldSerialization.WorldSerializer",
            "ReadFromSaveEntryStream", Category.Load, "WorldSerializer.ReadEntry", 1, milestone: true);
        Add(harmony, "Timberborn.WorldSerialization.WorldSerializer",
            "DeserializeSave", Category.Load, "WorldSerializer.BuildSerializedWorld", 1);
        Add(harmony, "Timberborn.SerializationSystem.SerializedObjectReaderWriter",
            "ReadJson", Category.Load, "JSON.ReadStream", 1, parameterType: typeof(System.IO.Stream));
        Add(harmony, "Timberborn.SerializationSystem.SerializedObjectReaderWriter",
            "ReadJson", Category.Load, "JSON.ParseAndBuild", 1, parameterType: typeof(string));
        Add(harmony, "Timberborn.SingletonSystem.SingletonLifecycleService",
            "UnloadAll", Category.Load, "Lifecycle.UnloadAll", 0, milestone: true);
        Add(harmony, "Timberborn.SingletonSystem.SingletonLifecycleService",
            "LoadAll", Category.Load, "Lifecycle.LoadAll", 0, begin: true, milestone: true);
        foreach (var phase in new[] { "LoadSingletons", "LoadNonSingletons",
                "PostLoadSingletons", "PostLoadNonSingletons" })
            Add(harmony, "Timberborn.SingletonSystem.SingletonLifecycleService",
                phase, Category.Load, "Lifecycle." + phase, 0, milestone: true);
        Add(harmony, "Timberborn.WorldPersistence.WorldEntitiesLoader",
            "LoadNonSingletons", Category.Load, "WorldEntities.InstantiateAndLoad", 0, milestone: true);
        Add(harmony, "Timberborn.WorldPersistence.WorldEntitiesLoader",
            "PostLoadNonSingletons", Category.Load, "WorldEntities.PostLoad", 0, milestone: true);
        Add(harmony, "Timberborn.WorldPersistence.WorldEntitiesLoader",
            "InstantiateEntities", Category.Load, "WorldEntities.Instantiate", 1);
        Add(harmony, "Timberborn.WorldPersistence.EntitiesLoader",
            "LoadAndInitialize", Category.Load, "WorldEntities.LoadAndInitialize", 1);
        Add(harmony, "Timberborn.WorldPersistence.EntitiesLoader",
            "PostLoad", Category.Load, "WorldEntities.PostLoadComponents", 1);
        Add(harmony, "Timberborn.GameStartup.GameInitializer",
            "InitializeGameFromSave", Category.Load, "InitializeFromSave", 0, milestone: true);
        Add(harmony, "Timberborn.GameStartup.GameInitializer",
            "ShowPrimaryUI", Category.Load, "ShowPrimaryUI", 0, end: true, milestone: true);

        // SceneLoader.LoadSceneCoroutine is a Unity IEnumerator. Instrument
        // MoveNext steps (including the synchronous SceneManager.LoadScene
        // call) but never attempt to move/cancel the enumerator ourselves.
        // v1.1.88: do not patch compiler-generated scene-coroutine MoveNext.
        // The request-to-Load/ShowPrimaryUI monotonic timing still includes
        // native scene load/coroutine/GC gaps without a dynamic MoveNext patch.

        // The private GameSaver.Save executes only for an actual save, unlike
        // SaveQueued / GameSaverUnityAdapter.LateUpdate, which run every frame.
        Add(harmony, "Timberborn.GameSaveRuntimeSystem.GameSaver",
            "Save", Category.Save, "GameSaver.Save", 1, begin: true, end: true, milestone: true);
        Add(harmony, "Timberborn.SaveSystem.SaveWriter",
            "WriteToSaveStream", Category.Save, "SaveWriter.ZIPArchiveAndEntries", 2,
            begin: true, milestone: true);
        Add(harmony, "Timberborn.GameSaveRepositorySystem.GameSaveRepository",
            "CreateSave", Category.Save, "Repository.CreateSaveFile", 1);
        Add(harmony, "Timberborn.GameSaveRepositorySystem.GameSaveRepository",
            "CreateSaveSkippingNameValidation", Category.Save, "Repository.CreateSaveFileUnchecked", 1);
        Add(harmony, "Timberborn.WorldPersistence.WorldEntryWriter",
            "WriteToSaveEntryStream", Category.Save, "WorldEntryWriter.GenerateAndWrite", 1);
        Add(harmony, "Timberborn.WorldPersistence.SerializedWorldFactory",
            "Create", Category.Save, "WorldFactory.CreateWorld", 0);
        Add(harmony, "Timberborn.WorldPersistence.SerializedWorldFactory",
            "SaveEntities", Category.Save, "WorldFactory.SaveEntities", 1);
        Add(harmony, "Timberborn.WorldPersistence.SerializedWorldFactory",
            "SaveSingletons", Category.Save, "WorldFactory.SaveSingletons", 1);
        Add(harmony, "Timberborn.WorldSerialization.WorldSerializer",
            "WriteToSaveEntryStream", Category.Save, "WorldSerializer.WriteEntry", 2);
        Add(harmony, "Timberborn.SerializationSystem.SerializedObjectReaderWriter",
            "WriteJson", Category.Save, "JSON.BuildAndWrite", 2, parameterType: null);

        // 1.1.88 startup crash mitigation:
        // Do not enumerate every AppDomain assembly or patch each mod's
        // ILoadableSingleton/ISaveEntryWriter implementation at startup.
        // This previously created large numbers of MonoMod dynamic-method
        // assemblies before Bindito's configurator discovery, which later
        // failed on an unfinished DMD<> type on the user's game.
        // Keep the curated coarse-grained save/load phase timers below.

        Runtime.Log($"LOADSAVE.PROBE startup-safe v1.1.88 installed={_installed} skipped={_skipped}; " +
            "curated game phase methods only; no all-assembly GetTypes, no " +
            "per-mod handler patching, no coroutine MoveNext patch; " +
            "LOAD.START/PHASE/GAP/SUMMARY and SAVE.START/PHASE/SUMMARY remain");
    }

    private static void Add(Harmony h, string typeName, string methodName,
        Category category, string label, int argCount, bool begin = false,
        bool request = false, bool end = false, bool milestone = false,
        Type? parameterType = null)
    {
        var type = AccessTools.TypeByName(typeName);
        if (type is null) { _skipped++; return; }
        var flags = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static;
        var found = type.GetMethods(flags).Where(m => m.Name == methodName &&
            !m.IsAbstract && !m.ContainsGenericParameters &&
            m.GetParameters().Length == argCount &&
            (parameterType is null ||
             (m.GetParameters().Length > 0 && m.GetParameters()[0].ParameterType == parameterType)))
            .ToArray();
        if (found.Length != 1)
        {
            _skipped++;
            Runtime.Log($"LOADSAVE.PROBE unavailable: {typeName}.{methodName}/{argCount} " +
                $"matches={found.Length}; no guessed overload");
            return;
        }
        Install(h, found[0], new Target(label, category, begin, request, end, milestone));
    }

    private static bool Install(Harmony harmony, MethodBase method, Target target)
    {
        if (Targets.ContainsKey(method)) return false;
        try
        {
            harmony.Patch(method,
                prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(LoadSavePhaseProfiler), nameof(Prefix))) { priority = Priority.First },
                finalizer: new HarmonyMethod(AccessTools.Method(
                    typeof(LoadSavePhaseProfiler), nameof(Finalizer))) { priority = Priority.Last });
            Targets.Add(method, target);
            _installed++;
            return true;
        }
        catch (Exception ex)
        {
            _skipped++;
            if (!target.Detail)
                Runtime.Log($"LOADSAVE.PROBE skip {method.DeclaringType?.FullName}.{method.Name}: " +
                    ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }

    private static void Prefix(MethodBase __originalMethod, out long __state)
    {
        __state = 0;
        if (!Targets.TryGetValue(__originalMethod, out var target)) return;

        lock (Sync)
        {
            Session? active = target.Kind == Category.Load ? _loading : _saving;
            if (target.Begin && active is null)
            {
                active = new Session(target.Kind == Category.Load ? "load" : "save");
                if (target.Kind == Category.Load)
                    _loading = active;
                else
                {
                    _saving = active;
                    active.WriterOnly = target.Name == "SaveWriter.ZIPArchiveAndEntries";
                }

                Runtime.Log((target.Kind == Category.Load ? "LOAD.START " : "SAVE.START ") +
                    $"origin={target.Name}; stopwatch started; " +
                    "timestamps measure actual elapsed wall time, including scene gaps");
            }

            if (active is null) return;

            if (target.Milestone || target.Request)
            {
                var now = Stopwatch.GetTimestamp();
                var gap = now - active.LastMarker;
                if (gap > Stopwatch.Frequency)
                    Runtime.Log((target.Kind == Category.Load ? "LOAD.GAP " : "SAVE.GAP ") +
                        $"since={active.LastName} to={target.Name} elapsed={Ms(gap):F1}ms");
                active.LastMarker = now;
                active.LastName = target.Name + " started";
                Runtime.Log((target.Kind == Category.Load ? "LOAD.BEGIN " : "SAVE.BEGIN ") +
                    target.Name + $" t+{Ms(now - active.Started):F1}ms");
            }
        }
        __state = Stopwatch.GetTimestamp();
    }

    private static Exception? Finalizer(Exception? __exception,
        MethodBase __originalMethod, long __state)
    {
        if (__state == 0 || !Targets.TryGetValue(__originalMethod, out var target))
            return __exception;

        var ended = Stopwatch.GetTimestamp();
        var elapsed = Math.Max(0, ended - __state);

        lock (Sync)
        {
            var session = target.Kind == Category.Load ? _loading : _saving;
            if (session is null) return __exception;

            if (!session.Entries.TryGetValue(target.Name, out var metric))
                session.Entries[target.Name] = metric = new Metric();

            metric.Count++;
            metric.Total += elapsed;
            metric.Max = Math.Max(metric.Max, elapsed);
            if (__exception is not null) { metric.Errors++; session.Errors++; }
            if (target.Detail) session.HandlersObserved++;

            if (target.Milestone || target.Request ||
                (!target.Detail && elapsed >= Stopwatch.Frequency / 4))
            {
                Runtime.Log((target.Kind == Category.Load ? "LOAD.PHASE " : "SAVE.PHASE ") +
                    $"{target.Name} elapsed={Ms(elapsed):F1}ms " +
                    $"t+{Ms(ended - session.Started):F1}ms" +
                    (__exception is null ? "" : " exception=" + __exception.GetType().Name));
            }
            if (target.Milestone || target.Request)
            {
                session.LastMarker = ended;
                session.LastName = target.Name + " ended";
            }

            if (target.End || (target.Kind == Category.Save && session.WriterOnly &&
                               target.Name == "SaveWriter.ZIPArchiveAndEntries"))
            {
                Complete(session, target.Kind, ended, __exception);
                if (target.Kind == Category.Load) _loading = null;
                else _saving = null;
            }
        }

        // Never swallow or replace the game's actual exception.
        return __exception;
    }

    private static void Complete(Session session, Category category,
        long now, Exception? exception)
    {
        var root = category == Category.Load ? "LOAD" : "SAVE";
        Runtime.Log($"{root}.SUMMARY elapsed={Ms(now - session.Started):F1}ms " +
            $"handlerCalls={session.HandlersObserved} distinctStages={session.Entries.Count} " +
            $"errors={session.Errors} completedBy={(category == Category.Load ? "ShowPrimaryUI" :
               (session.WriterOnly ? "SaveWriter" : "GameSaver.Save"))}" +
            (exception is null ? "" : " exception=" + exception.GetType().Name));

        // Inclusive intervals intentionally overlap. Never add all of these
        // as if they were exclusive partitions of save/load time.
        foreach (var pair in session.Entries
                     .OrderByDescending(x => x.Value.Total).Take(35))
        {
            var x = pair.Value;
            Runtime.Log($"{root}.STAGE {pair.Key} count={x.Count} " +
                $"inclusive={Ms(x.Total):F1}ms max={Ms(x.Max):F1}ms errors={x.Errors}");
        }
        Runtime.Log($"{root}.SUMMARY_END values are inclusive (nested); " +
            "ZIP stage includes entry compression/finalization, JSON stages nested; " +
            "remaining GameSaver time includes tick-finishing, file-copy/I-O, callbacks; " +
            "load request-to-first-phase gap includes native/Unity scene work");
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
