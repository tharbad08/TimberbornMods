using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.ModManagerScene;

namespace LeveeBlueprintSourceTracer;

public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        try { Tracer.Install(); }
        catch (Exception ex) { Log.Write("[LEVEE-SOURCE] Failed to install: " + ex); }
    }
}

internal static class Tracer
{
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static Type BlueprintSourceServiceType = null!;
    static Type TemplateSpecType = null!;
    static Type BlockObjectSpecType = null!;

    public static void Install()
    {
        BlueprintSourceServiceType = Type.GetType(
            "Timberborn.BlueprintSystem.BlueprintSourceService, Timberborn.BlueprintSystem",
            throwOnError: true)!;
        TemplateSpecType = Type.GetType(
            "Timberborn.TemplateSystem.TemplateSpec, Timberborn.TemplateSystem",
            throwOnError: true)!;
        BlockObjectSpecType = Type.GetType(
            "Timberborn.BlockSystem.BlockObjectSpec, Timberborn.BlockSystem",
            throwOnError: true)!;

        MethodInfo add = BlueprintSourceServiceType.GetMethods(AnyInstance)
            .Single(m => m.Name == "Add" && m.GetParameters().Length == 2);

        HarmonyBridge.PatchPostfix(
            add,
            typeof(Tracer).GetMethod(nameof(AfterBlueprintSourceAdded), AnyStatic)!);

        Log.Write("[LEVEE-SOURCE] v0.1.0 installed. Waiting for Levee blueprint deserialization.");
    }

    public static void AfterBlueprintSourceAdded(object __0, object __1)
    {
        try
        {
            object blueprint = __0;
            object bundle = __1;

            object? templateSpec = GetSpec(blueprint, TemplateSpecType);
            if (templateSpec == null)
                return;

            string templateName = SafeValue(templateSpec, "TemplateName");
            if (templateName != "Levee.Folktails" && templateName != "Levee.IronTeeth")
                return;

            Log.Write("[LEVEE-SOURCE] ===== " + templateName + " =====");
            Log.Write("[LEVEE-SOURCE] BlueprintPath=" + SafeValue(bundle, "Path"));
            Log.Write("[LEVEE-SOURCE] BlueprintName=" + SafeValue(bundle, "Name"));

            var sources = Enumerate(GetMember(bundle, "Sources")).Cast<object>()
                .Select(x => Convert.ToString(x) ?? "<null>").ToList();
            var jsons = Enumerate(GetMember(bundle, "Jsons")).Cast<object>()
                .Select(x => Convert.ToString(x) ?? "").ToList();

            Log.Write("[LEVEE-SOURCE] SourcesCount=" + sources.Count);
            for (int i = 0; i < sources.Count; i++)
            {
                string json = i < jsons.Count ? jsons[i] : "";
                Log.Write("[LEVEE-SOURCE] SOURCE[" + i + "]=" + sources[i]
                    + " jsonLength=" + json.Length
                    + " | " + ExtractHints(json));
            }

            object? blockObjectSpec = GetSpec(blueprint, BlockObjectSpecType);
            if (blockObjectSpec == null)
            {
                Log.Write("[LEVEE-SOURCE] Final BlockObjectSpec=<missing>");
            }
            else
            {
                Log.Write("[LEVEE-SOURCE] Final BlockObjectSpec Size=" + SafeValue(blockObjectSpec, "Size")
                    + " BaseZ=" + SafeValue(blockObjectSpec, "BaseZ")
                    + " Overridable=" + SafeValue(blockObjectSpec, "Overridable"));

                int n = 0;
                foreach (object block in Enumerate(GetMember(blockObjectSpec, "Blocks")))
                {
                    Log.Write("[LEVEE-SOURCE] FINAL-BLOCK[" + n + "]"
                        + " MatterBelow=" + SafeValue(block, "MatterBelow")
                        + " Stackable=" + SafeValue(block, "Stackable")
                        + " Occupations=" + SafeValue(block, "Occupations")
                        + " OccupyAllBelow=" + SafeValue(block, "OccupyAllBelow")
                        + " Underground=" + SafeValue(block, "Underground"));
                    n++;
                }
            }

            Log.Write("[LEVEE-SOURCE] ===== END " + templateName + " =====");
        }
        catch (Exception ex)
        {
            Log.Write("[LEVEE-SOURCE] Trace error: " + ex);
        }
    }

    static string ExtractHints(string json)
    {
        if (string.IsNullOrEmpty(json))
            return "empty";

        var hints = new List<string>();
        foreach (string token in new[]
        {
            "Levee.Folktails",
            "Levee.IronTeeth",
            "BlockObjectSpec",
            "\"MatterBelow\": \"Any\"",
            "\"MatterBelow\": \"GroundOrStackable\"",
            "\"Stackable\": \"UnfinishedGround\"",
            "\"Stackable\": \"BlockObject\"",
            "ReplatformableSpec",
            "SidePlatformSupportBlocker"
        })
        {
            if (json.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                hints.Add(token.Replace("\"", ""));
        }

        string compact = json.Replace("\r", " ").Replace("\n", " ");
        while (compact.Contains("  ")) compact = compact.Replace("  ", " ");
        if (compact.Length > 700) compact = compact.Substring(0, 700) + "...";

        return "hints=[" + string.Join(", ", hints) + "] jsonPreview=" + compact;
    }

    static object? GetSpec(object blueprint, Type specType)
    {
        MethodInfo? m = blueprint.GetType().GetMethod(
            "GetSpec",
            AnyInstance,
            null,
            new[] { typeof(Type) },
            null);
        if (m == null)
            throw new MissingMethodException(blueprint.GetType().FullName, "GetSpec(Type)");
        return m.Invoke(blueprint, new object[] { specType });
    }

    static object GetMember(object instance, string name)
    {
        Type? t = instance.GetType();
        while (t != null)
        {
            PropertyInfo? p = t.GetProperty(name, AnyInstance);
            if (p != null) return p.GetValue(instance)!;
            FieldInfo? f = t.GetField(name, AnyInstance);
            if (f != null) return f.GetValue(instance)!;
            t = t.BaseType;
        }
        throw new MissingMemberException(instance.GetType().FullName, name);
    }

    static string SafeValue(object instance, string name)
    {
        try { return GetMember(instance, name)?.ToString() ?? "null"; }
        catch (Exception ex) { return "<error:" + ex.GetType().Name + ">"; }
    }

    static IEnumerable Enumerate(object value)
    {
        if (value is IEnumerable e) return e;
        throw new InvalidOperationException(value.GetType().FullName + " is not enumerable");
    }
}

internal static class HarmonyBridge
{
    static readonly Type HarmonyType =
        Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: true)!;
    static readonly Type HarmonyMethodType =
        Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", throwOnError: true)!;

    public static void PatchPostfix(MethodBase original, MethodInfo patchMethodInfo)
    {
        object harmony = Activator.CreateInstance(
            HarmonyType, "LeveeBlueprintSourceTracer")!;
        object hm = Activator.CreateInstance(HarmonyMethodType, patchMethodInfo)!;

        MethodInfo patch = HarmonyType.GetMethods(
            BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "Patch")
            .First(m =>
            {
                ParameterInfo[] p = m.GetParameters();
                return p.Length >= 3
                    && typeof(MethodBase).IsAssignableFrom(p[0].ParameterType);
            });

        object?[] args = new object?[patch.GetParameters().Length];
        args[0] = original;
        args[2] = hm;
        patch.Invoke(harmony, args);
    }
}

internal static class Log
{
    static readonly MethodInfo? UnityLog =
        Type.GetType("UnityEngine.Debug, UnityEngine.CoreModule", throwOnError: false)?
            .GetMethod(
                "Log",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(object) },
                null);

    public static void Write(string message)
    {
        try
        {
            if (UnityLog != null) UnityLog.Invoke(null, new object?[] { message });
            else Console.WriteLine(message);
        }
        catch { Console.WriteLine(message); }
    }
}
