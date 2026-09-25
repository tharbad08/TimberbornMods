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
        try
        {
            Diagnostic.Install();
        }
        catch (Exception ex)
        {
            Log.Write("[SUPPORTDIAG] Failed to install: " + ex);
        }
    }
}

internal static class Diagnostic
{
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    static Type ConstructionSiteType = null!;
    static Type GroundedConstructionSiteType = null!;
    static Type MatterBelowValidatorType = null!;
    static Type ConstructionSiteAccessibleType = null!;

    static MethodInfo? ResourcesFindObjectsOfTypeAll;
    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long lastScanMs;
    static readonly Dictionary<int, string> LastState = new();
    static readonly HashSet<int> LoggedNormalSupport = new();

    public static void Install()
    {
        ConstructionSiteType = FindType("Timberborn.ConstructionSites.ConstructionSite, Timberborn.ConstructionSites");
        GroundedConstructionSiteType = FindType("Timberborn.ConstructionSites.GroundedConstructionSite, Timberborn.ConstructionSites");
        MatterBelowValidatorType = FindType("Timberborn.BlockSystem.MatterBelowValidator, Timberborn.BlockSystem");
        ConstructionSiteAccessibleType = FindType("Timberborn.BuildingsNavigation.ConstructionSiteAccessible, Timberborn.BuildingsNavigation");

        var resourcesType = FindType("UnityEngine.Resources, UnityEngine.CoreModule");
        ResourcesFindObjectsOfTypeAll = resourcesType.GetMethod(
            "FindObjectsOfTypeAll",
            BindingFlags.Static | BindingFlags.Public,
            null,
            new[] { typeof(Type) },
            null);

        if (ResourcesFindObjectsOfTypeAll == null)
            throw new MissingMethodException("UnityEngine.Resources.FindObjectsOfTypeAll(Type)");

        // Deliberately patch an unrelated periodic service, NOT any construction method.
        var soilType = FindType("Timberborn.SoilContaminationSystem.SoilContaminationService, Timberborn.SoilContaminationSystem");
        var tick = soilType.GetMethod("Tick", AnyInstance, null, Type.EmptyTypes, null)
            ?? soilType.GetMethods(AnyInstance).FirstOrDefault(m => m.Name.EndsWith(".Tick") && m.GetParameters().Length == 0)
            ?? throw new MissingMethodException(soilType.FullName, "Tick");

        HarmonyBridge.PatchPostfix(tick, typeof(Diagnostic).GetMethod(nameof(AfterUnrelatedWorldTick), AnyStatic)!);

        Log.Write("[SUPPORTDIAG] Passive diagnostic installed.");
        Log.Write("[SUPPORTDIAG] Construction methods are NOT patched. Sampler hook: " + tick.DeclaringType?.FullName + "." + tick.Name);
        DumpConstructionPatchOwners();
    }

    public static void AfterUnrelatedWorldTick()
    {
        long now = Clock.ElapsedMilliseconds;
        if (now - lastScanMs < 1500)
            return;

        lastScanMs = now;

        try
        {
            Scan();
        }
        catch (Exception ex)
        {
            Log.Write("[SUPPORTDIAG] Scan error: " + ex);
        }
    }

    static void Scan()
    {
        var raw = ResourcesFindObjectsOfTypeAll!.Invoke(null, new object?[] { ConstructionSiteType });
        if (raw is not IEnumerable sites)
            return;

        foreach (object? site in sites)
        {
            if (site == null)
                continue;

            object upper;
            try { upper = GetField(site, "_blockObject"); }
            catch { continue; }

            if (!SafeBool(upper, "IsUnfinished"))
                continue;

            if (!TryGetDirectUnfinishedSupports(site, upper, out var supports))
                continue;

            bool isOn = SafeBool(site, "IsOn");
            bool ready = SafeBool(site, "ReadyToBuild");
            bool readyFinish = SafeBool(site, "IsReadyToFinish");
            string buildProgress = SafeValue(site, "BuildTimeProgress");
            string buildHours = SafeValue(site, "BuildTimeProgressInHours");

            var validators = GetValidators(site);
            bool groundedPresent = validators.Any(v => v.TypeName == GroundedConstructionSiteType.FullName);
            bool groundedValid = validators.Any(v => v.TypeName == GroundedConstructionSiteType.FullName && v.IsValid);

            string groundingRecheck = RecheckGrounding(upper);
            bool independentInvalid = groundingRecheck.Contains("ignoreUnfinished=False");

            // This is the impossible state we're chasing:
            // direct unfinished support exists, yet site is enabled/buildable or grounded validator says valid.
            bool anomaly = isOn || ready || readyFinish || groundedValid || !groundedPresent;

            int id = GetObjectId(site);
            string signature =
                "isOn=" + isOn +
                "|ready=" + ready +
                "|readyFinish=" + readyFinish +
                "|groundedPresent=" + groundedPresent +
                "|groundedValid=" + groundedValid +
                "|build=" + buildProgress +
                "|hours=" + buildHours +
                "|recheck=" + groundingRecheck;

            if (LastState.TryGetValue(id, out string? previous) && previous == signature)
                continue;

            LastState[id] = signature;

            if (anomaly)
            {
                Log.Write(BuildReport(site, upper, supports, validators, groundingRecheck, independentInvalid));
            }
            else if (LoggedNormalSupport.Add(id))
            {
                Log.Write("[SUPPORTDIAG] Control: unfinished support correctly blocks upper site: "
                    + DescribeBlockObject(upper)
                    + " | Grounded=" + groundedValid
                    + " IsOn=" + isOn
                    + " ReadyToBuild=" + ready);
            }
        }
    }

    static string BuildReport(
        object site,
        object upper,
        List<object> supports,
        List<ValidatorState> validators,
        string groundingRecheck,
        bool independentInvalid)
    {
        var lines = new List<string>();
        lines.Add("[SUPPORTDIAG] ===== SUPPORT ANOMALY =====");
        lines.Add("[SUPPORTDIAG] Upper: " + DescribeBlockObject(upper));
        lines.Add("[SUPPORTDIAG] Site: IsOn=" + SafeValue(site, "IsOn")
            + " ReadyToBuild=" + SafeValue(site, "ReadyToBuild")
            + " IsReadyToFinish=" + SafeValue(site, "IsReadyToFinish")
            + " BuildTimeProgress=" + SafeValue(site, "BuildTimeProgress")
            + " BuildTimeProgressInHours=" + SafeValue(site, "BuildTimeProgressInHours")
            + " MaterialProgress=" + SafeValue(site, "MaterialProgress"));

        lines.Add("[SUPPORTDIAG] Direct unfinished support(s):");
        foreach (object support in supports)
            lines.Add("[SUPPORTDIAG]   - " + DescribeBlockObject(support));

        lines.Add("[SUPPORTDIAG] Validators:");
        foreach (var v in validators)
            lines.Add("[SUPPORTDIAG]   - " + v.TypeName + " IsValid=" + v.IsValid);

        if (validators.All(v => v.TypeName != GroundedConstructionSiteType.FullName))
            lines.Add("[SUPPORTDIAG]   !!! GroundedConstructionSite validator MISSING");

        lines.Add("[SUPPORTDIAG] Independent grounding re-check: " + groundingRecheck);
        lines.Add("[SUPPORTDIAG] Independent support says blocked=" + independentInvalid);

        object? accessible = GetComponent(upper, ConstructionSiteAccessibleType);
        if (accessible != null)
        {
            lines.Add("[SUPPORTDIAG] Reach: MinZ=" + SafeValue(accessible, "MinZ")
                + " MaxZ=" + SafeValue(accessible, "MaxZ")
                + " upperBase=" + SafeValue(upper, "CoordinatesAtBaseZ"));
        }

        lines.Add("[SUPPORTDIAG] Construction Harmony owners:");
        foreach (string s in HarmonyOwnersForRelevantMethods())
            lines.Add("[SUPPORTDIAG]   " + s);

        lines.Add("[SUPPORTDIAG] ===== END SUPPORT ANOMALY =====");
        return string.Join(Environment.NewLine, lines);
    }

    static bool TryGetDirectUnfinishedSupports(object site, object upper, out List<object> supports)
    {
        supports = new List<object>();
        var seen = new HashSet<int>();

        int baseZ = GetInt(GetMember(upper, "CoordinatesAtBaseZ"), "z");
        object positioned = GetMember(upper, "PositionedBlocks");
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
            object objectsAt;
            try { objectsAt = InvokeOneArg(blockService, "GetObjectsAt", below); }
            catch { continue; }

            foreach (object candidate in Enumerate(objectsAt))
            {
                if (ReferenceEquals(candidate, upper) || !SafeBool(candidate, "IsUnfinished"))
                    continue;

                object candidateBlocks;
                object candidateBlock;
                try
                {
                    candidateBlocks = GetMember(candidate, "PositionedBlocks");
                    candidateBlock = InvokeOneArg(candidateBlocks, "GetBlock", below);
                }
                catch
                {
                    continue;
                }

                string stackable = Convert.ToString(GetMember(candidateBlock, "Stackable")) ?? "None";
                if (stackable == "None")
                    continue;

                int id = GetObjectId(candidate);
                if (seen.Add(id))
                    supports.Add(candidate);
            }
        }

        return supports.Count > 0;
    }

    static List<ValidatorState> GetValidators(object site)
    {
        var result = new List<ValidatorState>();
        object validators;
        try { validators = GetField(site, "_constructionSiteValidators"); }
        catch { return result; }

        foreach (object v in Enumerate(validators))
        {
            result.Add(new ValidatorState(
                v.GetType().FullName ?? v.GetType().Name,
                SafeBool(v, "IsValid")));
        }

        return result;
    }

    static string RecheckGrounding(object upper)
    {
        object? grounded = GetComponent(upper, GroundedConstructionSiteType);
        if (grounded == null)
            return "GroundedConstructionSite component missing";

        object mv = GetField(grounded, "_matterBelowValidator");
        MethodInfo normal = MatterBelowValidatorType.GetMethod("Validate", AnyInstance)
            ?? throw new MissingMethodException(MatterBelowValidatorType.FullName, "Validate");
        MethodInfo ignore = MatterBelowValidatorType.GetMethod("ValidateIgnoringUnfinishedStackable", AnyInstance)
            ?? throw new MissingMethodException(MatterBelowValidatorType.FullName, "ValidateIgnoringUnfinishedStackable");

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
            bool n = Convert.ToBoolean(normal.Invoke(mv, a1));
            bool ig = Convert.ToBoolean(ignore.Invoke(mv, a2));
            pieces.Add("#" + (++i)
                + " normal=" + n
                + " ignoreUnfinished=" + ig
                + " at " + FormatCoords(coords));
        }

        return pieces.Count == 0 ? "no solid-matter foundation blocks found" : string.Join("; ", pieces);
    }

    static string DescribeBlockObject(object bo)
    {
        object? site = GetComponent(bo, ConstructionSiteType);
        string siteInfo = site == null
            ? " no ConstructionSite"
            : " progress=" + SafeValue(site, "BuildTimeProgress")
              + " hours=" + SafeValue(site, "BuildTimeProgressInHours")
              + " IsOn=" + SafeValue(site, "IsOn")
              + " ReadyToBuild=" + SafeValue(site, "ReadyToBuild");

        return "Name=" + SafeValue(bo, "Name")
            + " Coordinates=" + SafeValue(bo, "Coordinates")
            + " Base=" + SafeValue(bo, "CoordinatesAtBaseZ")
            + " unfinished=" + SafeValue(bo, "IsUnfinished")
            + " finished=" + SafeValue(bo, "IsFinished")
            + siteInfo;
    }

    static void DumpConstructionPatchOwners()
    {
        Log.Write("[SUPPORTDIAG] Construction Harmony map at startup:");
        foreach (string line in HarmonyOwnersForRelevantMethods())
            Log.Write("[SUPPORTDIAG]   " + line);
    }

    static IEnumerable<string> HarmonyOwnersForRelevantMethods()
    {
        var targets = new List<(string Name, MethodBase? Method)>
        {
            ("ConstructionSite.ReadyToBuild", ConstructionSiteType.GetProperty("ReadyToBuild", AnyInstance)?.GetGetMethod(true)),
            ("ConstructionSite.IsOn", ConstructionSiteType.GetProperty("IsOn", AnyInstance)?.GetGetMethod(true)),
            ("ConstructionSite.IsReadyToFinish", ConstructionSiteType.GetProperty("IsReadyToFinish", AnyInstance)?.GetGetMethod(true)),
            ("ConstructionSite.IncreaseBuildTime", ConstructionSiteType.GetMethod("IncreaseBuildTime", AnyInstance, null, new[] { typeof(float) }, null)),
            ("ConstructionSite.FinishNow", ConstructionSiteType.GetMethod("FinishNow", AnyInstance)),
            ("ConstructionSite.FinishIfRequirementsMet", ConstructionSiteType.GetMethod("FinishIfRequirementsMet", AnyInstance)),
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

    static Type FindType(string qualifiedName)
        => Type.GetType(qualifiedName, throwOnError: true)!;

    static int GetObjectId(object obj)
    {
        try
        {
            MethodInfo? m = obj.GetType().GetMethod("GetInstanceID", AnyInstance, null, Type.EmptyTypes, null);
            if (m != null)
                return Convert.ToInt32(m.Invoke(obj, null));
        }
        catch { }

        return RuntimeHelpers.GetHashCode(obj);
    }

    static object? GetComponent(object instance, Type componentType)
    {
        MethodInfo? m = instance.GetType().GetMethods(AnyInstance)
            .FirstOrDefault(x => x.Name == "GetComponent" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0);

        if (m == null)
            return null;

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

    static int GetInt(object instance, string name)
        => Convert.ToInt32(GetMember(instance, name));

    static object GetField(object instance, string name)
    {
        FieldInfo? f = FindField(instance.GetType(), name);
        if (f == null)
            throw new MissingFieldException(instance.GetType().FullName, name);
        return f.GetValue(instance)!;
    }

    static FieldInfo? FindField(Type? type, string name)
    {
        while (type != null)
        {
            FieldInfo? f = type.GetField(name, AnyInstance);
            if (f != null)
                return f;
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
            if (p != null)
                return p.GetValue(instance)!;

            FieldInfo? f = type.GetField(name, AnyInstance);
            if (f != null)
                return f.GetValue(instance)!;

            type = type.BaseType;
        }

        throw new MissingMemberException(instance.GetType().FullName, name);
    }

    static object InvokeNoArgs(object instance, string name)
    {
        MethodInfo? m = instance.GetType().GetMethods(AnyInstance)
            .FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 0);
        if (m == null)
            throw new MissingMethodException(instance.GetType().FullName, name);
        return m.Invoke(instance, null)!;
    }

    static object InvokeOneArg(object instance, string name, object argument)
    {
        MethodInfo? m = instance.GetType().GetMethods(AnyInstance)
            .FirstOrDefault(x =>
            {
                if (x.Name != name)
                    return false;
                ParameterInfo[] p = x.GetParameters();
                if (p.Length != 1)
                    return false;

                Type pt = p[0].ParameterType;
                if (pt.IsByRef)
                    pt = pt.GetElementType()!;

                return pt.IsInstanceOfType(argument);
            });

        if (m == null)
            throw new MissingMethodException(instance.GetType().FullName, name);

        return m.Invoke(instance, new[] { argument })!;
    }

    static IEnumerable Enumerate(object value)
    {
        if (value is IEnumerable e)
            return e;
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
}

internal static class HarmonyBridge
{
    const BindingFlags Any = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    static readonly Type HarmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: true)!;
    static readonly Type HarmonyMethodType = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", throwOnError: true)!;

    public static void PatchPostfix(MethodBase original, MethodInfo patchMethodInfo)
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
        args[2] = hm;
        patch.Invoke(harmony, args);
    }

    public static string DescribePatchInfo(MethodBase method)
    {
        try
        {
            MethodInfo? getPatchInfo = HarmonyType.GetMethod(
                "GetPatchInfo",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(MethodBase) },
                null);

            if (getPatchInfo == null)
                return "GetPatchInfo unavailable";

            object? info = getPatchInfo.Invoke(null, new object?[] { method });
            if (info == null)
                return "none";

            var parts = new List<string>();
            foreach (string category in new[] { "Prefixes", "Postfixes", "Transpilers", "Finalizers" })
            {
                object? collection = ReadMember(info, category);
                if (collection is not IEnumerable enumerable)
                    continue;

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
            PropertyInfo? p = t.GetProperty(name, Any);
            if (p != null)
                return p.GetValue(instance);

            FieldInfo? f = t.GetField(name, Any);
            if (f != null)
                return f.GetValue(instance);

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
            if (UnityLog != null)
                UnityLog.Invoke(null, new object?[] { message });
            else
                Console.WriteLine(message);
        }
        catch
        {
            Console.WriteLine(message);
        }
    }
}
