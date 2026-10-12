using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

internal static class UnusedPreviewPoolFastPath
{
    // Timberborn's PreviewPlacerFactory keeps the brush pool in Lazy<Preview[]>.
    // Vanilla HideAllPreviews always reads that Lazy even when no preview was
    // ever shown. A 899-slot brush can therefore instantiate 899 inactive
    // preview entities purely while exiting an UNUSED tool. This fast path
    // handles exactly that no-op scenario. It NEVER changes a materialized
    // pool, creation count, placement, service membership, or validation.
    //
    // Important compatibility detail: patch ONLY the vanilla method with a
    // Harmony prefix. Third-party Harmony postfixes on HideAllPreviews (e.g.
    // AutoScaffold/EzTube) still run when this prefix skips the method body.
    private static FieldInfo? _previews;
    private static PropertyInfo? _isValueCreated;
    private static FieldInfo? _shower;
    private static MethodInfo? _unhighlight;
    private static bool _enabled;
    private static int _skippedHides;
    private static int _skippedRemovals;

    public static void Patch(Harmony harmony)
    {
        var placer = AccessTools.TypeByName("Timberborn.BlockObjectTools.PreviewPlacer");
        if (placer is null)
        {
            Runtime.Log("unused preview pool optimization unavailable: PreviewPlacer type missing");
            return;
        }

        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        var previews = placer.GetField("_previews", flags);
        var shower = placer.GetField("_previewShower", flags);
        var lazyType = previews?.FieldType;
        var created = lazyType?.GetProperty("IsValueCreated", BindingFlags.Instance | BindingFlags.Public);
        var unhighlight = shower?.FieldType.GetMethod(
            "UnhighlightAllPreviews", BindingFlags.Instance | BindingFlags.Public);
        var hide = placer.GetMethod("HideAllPreviews", flags, null, Type.EmptyTypes, null);
        var remove = placer.GetMethod("RemovePreviewsFromServices", flags, null, Type.EmptyTypes, null);

        // Fail closed if 1.1 internals changed. Never substitute behavior for
        // a method whose exact preconditions cannot be inspected.
        if (previews is null || shower is null || created is null ||
            created.PropertyType != typeof(bool) ||
            unhighlight is null || unhighlight.ReturnType != typeof(void) ||
            hide is null || hide.ReturnType != typeof(void) ||
            remove is null || remove.ReturnType != typeof(void) ||
            lazyType is null || !lazyType.IsGenericType ||
            lazyType.GetGenericTypeDefinition() != typeof(Lazy<>))
        {
            Runtime.Log("unused preview pool optimization disabled: unsupported PreviewPlacer layout");
            return;
        }

        _previews = previews;
        _isValueCreated = created;
        _shower = shower;
        _unhighlight = unhighlight;

        try
        {
            // Late prefix: other mods' early prefixes can still perform their
            // normal work. Standard Harmony postfixes are not suppressed.
            harmony.Patch(hide,
                prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(UnusedPreviewPoolFastPath), nameof(HidePrefix)))
                { priority = Priority.Last });
            harmony.Patch(remove,
                prefix: new HarmonyMethod(AccessTools.Method(
                    typeof(UnusedPreviewPoolFastPath), nameof(RemovePrefix)))
                { priority = Priority.Last });
            _enabled = true;
            Runtime.Log(
                "unused preview pool fast path installed: avoid first-use allocation " +
                "of an unmaterialized brush pool while hiding/removing previews; " +
                "active previews and all placement flows remain vanilla");
        }
        catch (Exception ex)
        {
            // A prefix never changes behavior unless _enabled is true.
            _enabled = false;
            Runtime.Log("warning: unused preview pool fast path disabled: " +
                ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool HasNoMaterializedPool(object instance)
    {
        if (!_enabled || _previews is null || _isValueCreated is null)
            return false;

        try
        {
            var lazy = _previews.GetValue(instance);
            return lazy is not null && _isValueCreated.GetValue(lazy) is bool created && !created;
        }
        catch
        {
            // Fall back to vanilla on any reflection/version issue.
            return false;
        }
    }

    private static bool RemovePrefix(object __instance)
    {
        if (!HasNoMaterializedPool(__instance))
            return true;

        _skippedRemovals++;
        return false; // No preview was constructed, so no preview service was joined.
    }

    private static bool HidePrefix(object __instance)
    {
        if (!HasNoMaterializedPool(__instance) || _shower is null || _unhighlight is null)
            return true;

        try
        {
            var shower = _shower.GetValue(__instance);
            if (shower is null)
                return true;

            // Original HideAllPreviews calls HidePreviews, whose first action
            // is UnhighlightAllPreviews. Keep that global UI side effect.
            _unhighlight.Invoke(shower, null);
            _skippedHides++;
            if (_skippedHides == 1 || _skippedHides % 100 == 0)
                Runtime.Log(
                    $"unused preview pool: avoided {_skippedHides} first-use hide(s), " +
                    $"{_skippedRemovals} first-use service removal(s) " +
                    "(no preview objects created or skipped during actual placement)");
            return false;
        }
        catch (Exception ex)
        {
            Runtime.Log("warning: preview fast-path highlight reset failed; using vanilla: " +
                ex.GetType().Name + ": " + ex.Message);
            return true;
        }
    }
}
