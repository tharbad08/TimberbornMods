using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

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
