using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

internal static class FullyBuildablePreviewFastPath
{
    // Timberborn uses Previews.Except(buildablePreviews) on EVERY mixed brush
    // refresh, constructing LINQ/HashSet state for up to 899 previews. Its
    // buildable list is a unique ordered subset of the already-positioned pool.
    // Verify this invariant before doing anything, then perform an ordered
    // zero-allocation sweep and invoke the EXACT vanilla hide/removal helper
    // for each invalid preview. Mod callbacks on ShowPreviews remain in place.
    private static FieldInfo? _pool;
    private static PropertyInfo? _value;
    private static Action<object>? _hideAndRemove;
    private static bool _enabled;
    private static int _noOp;
    private static int _mixedSweeps;

    public static void Patch(Harmony harmony)
    {
        var type = AccessTools.TypeByName("Timberborn.BlockObjectTools.PreviewPlacer");
        if (type is null) return;
        var method = AccessTools.Method(type, "HideRemainingPreviews");
        var pool = AccessTools.Field(type, "_previews");
        var args = method?.GetParameters();
        var preview = AccessTools.TypeByName("Timberborn.BlockSystem.Preview");
        var hide = AccessTools.Method(type, "HideAndRemoveFromServices");
        if (method is null || method.ReturnType != typeof(void) ||
            args is null || args.Length != 2 ||
            args[1].ParameterType != typeof(bool) ||
            pool is null || !pool.FieldType.IsGenericType ||
            pool.FieldType.GetGenericTypeDefinition() != typeof(Lazy<>) ||
            preview is null || hide is null ||
            hide.GetParameters().Length != 1 ||
            hide.GetParameters()[0].ParameterType != preview ||
            hide.ReturnType != typeof(void))
        {
            Runtime.Log("preview ordered-sweep fast path disabled: method layout changed");
            return;
        }
        var value = pool.FieldType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
        if (value is null || value.PropertyType != preview.MakeArrayType())
            return;
        var info = Harmony.GetPatchInfo(method);
        if (info is not null &&
            info.Owners.Any(owner => owner != harmony.Id))
        {
            Runtime.Log("preview ordered-sweep fast path disabled: externally patched inner method");
            return;
        }
        try
        {
            var p = Expression.Parameter(typeof(object), "preview");
            _hideAndRemove = Expression.Lambda<Action<object>>(
                Expression.Call(hide, Expression.Convert(p, preview)), p).Compile();
            _pool = pool;
            _value = value;
            harmony.Patch(method, prefix: new HarmonyMethod(AccessTools.Method(
                typeof(FullyBuildablePreviewFastPath), nameof(Prefix)))
            { priority = Priority.Last });
            _enabled = true;
            Runtime.Log(
                "preview ordered-sweep fast path installed: zero-allocation replacement " +
                "for repeated remaining-preview LINQ/Except; preserves vanilla hide calls " +
                "and all preview validators and AutoScaffold/EzTube hooks");
        }
        catch (Exception ex)
        {
            _enabled = false;
            Runtime.Log("preview ordered-sweep fast path disabled: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool Prefix(object __instance, object __0, bool __1)
    {
        if (!_enabled || _pool is null || _value is null ||
            _hideAndRemove is null || __0 is not System.Collections.IList valid)
            return true;
        try
        {
            if (_value.GetValue(_pool.GetValue(__instance)) is not object[] previews)
                return true;
            // A valid buildable list is exactly an ordered pool subset.
            // If another mod changes the list, fail closed to vanilla.
            int found = 0;
            for (int i = 0; i < previews.Length && found < valid.Count; i++)
                if (ReferenceEquals(previews[i], valid[found]))
                    found++;
            if (found != valid.Count)
                return true;

            if (valid.Count == previews.Length)
            {
                _noOp++;
                if (_noOp == 1)
                    Runtime.Log("preview ordered-sweep: all-buildable no-op traversal skipped");
                return false;
            }

            int current = 0;
            for (int i = 0; i < previews.Length; i++)
            {
                var p = previews[i];
                if (current < valid.Count && ReferenceEquals(p, valid[current]))
                {
                    current++;
                    continue;
                }
                if (i == 0 && __1)
                    continue;
                _hideAndRemove(p);
            }
            _mixedSweeps++;
            if (_mixedSweeps == 1000)
                Runtime.Log("preview ordered-sweep: avoided 1000 repeated mixed-brush LINQ/HashSet allocations");
            return false;
        }
        catch (Exception ex)
        {
            // Do not replay vanilla after partial hiding: each hide performs
            // callbacks and repeating them may be unsafe. Warn, then keep
            // subsequent calls vanilla. The already-processed previews remain
            // consistently hidden using their original helper.
            _enabled = false;
            Runtime.Log("warning: preview ordered-sweep disabled after error: " +
                ex.GetType().Name + ": " + ex.Message);
            return false;
        }
    }
}
