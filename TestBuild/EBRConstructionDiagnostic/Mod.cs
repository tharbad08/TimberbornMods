using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Timberborn.ModManagerScene;

namespace EBRConstructionDiagnostic;

public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        try
        {
            Diagnostic.Install();
        }
        catch (Exception ex)
        {
            Log.Write("[EBRDIAG] Failed to install diagnostic: " + ex);
        }
    }
}

internal static class Diagnostic
{
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static int suspiciousProgressEvents;
    static int suspiciousFinishEvents;
    const int MaxDetailedEvents = 20;

    static Type ConstructionSiteType = null!;
    static Type BlockObjectType = null!;
    static Type GroundedConstructionSiteType = null!;
    static Type MatterBelowValidatorType = null!;
    static Type ConstructionSiteAccessibleType = null!;

    public static void Install()
    {
        ConstructionSiteType = FindType("Timberborn.ConstructionSites.ConstructionSite, Timberborn.ConstructionSites");
        BlockObjectType = FindType("Timberborn.BlockSystem.BlockObject, Timberborn.BlockSystem");
        GroundedConstructionSiteType = FindType("Timberborn.ConstructionSites.GroundedConstructionSite, Timberborn.ConstructionSites");
        MatterBelowValidatorType = FindType("Timberborn.BlockSystem.MatterBelowValidator, Timberborn.BlockSystem");
        ConstructionSiteAccessibleType = FindType("Timberborn.BuildingsNavigation.ConstructionSiteAccessible, Timberborn.BuildingsNavigation");

        Patch(ConstructionSiteType.GetMethod("IncreaseBuildTime", AnyInstance, null, new[] { typeof(float) }, null)!,
            nameof(BeforeIncreaseBuildTime), prefix: true);

        Patch(ConstructionSiteType.GetMethod("FinishNow", AnyInstance)!,
            nameof(BeforeFinishNow), prefix: true);

        Patch(BlockObjectType.GetMethod("MarkAsFinished", AnyInstance)!,
            nameof(BeforeMarkAsFinished), prefix: true);

        Log.Write("[EBRDIAG] Installed. This mod changes no construction behavior.");
        DumpRelevantHarmonyOwners();
    }

    public static void BeforeIncreaseBuildTime(object __instance, float hours)
    {
        if (!TryGetSuspiciousState(__instance, out var state))
            return;

        var n = ++suspiciousProgressEvents;
        if (n <= MaxDetailedEvents)
        {
            Log.Write(BuildReport("IncreaseBuildTime", __instance, state, "hours=" + hours.ToString("R")));
        }
        else if (n == MaxDetailedEvents + 1)
        {
            Log.Write("[EBRDIAG] More suspicious IncreaseBuildTime events detected; detailed logging capped at " + MaxDetailedEvents + ".");
        }
    }

    public static void BeforeFinishNow(object __instance)
    {
        if (!TryGetSuspiciousState(__instance, out var state))
            return;

        var n = ++suspiciousFinishEvents;
        if (n <= MaxDetailedEvents)
            Log.Write(BuildReport("FinishNow", __instance, state, null));
    }

    public static void BeforeMarkAsFinished(object __instance)
    {
        if (!GetBool(__instance, "IsUnfinished"))
            return;

        object? site = GetComponent(__instance, ConstructionSiteType);
        if (site == null || !TryGetSuspiciousState(site, out var state))
            return;

        Log.Write(BuildReport("BlockObject.MarkAsFinished", site, state, null));
    }

    static string BuildReport(string trigger, object site, SuspiciousState state, string? extra)
    {
        var lines = new List<string>();
        lines.Add("[EBRDIAG] ===== SUSPICIOUS CONSTRUCTION EVENT =====");
        lines.Add("[EBRDIAG] Trigger: " + trigger + (extra == null ? "" : " (" + extra + ")"));
        lines.Add("[EBRDIAG] Upper: " + DescribeBlockObject(state.UpperBlockObject));
        lines.Add("[EBRDIAG] Site: IsOn=" + SafeValue(site, "IsOn")
            + " ReadyToBuild=" + SafeValue(site, "ReadyToBuild")
            + " IsReadyToFinish=" + SafeValue(site, "IsReadyToFinish")
            + " BuildTimeProgress=" + SafeValue(site, "BuildTimeProgress")
            + " BuildTimeHours=" + SafeValue(site, "BuildTimeProgressInHours")
            + " MaterialProgress=" + SafeValue(site, "MaterialProgress"));

        lines.Add("[EBRDIAG] Validators:");
        foreach (var v in state.Validators)
        {
            lines.Add("[EBRDIAG]   - " + v.TypeName + " IsValid=" + v.IsValid);
        }

        if (state.Validators.All(v => v.TypeName != GroundedConstructionSiteType.FullName))
            lines.Add("[EBRDIAG]   !!! GroundedConstructionSite validator is MISSING");

        lines.Add("[EBRDIAG] Direct unfinished support(s):");
        foreach (var support in state.Supports)
            lines.Add("[EBRDIAG]   - " + DescribeBlockObject(support));

        lines.Add("[EBRDIAG] Grounding re-check (current state): " + state.GroundingRecheck);
        lines.Add("[EBRDIAG] Harmony owners at event:");
        foreach (var s in HarmonyOwnersForRelevantMethods())
            lines.Add("[EBRDIAG]   " + s);

        lines.Add("[EBRDIAG] Call stack:");
        foreach (var s in Environment.StackTrace.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            lines.Add("[EBRDIAG]   " + s.Trim());

        lines.Add("[EBRDIAG] ===== END SUSPICIOUS EVENT =====");
        return string.Join(Environment.NewLine, lines);
    }

    static bool TryGetSuspiciousState(object site, out SuspiciousState state)
    {
        state = default!;

        object upper = GetField(site, "_blockObject");
        if (!GetBool(upper, "IsUnfinished"))
            return false;

        int baseZ = GetInt(GetMember(upper, "CoordinatesAtBaseZ"), "z");
        object positioned = GetMember(upper, "PositionedBlocks");
        var supports = new HashSet<object>(ReferenceEqualityComparer.Instance);

        object blockService = GetField(upper, "_blockService");

        foreach (object block in Enumerate(InvokeNoArgs(positioned, "GetOccupiedBlocks")))
        {
            object coords = GetMember(block, "Coordinates");
            if (GetInt(coords, "z") != baseZ)
                continue;

            string matterBelow = Convert.ToString(GetMember(block, "MatterBelow")) ?? "";
            if (matterBelow != "Ground" && matterBelow != "GroundOrStackable" && matterBelow != "Stackable")
                continue;

            object below = CreateBelow(coords);
            object objectsAt = InvokeOneArg(blockService, "GetObjectsAt", below);

            foreach (object candidate in Enumerate(objectsAt))
            {
                if (ReferenceEquals(candidate, upper) || !GetBool(candidate, "IsUnfinished"))
                    continue;

                object candidateBlocks = GetMember(candidate, "PositionedBlocks");
                object candidateBlock;
                try { candidateBlock = InvokeOneArg(candidateBlocks, "GetBlock", below); }
                catch { continue; }

                string stackable = Convert.ToString(GetMember(candidateBlock, "Stackable")) ?? "None";
                if (stackable != "None")
                    supports.Add(candidate);
            }
        }

        if (supports.Count == 0)
            return false;

        var validators = GetValidators(site);
        string grounding = RecheckGrounding(upper, validators);

        state = new SuspiciousState(upper, supports.ToList(), validators, grounding);
        return true;
    }

    static List<ValidatorState> GetValidators(object site)
    {
        var result = new List<ValidatorState>();
        object validators = GetField(site, "_constructionSiteValidators");
        foreach (object v in Enumerate(validators))
        {
            result.Add(new ValidatorState(
                v.GetType().FullName ?? v.GetType().Name,
                SafeBool(v, "IsValid")));
        }
        return result;
    }

    static string RecheckGrounding(object upper, List<ValidatorState> validators)
    {
        object? grounded = GetComponent(upper, GroundedConstructionSiteType);
        if (grounded == null)
            return "GroundedConstructionSite component missing";

        object mv = GetField(grounded, "_matterBelowValidator");
        MethodInfo normal = MatterBelowValidatorType.GetMethod("Validate", AnyInstance)!;
        MethodInfo ignore = MatterBelowValidatorType.GetMethod("ValidateIgnoringUnfinishedStackable", AnyInstance)!;

        int baseZ = GetInt(GetMember(upper, "CoordinatesAtBaseZ"), "z");
        object positioned = GetMember(upper, "PositionedBlocks");

        var pieces = new List<string>();
        int i = 0;
        foreach (object block in Enumerate(InvokeNoArgs(positioned, "GetOccupiedBlocks")))
        {
            object coords = GetMember(block, "Coordinates");
            if (GetInt(coords, "z") != baseZ)
                continue;

            string matterBelow = Convert.ToString(GetMember(block, "MatterBelow")) ?? "";
            if (matterBelow != "Ground" && matterBelow != "GroundOrStackable" && matterBelow != "Stackable")
                continue;

            object?[] a1 = { block };
            object?[] a2 = { block };
            bool n = (bool)normal.Invoke(mv, a1)!;
            bool ig = (bool)ignore.Invoke(mv, a2)!;
            pieces.Add("#" + (++i) + " normal=" + n + " ignoreUnfinished=" + ig + " at " + FormatCoords(coords));
        }

        return pieces.Count == 0 ? "no solid-matter foundation blocks found" : string.Join("; ", pieces);
    }

    static string DescribeBlockObject(object bo)
    {
        var name = SafeValue(bo, "Name");
        var coords = SafeValue(bo, "Coordinates");
        var baseCoords = SafeValue(bo, "CoordinatesAtBaseZ");
        var unfinished = SafeValue(bo, "IsUnfinished");
        var finished = SafeValue(bo, "IsFinished");

        object? site = GetComponent(bo, ConstructionSiteType);
        string siteInfo = site == null ? " no ConstructionSite"
            : " progress=" + SafeValue(site, "BuildTimeProgress")
              + " hours=" + SafeValue(site, "BuildTimeProgressInHours")
              + " IsOn=" + SafeValue(site, "IsOn")
              + " ReadyToBuild=" + SafeValue(site, "ReadyToBuild");

        return "Name=" + name + " Coordinates=" + coords + " Base=" + baseCoords
            + " unfinished=" + unfinished + " finished=" + finished + siteInfo;
    }

    static void DumpRelevantHarmonyOwners()
    {
        Log.Write("[EBRDIAG] Harmony patch map at startup:");
        foreach (var line in HarmonyOwnersForRelevantMethods())
            Log.Write("[EBRDIAG]   " + line);
    }

    static IEnumerable<string> HarmonyOwnersForRelevantMethods()
    {
        var targets = new List<(string Name, MethodBase? Method)>
        {
            ("ConstructionSite.IncreaseBuildTime", ConstructionSiteType.GetMethod("IncreaseBuildTime", AnyInstance, null, new[] { typeof(float) }, null)),
            ("ConstructionSite.FinishNow", ConstructionSiteType.GetMethod("FinishNow", AnyInstance)),
            ("ConstructionSite.ReadyToBuild", ConstructionSiteType.GetProperty("ReadyToBuild", AnyInstance)?.GetGetMethod(true)),
            ("ConstructionSite.IsOn", ConstructionSiteType.GetProperty("IsOn", AnyInstance)?.GetGetMethod(true)),
            ("ConstructionSite.IsReadyToFinish", ConstructionSiteType.GetProperty("IsReadyToFinish", AnyInstance)?.GetGetMethod(true)),
            ("GroundedConstructionSite.Validate", GroundedConstructionSiteType.GetMethod("Validate", AnyInstance)),
            ("MatterBelowValidator.ValidateIgnoringUnfinishedStackable", MatterBelowValidatorType.GetMethod("ValidateIgnoringUnfinishedStackable", AnyInstance)),
            ("ConstructionSiteAccessible.MinZ", ConstructionSiteAccessibleType.GetProperty("MinZ", AnyInstance)?.GetGetMethod(true)),
            ("ConstructionSiteAccessible.MaxZ", ConstructionSiteAccessibleType.GetProperty("MaxZ", AnyInstance)?.GetGetMethod(true)),
        };

        foreach (var t in targets)
        {
            if (t.Method == null)
            {
                yield return t.Name + ": method not found";
                continue;
            }

            yield return t.Name + ": " + HarmonyBridge.DescribePatchInfo(t.Method);
        }
    }

    static void Patch(MethodBase original, string diagnosticMethod, bool prefix)
    {
        MethodInfo patch = typeof(Diagnostic).GetMethod(diagnosticMethod, AnyStatic)!;
        HarmonyBridge.Patch(original, patch, prefix);
    }

    static Type FindType(string qualifiedName)
        => Type.GetType(qualifiedName, throwOnError: true)!;

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
        try
        {
            object? v = GetMember(instance, name);
            return v?.ToString() ?? "null";
        }
        catch (Exception ex)
        {
            return "<error:" + ex.GetType().Name + ">";
        }
    }

    static bool SafeBool(object instance, string name)
    {
        try { return Convert.ToBoolean(GetMember(instance, name)); }
        catch { return false; }
    }

    static bool GetBool(object instance, string name) => Convert.ToBoolean(GetMember(instance, name));
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
            var f = type.GetField(name, AnyInstance);
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
                var p = x.GetParameters();
                if (p.Length != 1) return false;
                var pt = p[0].ParameterType;
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
        int x = GetInt(coords, "x");
        int y = GetInt(coords, "y");
        int z = GetInt(coords, "z") - 1;
        return Activator.CreateInstance(t, x, y, z)!;
    }

    static string FormatCoords(object coords)
        => "(" + GetInt(coords, "x") + "," + GetInt(coords, "y") + "," + GetInt(coords, "z") + ")";

    sealed class ValidatorState
    {
        public string TypeName { get; }
        public bool IsValid { get; }

        public ValidatorState(string typeName, bool isValid)
        {
            TypeName = typeName;
            IsValid = isValid;
        }
    }

    sealed class SuspiciousState
    {
        public object UpperBlockObject { get; }
        public List<object> Supports { get; }
        public List<ValidatorState> Validators { get; }
        public string GroundingRecheck { get; }

        public SuspiciousState(
            object upperBlockObject,
            List<object> supports,
            List<ValidatorState> validators,
            string groundingRecheck)
        {
            UpperBlockObject = upperBlockObject;
            Supports = supports;
            Validators = validators;
            GroundingRecheck = groundingRecheck;
        }
    }

    sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

internal static class HarmonyBridge
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static readonly Type HarmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: true)!;
    static readonly Type HarmonyMethodType = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", throwOnError: true)!;

    public static void Patch(MethodBase original, MethodInfo patchMethodInfo, bool prefix)
    {
        object harmony = Activator.CreateInstance(HarmonyType, "EBRConstructionDiagnostic")!;
        object hm = Activator.CreateInstance(HarmonyMethodType, patchMethodInfo)!;

        MethodInfo patch = HarmonyType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "Patch")
            .First(m =>
            {
                var p = m.GetParameters();
                return p.Length >= 3 && typeof(MethodBase).IsAssignableFrom(p[0].ParameterType);
            });

        object?[] args = new object?[patch.GetParameters().Length];
        args[0] = original;
        if (prefix) args[1] = hm;
        else args[2] = hm;
        patch.Invoke(harmony, args);
    }

    public static string DescribePatchInfo(MethodBase method)
    {
        try
        {
            MethodInfo? getPatchInfo = HarmonyType.GetMethod("GetPatchInfo", BindingFlags.Static | BindingFlags.Public, null, new[] { typeof(MethodBase) }, null);
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

                if (entries.Count > 0)
                    parts.Add(category + "[" + string.Join(", ", entries) + "]");
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
            var p = t.GetProperty(name, Any);
            if (p != null) return p.GetValue(instance);
            var f = t.GetField(name, Any);
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
        catch
        {
            Console.WriteLine(message);
        }
    }
}
