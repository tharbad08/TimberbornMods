using System.Collections;
using System.Reflection;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using HarmonyLib;
using Timberborn.ModManagerScene;

namespace BenchmarkAndOptimizerV11;

internal static class KeystoneFaunaTemplatePrewarmer
{
    private static readonly string[] TargetBlueprintNames =
    {
        "KeystoneCow",
        "KeystoneBull",
        "KeystoneDeer",
    };

    private sealed class State
    {
        public bool InProgress;
        public bool Done;
    }

    private static readonly ConditionalWeakTable<object, State> States = new();
    private static WeakReference<object>? _templateInstantiator;
    private static WeakReference<object>? _templateCollectionService;
    private static bool _installed;

    public static void Patch(Harmony harmony)
    {
        if (_installed)
        {
            return;
        }

        try
        {
            var instantiatorType =
                AccessTools.TypeByName("Timberborn.TemplateInstantiation.TemplateInstantiator");
            var collectionType =
                AccessTools.TypeByName("Timberborn.TemplateCollectionSystem.TemplateCollectionService");

            if (instantiatorType is null || collectionType is null)
            {
                Runtime.Log(
                    "Keystone fauna template prewarm not installed: " +
                    "TemplateInstantiator/TemplateCollectionService unavailable");
                return;
            }

            foreach (var constructor in instantiatorType.GetConstructors(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                harmony.Patch(
                    constructor,
                    postfix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(KeystoneFaunaTemplatePrewarmer),
                            nameof(TemplateInstantiatorConstructed)))
                    {
                        priority = Priority.Last
                    });
            }

            var load = AccessTools.Method(collectionType, "Load", Type.EmptyTypes);
            var gameInitializerType = AccessTools.TypeByName("Timberborn.GameStartup.GameInitializer");
            var showPrimaryUi = gameInitializerType is null
                ? null
                : AccessTools.DeclaredMethod(gameInitializerType, "ShowPrimaryUI", Type.EmptyTypes);

            if (load is null || showPrimaryUi is null)
            {
                Runtime.Log(
                    "Keystone fauna template prewarm not installed: " +
                    "TemplateCollectionService.Load/GameInitializer.ShowPrimaryUI unavailable");
                return;
            }

            harmony.Patch(
                load,
                postfix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(KeystoneFaunaTemplatePrewarmer),
                        nameof(TemplateCollectionLoaded)))
                {
                    priority = Priority.Last
                });

            harmony.Patch(
                showPrimaryUi,
                prefix: new HarmonyMethod(
                    AccessTools.Method(
                        typeof(KeystoneFaunaTemplatePrewarmer),
                        nameof(BeforePrimaryUi)))
                {
                    priority = Priority.First
                });

            _installed = true;
            Runtime.Log(
                "Keystone fauna template prewarm installed: " +
                "Cow/Bull/Deer are deferred until GameInitializer.ShowPrimaryUI, " +
                "then cached through vanilla TemplateInstantiator.CacheInstance");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: Keystone fauna template prewarm installation failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TemplateInstantiatorConstructed(object __instance)
    {
        if (__instance is null)
        {
            return;
        }

        _templateInstantiator = new WeakReference<object>(__instance);
    }

    private static void TemplateCollectionLoaded(object __instance)
    {
        if (__instance is null)
        {
            return;
        }

        _templateCollectionService = new WeakReference<object>(__instance);
    }

    private static void BeforePrimaryUi()
    {
        Runtime.Log(
            "Keystone fauna template prewarm starting at end of game initialization, before primary UI");
        TryPrewarm();

    }

    private static void TryPrewarm()
    {
        if (_templateInstantiator is null ||
            !_templateInstantiator.TryGetTarget(out var instantiator) ||
            _templateCollectionService is null ||
            !_templateCollectionService.TryGetTarget(out var collectionService))
        {
            return;
        }

        var state = States.GetOrCreateValue(instantiator);
        if (state.Done || state.InProgress)
        {
            return;
        }

        state.InProgress = true;
        try
        {
            var allTemplatesProperty =
                AccessTools.Property(collectionService.GetType(), "AllTemplates");
            var allTemplates = allTemplatesProperty?.GetValue(collectionService) as IEnumerable;
            if (allTemplates is null)
            {
                return;
            }

            var byName = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var blueprint in allTemplates)
            {
                if (blueprint is null)
                {
                    continue;
                }

                var blueprintType = blueprint.GetType();
                var nameProperty = AccessTools.Property(blueprintType, "Name");
                var nameField = AccessTools.Field(blueprintType, "Name");
                var name =
                    nameProperty?.GetValue(blueprint) as string ??
                    nameField?.GetValue(blueprint) as string;

                if (!string.IsNullOrEmpty(name) &&
                    TargetBlueprintNames.Contains(name))
                {
                    byName[name] = blueprint;
                }
            }

            var cacheInstance = instantiator.GetType()
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method =>
                    method.Name == "CacheInstance" &&
                    method.GetParameters().Length == 1);

            if (cacheInstance is null)
            {
                Runtime.Log(
                    "warning: Keystone fauna template prewarm skipped: " +
                    "TemplateInstantiator.CacheInstance unavailable");
                state.Done = true;
                return;
            }

            var warmed = 0;
            foreach (var targetName in TargetBlueprintNames)
            {
                if (!byName.TryGetValue(targetName, out var blueprint))
                {
                    Runtime.Log(
                        $"warning: Keystone fauna template prewarm could not find '{targetName}' " +
                        "in TemplateCollectionService.AllTemplates");
                    continue;
                }

                var started = System.Diagnostics.Stopwatch.GetTimestamp();

                cacheInstance.Invoke(instantiator, new[] { blueprint });

                var elapsedMs =
                    (System.Diagnostics.Stopwatch.GetTimestamp() - started) *
                    1000.0 / System.Diagnostics.Stopwatch.Frequency;

                warmed++;
                Runtime.Log(
                    $"Keystone fauna template prewarmed: {targetName}; " +
                    $"elapsed={elapsedMs:F1}ms");
            }

            state.Done = true;
            Runtime.Log(
                $"Keystone fauna template prewarm complete: {warmed}/{TargetBlueprintNames.Length}; " +
                "uses vanilla template cache, so no duplicate retained prefab cache is created");
        }
        catch (TargetInvocationException ex)
        {
            Runtime.Log(
                $"warning: Keystone fauna template prewarm failed at deferred game-init stage; " +
                $"cache remains retryable: " +
                $"{ex.InnerException?.GetType().Name ?? ex.GetType().Name}: " +
                $"{ex.InnerException?.Message ?? ex.Message}");
        }
        catch (Exception ex)
        {
            Runtime.Log(
                $"warning: Keystone fauna template prewarm failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            state.InProgress = false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / 1024.0 / 1024.0:F1}MB";
        }

        if (bytes >= 1024L)
        {
            return $"{bytes / 1024.0:F1}KB";
        }

        return $"{bytes}B";
    }
}
