using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

internal static class PreviewNavMeshBatcher
{
    private static Action<object>? _enqueueAdd;
    private static Action<object>? _enqueueRemove;
    private static bool _installed;

    public static void Patch(Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        var type = AccessTools.TypeByName("Timberborn.Navigation.NavMeshObject");
        var add = type is null ? null : AccessTools.Method(type, "AddToPreviewNavMesh", Type.EmptyTypes);
        var remove = type is null ? null : AccessTools.Method(type, "RemoveFromPreviewNavMesh", Type.EmptyTypes);
        var enqueueAdd = type is null ? null : AccessTools.Method(type, "EnqueueAddToPreviewNavMesh", Type.EmptyTypes);
        var enqueueRemove = type is null ? null : AccessTools.Method(type, "EnqueueRemoveFromPreviewNavMesh", Type.EmptyTypes);

        if (type is null || add is null || remove is null || enqueueAdd is null || enqueueRemove is null)
        {
            Runtime.Log(
                "warning: preview-navmesh batching unavailable: NavMeshObject immediate/enqueue methods not resolved");
            return;
        }

        _enqueueAdd = CompileInvoker(type, enqueueAdd);
        _enqueueRemove = CompileInvoker(type, enqueueRemove);

        harmony.Patch(
            add,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(PreviewNavMeshBatcher), nameof(AddPrefix)))
            {
                priority = Priority.First
            });
        harmony.Patch(
            remove,
            prefix: new HarmonyMethod(
                AccessTools.Method(typeof(PreviewNavMeshBatcher), nameof(RemovePrefix)))
            {
                priority = Priority.First
            });

        _installed = true;
        Runtime.Log(
            "preview-navmesh batching installed: BlockObject preview navmesh add/remove now enqueue changes; " +
            "NavigationSynchronizer applies and notifies the combined preview update once instead of once per preview");
    }

    private static bool AddPrefix(object __instance)
    {
        _enqueueAdd!(__instance);
        return false;
    }

    private static bool RemovePrefix(object __instance)
    {
        _enqueueRemove!(__instance);
        return false;
    }

    private static Action<object> CompileInvoker(Type type, MethodInfo method)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var call = Expression.Call(Expression.Convert(instance, type), method);
        return Expression.Lambda<Action<object>>(call, instance).Compile();
    }
}
