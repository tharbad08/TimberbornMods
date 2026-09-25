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
    static Type BlockObjectType = null!;

    static readonly Stopwatch Clock = Stopwatch.StartNew();
    static long lastScanMs;
    static bool dumpedFinalPatchMap;
    static readonly HashSet<object> ConstructionSites = new(ReferenceEqualityComparer.Instance);
    static readonly Dictionary<int, string> LastState = new();
    static readonly HashSet<int> LoggedNormalSupport = new();

    public static void Install()
    {
        ConstructionSiteType = FindType("Timberborn.ConstructionSites.ConstructionSite, Timberborn.ConstructionSites");
        GroundedConstructionSiteType = FindType("Timberborn.ConstructionSites.GroundedConstructionSite, Timberborn.ConstructionSites");
        MatterBelowValidatorType = FindType("Timberborn.BlockSystem.MatterBelowValidator, Timberborn.BlockSystem");
        ConstructionSiteAccessibleType = FindType("Timberborn.BuildingsNavigation.ConstructionSiteAccessible, Timberborn.BuildingsNavigation");
        BlockObjectType = FindType("Timberborn.BlockSystem.BlockObject, Timberborn.BlockSystem");

        var registryType = FindType("Timberborn.EntitySystem.EntityComponentRegistry, Timberborn.EntitySystem");
        var entityComponentType = FindType("Timberborn.EntitySystem.EntityComponent, Timberborn.EntitySystem");

        var register = registryType.GetMethod("Register", AnyInstance, null, new[] { entityComponentType }, null)
            ?? throw new MissingMethodException(registryType.FullName, "Register(EntityComponent)");
        var unregister = registryType.GetMethod("Unregister", AnyInstance, null, new[] { entityComponentType }, null)
            ?? throw new MissingMethodException(registryType.FullName, "Unregister(EntityComponent)");

        HarmonyBridge.PatchPostfix(register, typeof(Diagnostic).GetMethod(nameof(AfterEntityRegistered), AnyStatic)!);
        HarmonyBridge.PatchPostfix(unregister, typeof(Diagnostic).GetMethod(nameof(AfterEntityUnregistered), AnyStatic)!);

        // Deliberately patch the global simulation tick, NOT any construction method.
        // This gives us frequent sampling without touching construction behavior.
        var tickServiceType = FindType("Timberborn.TickSystem.TickableSingletonService, Timberborn.TickSystem");
        var tick = tickServiceType.GetMethod("TickAll", AnyInstance, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(tickServiceType.FullName, "TickAll");

        HarmonyBridge.PatchPostfix(tick, typeof(Diagnostic).GetMethod(nameof(AfterUnrelatedWorldTick), AnyStatic)!);

        // Event-driven observers. These do not alter args/results and always allow originals to run.
        var increase = ConstructionSiteType.GetMethod("IncreaseBuildTime", AnyInstance, null, new[] { typeof(float) }, null)
            ?? throw new MissingMethodException(ConstructionSiteType.FullName, "IncreaseBuildTime(float)");
        HarmonyBridge.PatchPrefix(increase, typeof(Diagnostic).GetMethod(nameof(BeforeIncreaseBuildTime), AnyStatic)!);

        var markFinished = BlockObjectType.GetMethod("MarkAsFinished", AnyInstance, null, Type.EmptyTypes, null)
            ?? throw new MissingMethodException(BlockObjectType.FullName, "MarkAsFinished()");
        HarmonyBridge.PatchPrefix(markFinished, typeof(Diagnostic).GetMethod(nameof(BeforeMarkAsFinished), AnyStatic)!);

        Log.Write("[SUPPORTDIAG] Diagnostic installed.");
        Log.Write("[SUPPORTDIAG] Observer hooks only; no args/results/flow are modified.");
        Log.Write("[SUPPORTDIAG] Sampler hook: " + tick.DeclaringType?.FullName + "." + tick.Name);
        Log.Write("[SUPPORTDIAG] Event hooks: ConstructionSite.IncreaseBuildTime + BlockObject.MarkAsFinished");
        DumpConstructionPatchOwners();
    }

    public static void AfterEntityRegistered(object __0)
    {
        TrackEntity(__0, add: true);
    }

    public static void AfterEntityUnregistered(object __0)
    {
        TrackEntity(__0, add: false);
    }

    static void TrackEntity(object entityComponent, bool add)
    {
        try
        {
            object registered = GetMember(entityComponent, "RegisteredComponents");
            foreach (object component in Enumerate(registered))
            {
                if (!ConstructionSiteType.IsInstanceOfType(component))
                    continue;

                if (add)
                    ConstructionSites.Add(component);
                else
                    ConstructionSites.Remove(component);
            }
        }
        catch (Exception ex)
        {
            Log.Write("[SUPPORTDIAG] Registry tracking error: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    public static void BeforeIncreaseBuildTime(object __instance, float __0)
    {
        try
        {
            ObserveConstructionProgress(__instance, "IncreaseBuildTime(" + __0 + ")");
        }
        catch (Exception ex)
        {
            Log.Write("[SUPPORTDIAG] IncreaseBuildTime observer error: " + ex);
        }
    }

    public static void BeforeMarkAsFinished(object __instance)
    {
        try
        {
            object? site = GetComponent(__instance, ConstructionSiteType);
            if (site == null)
                return;

            ObserveConstructionProgress(site, "BlockObject.MarkAsFinished");
        }
        catch (Exception ex)
        {
            Log.Write("[SUPPORTDIAG] MarkAsFinished observer error: " + ex);
        }
    }

    static void ObserveConstructionProgress(object site, string eventName)
    {
        object upper;
        try { upper = GetField(site, "_blockObject"); }
        catch { return; }

        GroundingCheck check;
        try { check = RecheckGroundingDetailed(upper); }
        catch (Exception ex)
        {
            Log.Write("[SUPPORTDIAG] EVENT " + eventName + " grounding recheck failed for "
                + DescribeBlockObject(upper) + ": " + ex);
            return;
        }

        if (!check.HasSolidFoundation || check.AllGrounded)
            return;

        var validators = GetValidators(site);
        TryGetDirectIncompleteSupports(site, upper, out var supports);

        Log.Write("[SUPPORTDIAG] ===== INVALID CONSTRUCTION EVENT =====");
        Log.Write("[SUPPORTDIAG] Event=" + eventName);
        Log.Write(BuildReport(site, upper, supports, validators, check.Details, true));
        Log.Write("[SUPPORTDIAG] Call stack:" + Environment.NewLine + Environment.StackTrace);
        Log.Write("[SUPPORTDIAG] ===== END INVALID CONSTRUCTION EVENT =====");
    }

    public static void AfterUnrelatedWorldTick()
    {
        long now = Clock.ElapsedMilliseconds;
        if (now - lastScanMs < 100)
            return;

        lastScanMs = now;

        if (!dumpedFinalPatchMap)
        {
            dumpedFinalPatchMap = true;
            Log.Write("[SUPPORTDIAG] Final construction Harmony map after mod/world initialization:");
            foreach (string line in HarmonyOwnersForRelevantMethods())
                Log.Write("[SUPPORTDIAG]   " + line);
            Log.Write("[SUPPORTDIAG] Tracked ConstructionSite count at first scan: " + ConstructionSites.Count);
        }

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
        object[] sites = ConstructionSites.ToArray();

        foreach (object site in sites)
        {
            object upper;
            try { upper = GetField(site, "_blockObject"); }
            catch { continue; }

            if (!SafeBool(upper, "IsUnfinished"))
                continue;

            bool isOn = SafeBool(site, "IsOn");
            bool ready = SafeBool(site, "ReadyToBuild");
            bool readyFinish = SafeBool(site, "IsReadyToFinish");

            // Actual construction can only happen while IsOn is true. Keep the hot-path
            // cheap and only do the expensive independent grounding check for sites that
            // vanilla currently considers enabled/buildable.
            if (!isOn && !ready && !readyFinish)
                continue;

            var validators = GetValidators(site);
            string groundedTypeName = GroundedConstructionSiteType.FullName ?? "Timberborn.ConstructionSites.GroundedConstructionSite";
            bool groundedPresent = validators.Any(v => v.TypeName.StartsWith(groundedTypeName, StringComparison.Ordinal));
            bool groundedValid = validators.Any(v => v.TypeName.StartsWith(groundedTypeName, StringComparison.Ordinal) && v.IsValid);

            GroundingCheck check;
            try { check = RecheckGroundingDetailed(upper); }
            catch (Exception ex)
            {
                Log.Write("[SUPPORTDIAG] Recheck error for " + DescribeBlockObject(upper) + ": " + ex);
                continue;
            }

            // Buildings with no solid-matter foundation requirement are irrelevant.
            if (!check.HasSolidFoundation)
                continue;

            bool anomaly = !check.AllGrounded || !groundedPresent || (groundedPresent && groundedValid != check.AllGrounded);
            if (!anomaly)
                continue;

            int id = GetObjectId(site);
            string signature =
                "isOn=" + isOn +
                "|ready=" + ready +
                "|readyFinish=" + readyFinish +
                "|groundedPresent=" + groundedPresent +
                "|groundedValid=" + groundedValid +
                "|allGrounded=" + check.AllGrounded +
                "|details=" + check.Details;

            if (LastState.TryGetValue(id, out string? previous) && previous == signature)
                continue;

            LastState[id] = signature;

            TryGetDirectIncompleteSupports(site, upper, out var supports);
            Log.Write(BuildReport(site, upper, supports, validators, check.Details, !check.AllGrounded));
        }
    }

    static GroundingCheck RecheckGroundingDetailed(object upper)
    {
        object? grounded = GetComponent(upper, GroundedConstructionSiteType);
        if (grounded == null)
        {
            // We can still determine whether the template has a solid foundation requirement.
            int baseZ0 = GetInt(GetMember(upper, "CoordinatesAtBaseZ"), "z");
            object positioned0 = GetMember(upper, "PositionedBlocks");
            bool hasSolid0 = false;
            foreach (object block in Enumerate(InvokeNoArgs(positioned0, "GetOccupiedBlocks")))
            {
                object coords = GetMember(block, "Coordinates");
                if (GetInt(coords, "z") != baseZ0)
                    continue;
                string matter = Convert.ToString(GetMember(block, "MatterBelow")) ?? "";
                if (matter == "Ground" || matter == "GroundOrStackable" || matter == "Stackable")
                {
                    hasSolid0 = true;
                    break;
                }
            }
            return new GroundingCheck(hasSolid0, false, "GroundedConstructionSite component missing");
        }

        object mv = GetField(grounded, "_matterBelowValidator");
        MethodInfo normal = MatterBelowValidatorType.GetMethods(AnyInstance)
            .Single(m => m.Name == "Validate" && m.GetParameters().Length == 1);
        MethodInfo ignore = MatterBelowValidatorType.GetMethods(AnyInstance)
            .Single(m => m.Name == "ValidateIgnoringUnfinishedStackable" && m.GetParameters().Length == 1);

        int baseZ = GetInt(GetMember(upper, "CoordinatesAtBaseZ"), "z");
        object positioned = GetMember(upper, "PositionedBlocks");

        var pieces = new List<string>();
        bool all = true;
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
            all &= ig;
            pieces.Add("#" + (++i)
                + " matter=" + matterBelow
                + " normal=" + n
                + " ignoreUnfinished=" + ig
                + " at " + FormatCoords(coords));
        }

        return new GroundingCheck(
            pieces.Count > 0,
            pieces.Count == 0 || all,
            pieces.Count == 0 ? "no solid-matter foundation blocks found" : string.Join("; ", pieces));
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

        lines.Add("[SUPPORTDIAG] Validators:");
        foreach (var v in validators)
            lines.Add("[SUPPORTDIAG]   - " + v.TypeName + " IsValid=" + v.IsValid);

        string groundedTypeName = GroundedConstructionSiteType.FullName ?? "Timberborn.ConstructionSites.GroundedConstructionSite";
        if (validators.All(v => !v.TypeName.StartsWith(groundedTypeName, StringComparison.Ordinal)))
            lines.Add("[SUPPORTDIAG]   !!! GroundedConstructionSite validator MISSING");

        object? groundedComponent = GetComponent(upper, GroundedConstructionSiteType);
        lines.Add("[SUPPORTDIAG] Grounded component present=" + (groundedComponent != null)
            + (groundedComponent == null ? "" : " IsValid=" + SafeValue(groundedComponent, "IsValid")));

        lines.Add("[SUPPORTDIAG] Direct incomplete support(s):");
        if (supports.Count == 0)
            lines.Add("[SUPPORTDIAG]   - none detected by support enumerator");
        foreach (object support in supports)
        {
            object? ss = GetComponent(support, ConstructionSiteType);
            string extra = ss == null ? "" :
                " | supportSiteProgress=" + SafeValue(ss, "BuildTimeProgress")
                + " supportHours=" + SafeValue(ss, "BuildTimeProgressInHours");
            lines.Add("[SUPPORTDIAG]   - " + DescribeBlockObject(support) + extra);
        }

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

    static bool TryGetDirectIncompleteSupports(object site, object upper, out List<object> supports)
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
                if (ReferenceEquals(candidate, upper))
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

                bool unfinished = SafeBool(candidate, "IsUnfinished");
                object? candidateSite = GetComponent(candidate, ConstructionSiteType);
                bool progressIncomplete = false;
                if (candidateSite != null)
                {
                    try
                    {
                        double p = Convert.ToDouble(GetMember(candidateSite, "BuildTimeProgress"));
                        progressIncomplete = p < 0.999;
                    }
                    catch { }
                }

                if (!unfinished && !progressIncomplete)
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
        catch (Exception ex)
        {
            result.Add(new ValidatorState("<validator-list-error:" + ex.GetType().Name + ">", false));
            return result;
        }

        foreach (object v in Enumerate(validators))
        {
            bool isValid;
            string detail = "";
            try
            {
                isValid = Convert.ToBoolean(GetMember(v, "IsValid"));
                MethodInfo? validate = v.GetType().GetMethod("Validate", AnyInstance, null, Type.EmptyTypes, null);
                if (validate != null)
                    detail = " ValidateOwner=" + (validate.DeclaringType?.FullName ?? "?");
            }
            catch (Exception ex)
            {
                isValid = false;
                detail = " <read-error:" + ex.GetType().Name + ">";
            }

            result.Add(new ValidatorState(
                (v.GetType().FullName ?? v.GetType().Name) + detail,
                isValid));
        }

        return result;
    }

    static string DescribeBlockObject(object bo)
    {
        object? site = GetComponent(bo, ConstructionSiteType);
        string siteInfo = site == null
            ? " no ConstructionSite"
            : " progress=" + SafeValue(site, "BuildTimeProgress")
              + " hours=" + SafeValue(site, "BuildTimeProgressInHours")
              + " IsOn=" + SafeValue(site, "IsOn")
              + " ReadyToBuild=" + SafeValue(site, "ReadyToBuild")
            + " templateHint=" + SafeValue(bo, "Name");

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

    sealed class GroundingCheck
    {
        public bool HasSolidFoundation { get; }
        public bool AllGrounded { get; }
        public string Details { get; }

        public GroundingCheck(bool hasSolidFoundation, bool allGrounded, string details)
        {
            HasSolidFoundation = hasSolidFoundation;
            AllGrounded = allGrounded;
            Details = details;
        }
    }

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
        args[1] = hm;
        patch.Invoke(harmony, args);
    }

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
