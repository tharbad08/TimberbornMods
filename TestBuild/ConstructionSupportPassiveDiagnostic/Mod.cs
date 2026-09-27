using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.ModManagerScene;

namespace ConstructionSupportPassiveDiagnostic;

public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        try { Diagnostic.Install(); }
        catch (Exception ex) { Log.Write("[LEVEE-CMP] Failed to install: " + ex); }
    }
}

internal static class Diagnostic
{
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    const string VanillaLevee = "Levee.Folktails";
    const string SquareLevee = "DD_SquareW1x1.Folktails";

    static Type ConstructionSiteType = null!;
    static Type GroundedConstructionSiteType = null!;
    static Type MatterBelowValidatorType = null!;
    static Type ConstructionSiteAccessibleType = null!;

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long lastPollMs;
    static bool dumpedFinalPatchMap;

    static readonly HashSet<object> ConstructionSites = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<int, string> LastPollSignature = new();
    static readonly Dictionary<int, int> LastProgressBucket = new();
    static readonly HashSet<int> LoggedBuildStacks = new();

    public static void Install()
    {
        ConstructionSiteType = FindType("Timberborn.ConstructionSites.ConstructionSite, Timberborn.ConstructionSites");
        GroundedConstructionSiteType = FindType("Timberborn.ConstructionSites.GroundedConstructionSite, Timberborn.ConstructionSites");
        MatterBelowValidatorType = FindType("Timberborn.BlockSystem.MatterBelowValidator, Timberborn.BlockSystem");
        ConstructionSiteAccessibleType = FindType("Timberborn.BuildingsNavigation.ConstructionSiteAccessible, Timberborn.BuildingsNavigation");

        var registryType = FindType("Timberborn.EntitySystem.EntityComponentRegistry, Timberborn.EntitySystem");
        var entityComponentType = FindType("Timberborn.EntitySystem.EntityComponent, Timberborn.EntitySystem");

        var register = registryType.GetMethod("Register", AnyInstance, null, new[] { entityComponentType }, null)
            ?? throw new MissingMethodException(registryType.FullName, "Register(EntityComponent)");
        var unregister = registryType.GetMethod("Unregister", AnyInstance, null, new[] { entityComponentType }, null)
            ?? throw new MissingMethodException(registryType.FullName, "Unregister(EntityComponent)");

        HarmonyBridge.PatchPostfix(register, typeof(Diagnostic).GetMethod(nameof(AfterEntityRegistered), AnyStatic)!);
        HarmonyBridge.PatchPostfix(unregister, typeof(Diagnostic).GetMethod(nameof(AfterEntityUnregistered), AnyStatic)!);

        var tickServiceType = FindType("Timberborn.TickSystem.TickableSingletonService, Timberborn.TickSystem");
        var tick = tickServiceType.GetMethod("TickAll", AnyInstance, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(tickServiceType.FullName, "TickAll");
        HarmonyBridge.PatchPostfix(tick, typeof(Diagnostic).GetMethod(nameof(AfterWorldTick), AnyStatic)!);

        var increase = ConstructionSiteType.GetMethod("IncreaseBuildTime", AnyInstance, null, new[] { typeof(float) }, null)
            ?? throw new MissingMethodException(ConstructionSiteType.FullName, "IncreaseBuildTime(float)");
        HarmonyBridge.PatchPrefix(increase, typeof(Diagnostic).GetMethod(nameof(BeforeIncreaseBuildTime), AnyStatic)!);

        Log.Write("[LEVEE-CMP] v0.8 installed.");
        Log.Write("[LEVEE-CMP] Comparing runtime templates: " + VanillaLevee + " vs " + SquareLevee);
        Log.Write("[LEVEE-CMP] Only construction observer hook: ConstructionSite.IncreaseBuildTime.");
        Log.Write("[LEVEE-CMP] BlockObject.MarkAsFinished is NOT patched.");
        DumpConstructionPatchOwners("startup");
    }

    public static void AfterEntityRegistered(object __0) => TrackEntity(__0, true);
    public static void AfterEntityUnregistered(object __0) => TrackEntity(__0, false);

    static void TrackEntity(object entityComponent, bool add)
    {
        try
        {
            object registered = GetMember(entityComponent, "RegisteredComponents");
            foreach (object component in Enumerate(registered))
            {
                if (!ConstructionSiteType.IsInstanceOfType(component))
                    continue;

                if (add) ConstructionSites.Add(component);
                else ConstructionSites.Remove(component);
            }
        }
        catch (Exception ex)
        {
            Log.Write("[LEVEE-CMP] Registry tracking error: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static void AfterWorldTick()
    {
        long now = Clock.ElapsedMilliseconds;
        if (now - lastPollMs < 1000)
            return;
        lastPollMs = now;

        if (!dumpedFinalPatchMap)
        {
            dumpedFinalPatchMap = true;
            DumpConstructionPatchOwners("after world/mod initialization");
            Log.Write("[LEVEE-CMP] Tracked ConstructionSite count=" + ConstructionSites.Count);
        }

        foreach (object site in ConstructionSites.ToArray())
        {
            object upper;
            try { upper = GetField(site, "_blockObject"); }
            catch { continue; }

            string? kind = TargetKind(upper);
            if (kind == null)
                continue;

            int id = GetObjectId(site);
            string sig = SafeValue(site, "IsOn") + "|" +
                         SafeValue(site, "ReadyToBuild") + "|" +
                         SafeValue(site, "BuildTimeProgress") + "|" +
                         SafeValue(upper, "IsFinished") + "|" +
                         SafeValue(upper, "IsUnfinished");

            if (LastPollSignature.TryGetValue(id, out string? previous) && previous == sig)
                continue;

            LastPollSignature[id] = sig;
            DumpTarget(site, upper, kind, "STATE-CHANGE", includeStack: false);
        }
    }

    public static void BeforeIncreaseBuildTime(object __instance, float __0)
    {
        try
        {
            object upper = GetField(__instance, "_blockObject");
            string? kind = TargetKind(upper);
            if (kind == null)
                return;

            int id = GetObjectId(__instance);
            double p = SafeDouble(__instance, "BuildTimeProgress");
            int bucket = p >= 0.95 ? 95 : p >= 0.75 ? 75 : p >= 0.50 ? 50 : p >= 0.25 ? 25 : 0;

            if (!LastProgressBucket.TryGetValue(id, out int oldBucket) || oldBucket != bucket)
            {
                LastProgressBucket[id] = bucket;
                DumpTarget(__instance, upper, kind,
                    "IncreaseBuildTime delta=" + __0 + " progressBucket=" + bucket,
                    includeStack: LoggedBuildStacks.Add(id));
            }
        }
        catch (Exception ex)
        {
            Log.Write("[LEVEE-CMP] IncreaseBuildTime observer error: " + ex);
        }
    }

    static string? TargetKind(object blockObject)
    {
        string name = Convert.ToString(GetMember(blockObject, "Name")) ?? "";
        if (name.StartsWith(VanillaLevee, StringComparison.Ordinal))
            return "BUGGED-LANDSCAPING-LEVEE";
        if (name.StartsWith(SquareLevee, StringComparison.Ordinal))
            return "GOOD-SQUARE-LEVEE";
        return null;
    }

    static void DumpTarget(object site, object upper, string kind, string reason, bool includeStack)
    {
        var lines = new List<string>();
        lines.Add("[LEVEE-CMP] ===== " + kind + " =====");
        lines.Add("[LEVEE-CMP] Reason=" + reason);
        lines.Add("[LEVEE-CMP] Object=" + DescribeBlockObject(upper));
        lines.Add("[LEVEE-CMP] Site: IsOn=" + SafeValue(site, "IsOn")
            + " ReadyToBuild=" + SafeValue(site, "ReadyToBuild")
            + " IsReadyToFinish=" + SafeValue(site, "IsReadyToFinish")
            + " BuildTimeProgress=" + SafeValue(site, "BuildTimeProgress")
            + " BuildTimeProgressInHours=" + SafeValue(site, "BuildTimeProgressInHours")
            + " MaterialProgress=" + SafeValue(site, "MaterialProgress"));

        lines.Add("[LEVEE-CMP] Components (" + ComponentNames(upper).Count + "):");
        foreach (string component in ComponentNames(upper))
            lines.Add("[LEVEE-CMP]   COMPONENT " + component);

        var validators = GetValidators(site);
        lines.Add("[LEVEE-CMP] Validators (" + validators.Count + "):");
        foreach (var v in validators)
            lines.Add("[LEVEE-CMP]   VALIDATOR " + v.TypeName + " IsValid=" + v.IsValid);

        object? grounded = GetComponent(upper, GroundedConstructionSiteType);
        lines.Add("[LEVEE-CMP] GroundedConstructionSite present=" + (grounded != null)
            + (grounded == null ? "" : " IsValid=" + SafeValue(grounded, "IsValid")));

        object? accessible = GetComponent(upper, ConstructionSiteAccessibleType);
        if (accessible != null)
        {
            lines.Add("[LEVEE-CMP] Reach MinZ=" + SafeValue(accessible, "MinZ")
                + " MaxZ=" + SafeValue(accessible, "MaxZ"));
        }

        try
        {
            DumpBlocksAndSupports(lines, upper, grounded);
        }
        catch (Exception ex)
        {
            lines.Add("[LEVEE-CMP] BLOCK/SUPPORT DUMP ERROR: " + ex);
        }

        if (includeStack)
            lines.Add("[LEVEE-CMP] First-build call stack:" + Environment.NewLine + Environment.StackTrace);

        lines.Add("[LEVEE-CMP] ===== END " + kind + " =====");
        Log.Write(string.Join(Environment.NewLine, lines));
    }

    static void DumpBlocksAndSupports(List<string> lines, object upper, object? grounded)
    {
        object positioned = GetMember(upper, "PositionedBlocks");
        object blockService = GetField(upper, "_blockService");
        int baseZ = GetInt(GetMember(upper, "CoordinatesAtBaseZ"), "z");

        object? matterValidator = grounded == null ? null : GetField(grounded, "_matterBelowValidator");
        MethodInfo? validate = matterValidator == null ? null : MatterBelowValidatorType.GetMethods(AnyInstance)
            .Single(m => m.Name == "Validate" && m.GetParameters().Length == 1);
        MethodInfo? validateIgnore = matterValidator == null ? null : MatterBelowValidatorType.GetMethods(AnyInstance)
            .Single(m => m.Name == "ValidateIgnoringUnfinishedStackable" && m.GetParameters().Length == 1);

        object? stackableService = matterValidator == null ? null : GetField(matterValidator, "_stackableBlockService");
        MethodInfo? finishedStackable = stackableService?.GetType().GetMethods(AnyInstance)
            .Single(m => m.Name == "IsFinishedStackableBlockAt" && m.GetParameters().Length == 1);

        int n = 0;
        foreach (object block in Enumerate(InvokeNoArgs(positioned, "GetOccupiedBlocks")))
        {
            n++;
            object coords = GetMember(block, "Coordinates");
            bool atBase = GetInt(coords, "z") == baseZ;

            lines.Add("[LEVEE-CMP] BLOCK #" + n
                + " coords=" + FormatCoords(coords)
                + " atBase=" + atBase
                + " MatterBelow=" + SafeValue(block, "MatterBelow")
                + " Stackable=" + SafeValue(block, "Stackable")
                + " Occupations=" + SafeValue(block, "Occupations")
                + " Underground=" + SafeValue(block, "Underground")
                + " IsFoundationBlock=" + SafeValue(block, "IsFoundationBlock"));

            if (!atBase)
                continue;

            if (matterValidator != null && validate != null && validateIgnore != null)
            {
                object?[] a1 = { block };
                object?[] a2 = { block };
                bool normal = Convert.ToBoolean(validate.Invoke(matterValidator, a1));
                bool ignore = Convert.ToBoolean(validateIgnore.Invoke(matterValidator, a2));
                lines.Add("[LEVEE-CMP]   MatterBelowValidator normal=" + normal
                    + " ignoreUnfinishedStackable=" + ignore);
            }

            object below = CreateBelow(coords);

            if (stackableService != null && finishedStackable != null)
            {
                bool fs = Convert.ToBoolean(finishedStackable.Invoke(stackableService, new[] { below }));
                lines.Add("[LEVEE-CMP]   IsFinishedStackableBlockAt(" + FormatCoords(below) + ")=" + fs);
            }

            object objectsAt = InvokeOneArg(blockService, "GetObjectsAt", below);
            var belowObjects = Enumerate(objectsAt).Cast<object>().ToList();
            lines.Add("[LEVEE-CMP]   Below " + FormatCoords(below) + " objects=" + belowObjects.Count);

            foreach (object candidate in belowObjects)
            {
                string blockDetail;
                try
                {
                    object cpos = GetMember(candidate, "PositionedBlocks");
                    object cblock = InvokeOneArg(cpos, "GetBlock", below);
                    blockDetail = " block.Stackable=" + SafeValue(cblock, "Stackable")
                        + " block.MatterBelow=" + SafeValue(cblock, "MatterBelow")
                        + " block.Occupations=" + SafeValue(cblock, "Occupations")
                        + " block.Underground=" + SafeValue(cblock, "Underground");
                }
                catch (Exception ex)
                {
                    blockDetail = " block=<error:" + ex.GetType().Name + ">";
                }

                object? supportSite = GetComponent(candidate, ConstructionSiteType);
                string supportProgress = supportSite == null ? "" :
                    " supportProgress=" + SafeValue(supportSite, "BuildTimeProgress")
                    + " supportIsOn=" + SafeValue(supportSite, "IsOn");

                lines.Add("[LEVEE-CMP]     SUPPORT name=" + SafeValue(candidate, "Name")
                    + " coords=" + SafeValue(candidate, "Coordinates")
                    + " finished=" + SafeValue(candidate, "IsFinished")
                    + " unfinished=" + SafeValue(candidate, "IsUnfinished")
                    + supportProgress
                    + blockDetail);
            }
        }
    }

    static List<string> ComponentNames(object blockObject)
    {
        try
        {
            object all = GetMember(blockObject, "AllComponents");
            return Enumerate(all).Cast<object>()
                .Select(x => x.GetType().FullName ?? x.GetType().Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception ex)
        {
            return new List<string> { "<component-list-error:" + ex.GetType().Name + ">" };
        }
    }

    static List<ValidatorState> GetValidators(object site)
    {
        var result = new List<ValidatorState>();
        object validators;
        try { validators = GetField(site, "_constructionSiteValidators"); }
        catch (Exception ex)
        {
            result.Add(new ValidatorState("<validator-list-error:" + ex.GetType().Name + ">", false));
            return result;
        }

        foreach (object v in Enumerate(validators))
        {
            bool isValid;
            try { isValid = Convert.ToBoolean(GetMember(v, "IsValid")); }
            catch { isValid = false; }

            result.Add(new ValidatorState(v.GetType().FullName ?? v.GetType().Name, isValid));
        }
        return result;
    }

    static string DescribeBlockObject(object bo)
    {
        return "Name=" + SafeValue(bo, "Name")
            + " Coordinates=" + SafeValue(bo, "Coordinates")
            + " Base=" + SafeValue(bo, "CoordinatesAtBaseZ")
            + " IsFinished=" + SafeValue(bo, "IsFinished")
            + " IsUnfinished=" + SafeValue(bo, "IsUnfinished")
            + " IsPreview=" + SafeValue(bo, "IsPreview")
            + " Solid=" + SafeValue(bo, "Solid")
            + " GroundOnly=" + SafeValue(bo, "GroundOnly")
            + " AboveGround=" + SafeValue(bo, "AboveGround")
            + " AddedToService=" + SafeValue(bo, "AddedToService")
            + " Overridable=" + SafeValue(bo, "Overridable");
    }

    static void DumpConstructionPatchOwners(string phase)
    {
        Log.Write("[LEVEE-CMP] Harmony map " + phase + ":");
        foreach (string line in HarmonyOwnersForRelevantMethods())
            Log.Write("[LEVEE-CMP]   " + line);
    }

    static IEnumerable<string> HarmonyOwnersForRelevantMethods()
    {
        var targets = new List<(string Name, MethodBase? Method)>
        {
            ("ConstructionSite.ReadyToBuild", ConstructionSiteType.GetProperty("ReadyToBuild", AnyInstance)?.GetGetMethod(true)),
            ("ConstructionSite.IsOn", ConstructionSiteType.GetProperty("IsOn", AnyInstance)?.GetGetMethod(true)),
            ("ConstructionSite.IncreaseBuildTime", ConstructionSiteType.GetMethod("IncreaseBuildTime", AnyInstance, null, new[] { typeof(float) }, null)),
            ("GroundedConstructionSite.Validate", GroundedConstructionSiteType.GetMethod("Validate", AnyInstance)),
            ("MatterBelowValidator.ValidateIgnoringUnfinishedStackable", MatterBelowValidatorType.GetMethods(AnyInstance).FirstOrDefault(m => m.Name == "ValidateIgnoringUnfinishedStackable" && m.GetParameters().Length == 1)),
            ("ConstructionSiteAccessible.MinZ", ConstructionSiteAccessibleType.GetProperty("MinZ", AnyInstance)?.GetGetMethod(true)),
            ("ConstructionSiteAccessible.MaxZ", ConstructionSiteAccessibleType.GetProperty("MaxZ", AnyInstance)?.GetGetMethod(true)),
        };

        foreach (var t in targets)
            yield return t.Name + ": " + (t.Method == null ? "method not found" : HarmonyBridge.DescribePatchInfo(t.Method));
    }

    static Type FindType(string qualifiedName) => Type.GetType(qualifiedName, throwOnError: true)!;

    static int GetObjectId(object obj)
    {
        try
        {
            MethodInfo? m = obj.GetType().GetMethod("GetInstanceID", AnyInstance, null, Type.EmptyTypes, null);
            if (m != null) return Convert.ToInt32(m.Invoke(obj, null));
        }
        catch { }
        return RuntimeHelpers.GetHashCode(obj);
    }

    static object? GetComponent(object instance, Type componentType)
    {
        MethodInfo? m = instance.GetType().GetMethods(AnyInstance)
            .FirstOrDefault(x => x.Name == "GetComponent" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0);
        if (m == null) return null;
        try { return m.MakeGenericMethod(componentType).Invoke(instance, null); }
        catch { return null; }
    }

    static string SafeValue(object instance, string name)
    {
        try { return GetMember(instance, name)?.ToString() ?? "null"; }
        catch (Exception ex) { return "<error:" + ex.GetType().Name + ">"; }
    }

    static double SafeDouble(object instance, string name)
    {
        try { return Convert.ToDouble(GetMember(instance, name)); }
        catch { return 0.0; }
    }

    static int GetInt(object instance, string name) => Convert.ToInt32(GetMember(instance, name));

    static object GetField(object instance, string name)
    {
        FieldInfo? f = FindField(instance.GetType(), name);
        if (f == null) throw new MissingFieldException(instance.GetType().FullName, name);
        return f.GetValue(instance)!;
    }

    static FieldInfo? FindField(Type? type, string name)
    {
        while (type != null)
        {
            FieldInfo? f = type.GetField(name, AnyInstance);
            if (f != null) return f;
            type = type.BaseType;
        }
        return null;
    }

    static object GetMember(object instance, string name)
    {
        Type? type = instance.GetType();
        while (type != null)
        {
            PropertyInfo? p = type.GetProperty(name, AnyInstance);
            if (p != null) return p.GetValue(instance)!;
            FieldInfo? f = type.GetField(name, AnyInstance);
            if (f != null) return f.GetValue(instance)!;
            type = type.BaseType;
        }
        throw new MissingMemberException(instance.GetType().FullName, name);
    }

    static object InvokeNoArgs(object instance, string name)
    {
        MethodInfo? m = instance.GetType().GetMethods(AnyInstance)
            .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 0);
        if (m == null) throw new MissingMethodException(instance.GetType().FullName, name);
        return m.Invoke(instance, null)!;
    }

    static object InvokeOneArg(object instance, string name, object argument)
    {
        MethodInfo? m = instance.GetType().GetMethods(AnyInstance)
            .FirstOrDefault(x =>
            {
                if (x.Name != name) return false;
                ParameterInfo[] p = x.GetParameters();
                if (p.Length != 1) return false;
                Type pt = p[0].ParameterType;
                if (pt.IsByRef) pt = pt.GetElementType()!;
                return pt.IsInstanceOfType(argument);
            });

        if (m == null) throw new MissingMethodException(instance.GetType().FullName, name);
        return m.Invoke(instance, new[] { argument })!;
    }

    static IEnumerable Enumerate(object value)
    {
        if (value is IEnumerable e) return e;
        throw new InvalidOperationException(value.GetType().FullName + " is not enumerable");
    }

    static object CreateBelow(object coords)
    {
        Type t = coords.GetType();
        return Activator.CreateInstance(t,
            GetInt(coords, "x"),
            GetInt(coords, "y"),
            GetInt(coords, "z") - 1)!;
    }

    static string FormatCoords(object coords)
        => "(" + GetInt(coords, "x") + "," + GetInt(coords, "y") + "," + GetInt(coords, "z") + ")";

    sealed class ValidatorState
    {
        public string TypeName { get; }
        public bool IsValid { get; }
        public ValidatorState(string typeName, bool isValid) { TypeName = typeName; IsValid = isValid; }
    }

    sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

internal static class HarmonyBridge
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Type HarmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: true)!;
    static readonly Type HarmonyMethodType = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", throwOnError: true)!;

    public static void PatchPrefix(MethodBase original, MethodInfo patchMethodInfo)
    {
        Patch(original, patchMethodInfo, prefix: true);
    }

    public static void PatchPostfix(MethodBase original, MethodInfo patchMethodInfo)
    {
        Patch(original, patchMethodInfo, prefix: false);
    }

    static void Patch(MethodBase original, MethodInfo patchMethodInfo, bool prefix)
    {
        object harmony = Activator.CreateInstance(HarmonyType, "ConstructionSupportPassiveDiagnostic")!;
        object hm = Activator.CreateInstance(HarmonyMethodType, patchMethodInfo)!;
        MethodInfo patch = HarmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "Patch")
            .First(m =>
            {
                ParameterInfo[] p = m.GetParameters();
                return p.Length >= 3 && typeof(MethodBase).IsAssignableFrom(p[0].ParameterType);
            });

        object?[] args = new object?[patch.GetParameters().Length];
        args[0] = original;
        args[prefix ? 1 : 2] = hm;
        patch.Invoke(harmony, args);
    }

    public static string DescribePatchInfo(MethodBase method)
    {
        try
        {
            MethodInfo? getPatchInfo = HarmonyType.GetMethod(
                "GetPatchInfo", BindingFlags.Static | BindingFlags.Public, null,
                new[] { typeof(MethodBase) }, null);
            if (getPatchInfo == null) return "GetPatchInfo unavailable";

            object? info = getPatchInfo.Invoke(null, new object?[] { method });
            if (info == null) return "none";

            var parts = new List<string>();
            foreach (string category in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
            {
                object? collection = ReadMember(info, category);
                if (collection is not IEnumerable enumerable) continue;

                var entries = new List<string>();
                foreach (object p in enumerable)
                {
                    string owner = Convert.ToString(ReadMember(p, "owner") ?? ReadMember(p, "Owner")) ?? "?";
                    object? pm = ReadMember(p, "PatchMethod");
                    string methodName = pm is MethodBase mb
                        ? (mb.DeclaringType?.FullName ?? "?") + "." + mb.Name
                        : "?";
                    entries.Add(owner + "=>" + methodName);
                }
                if (entries.Count > 0) parts.Add(category + "[" + string.Join(", ", entries) + "]");
            }
            return parts.Count == 0 ? "none" : string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            return "<patch-info-error:" + ex.GetType().Name + ">";
        }
    }

    static object? ReadMember(object instance, string name)
    {
        Type? t = instance.GetType();
        while (t != null)
        {
            PropertyInfo? p = t.GetProperty(name, Any);
            if (p != null) return p.GetValue(instance);
            FieldInfo? f = t.GetField(name, Any);
            if (f != null) return f.GetValue(instance);
            t = t.BaseType;
        }
        return null;
    }
}

internal static class Log
{
    static readonly MethodInfo? UnityLog =
        Type.GetType("UnityEngine.Debug, UnityEngine.CoreModule", throwOnError: false)?
            .GetMethod("Log", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(object) }, null);

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
