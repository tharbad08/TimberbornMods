using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Timberborn.ModManagerScene;

namespace ConstructionSupportGuard;

public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        try { Guard.Install(); }
        catch (Exception ex) { Log.Write("[SUPPORTGUARD] Failed to install: " + ex); }
    }
}

internal static class Guard
{
    const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static Type ConstructionSiteType = null!;
    static readonly HashSet<int> LoggedBlockedSites = new();

    public static void Install()
    {
        ConstructionSiteType = Type.GetType(
            "Timberborn.ConstructionSites.ConstructionSite, Timberborn.ConstructionSites",
            throwOnError: true)!;

        MethodInfo ready = ConstructionSiteType.GetProperty("ReadyToBuild", AnyInstance)?.GetGetMethod(true)
            ?? throw new MissingMethodException(ConstructionSiteType.FullName, "get_ReadyToBuild");
        MethodInfo finish = ConstructionSiteType.GetProperty("IsReadyToFinish", AnyInstance)?.GetGetMethod(true)
            ?? throw new MissingMethodException(ConstructionSiteType.FullName, "get_IsReadyToFinish");
        MethodInfo increase = ConstructionSiteType.GetMethod(
            "IncreaseBuildTime", AnyInstance, null, new[] { typeof(float) }, null)
            ?? throw new MissingMethodException(ConstructionSiteType.FullName, "IncreaseBuildTime(float)");

        HarmonyBridge.PatchPostfix(ready, typeof(Guard).GetMethod(nameof(AfterReadyToBuild), AnyStatic)!);
        HarmonyBridge.PatchPostfix(finish, typeof(Guard).GetMethod(nameof(AfterIsReadyToFinish), AnyStatic)!);
        HarmonyBridge.PatchPrefix(increase, typeof(Guard).GetMethod(nameof(BeforeIncreaseBuildTime), AnyStatic)!);

        Log.Write("[SUPPORTGUARD] Installed.");
        Log.Write("[SUPPORTGUARD] ReadyToBuild/IncreaseBuildTime/IsReadyToFinish guarded.");
        Log.Write("[SUPPORTGUARD] Terrain-like UnfinishedGround construction also obeys Timberborn terrain physics.");
        Log.Write("[SUPPORTGUARD] IsOn is intentionally untouched so material delivery remains available.");
    }

    public static void AfterReadyToBuild(object __instance, ref bool __result)
    {
        if (__result && HasUnsafeSupport(__instance, out _))
            __result = false;
    }

    public static void AfterIsReadyToFinish(object __instance, ref bool __result)
    {
        if (__result && HasUnsafeSupport(__instance, out _))
            __result = false;
    }

    public static bool BeforeIncreaseBuildTime(object __instance)
    {
        if (!HasUnsafeSupport(__instance, out string detail))
        {
            LoggedBlockedSites.Remove(GetObjectId(__instance));
            return true;
        }

        int id = GetObjectId(__instance);
        if (LoggedBlockedSites.Add(id))
        {
            object? upper = TryGetField(__instance, "_blockObject");
            Log.Write("[SUPPORTGUARD] Waiting: "
                + (upper == null ? "<unknown>" : SafeValue(upper, "Name") + " at " + SafeValue(upper, "Coordinates"))
                + " | " + detail);
        }
        return false;
    }

    static bool HasUnsafeSupport(object site, out string detail)
    {
        if (HasUnfinishedDirectSupport(site, out detail))
            return true;
        if (ViolatesTerrainPhysics(site, out detail))
            return true;
        detail = "";
        return false;
    }

    static bool HasUnfinishedDirectSupport(object site, out string detail)
    {
        detail = "";
        object? upper = TryGetField(site, "_blockObject");
        if (upper == null) return false;

        object positioned;
        object blockService;
        int baseZ;
        try
        {
            positioned = GetMember(upper, "PositionedBlocks");
            blockService = GetField(upper, "_blockService");
            baseZ = GetInt(GetMember(upper, "CoordinatesAtBaseZ"), "z");
        }
        catch { return false; }

        foreach (object upperBlock in Enumerate(InvokeNoArgs(positioned, "GetOccupiedBlocks")))
        {
            object coords;
            try { coords = GetMember(upperBlock, "Coordinates"); }
            catch { continue; }
            if (GetInt(coords, "z") != baseZ) continue;

            object below = CreateBelow(coords);
            object objectsAt;
            try { objectsAt = InvokeOneArg(blockService, "GetObjectsAt", below); }
            catch { continue; }

            bool sawStackable = false;
            bool sawFinishedStackable = false;
            object? unfinished = null;

            foreach (object candidate in Enumerate(objectsAt))
            {
                if (ReferenceEquals(candidate, upper)) continue;

                object candidateBlock;
                try
                {
                    object candidateBlocks = GetMember(candidate, "PositionedBlocks");
                    candidateBlock = InvokeOneArg(candidateBlocks, "GetBlock", below);
                }
                catch { continue; }

                string stackable = SafeValue(candidateBlock, "Stackable");
                if (string.Equals(stackable, "None", StringComparison.OrdinalIgnoreCase)) continue;

                sawStackable = true;
                if (SafeBool(candidate, "IsFinished"))
                {
                    sawFinishedStackable = true;
                    break;
                }

                if (SafeBool(candidate, "IsUnfinished") && unfinished == null)
                    unfinished = candidate;
            }

            if (sawStackable && !sawFinishedStackable && unfinished != null)
            {
                detail = "direct support " + SafeValue(unfinished, "Name")
                    + " at " + SafeValue(unfinished, "Coordinates")
                    + " is unfinished; upper base=" + FormatCoords(coords);
                return true;
            }
        }
        return false;
    }

    static bool ViolatesTerrainPhysics(object site, out string detail)
    {
        detail = "";
        object? upper = TryGetField(site, "_blockObject");
        if (upper == null) return false;

        object positioned;
        try { positioned = GetMember(upper, "PositionedBlocks"); }
        catch { return false; }

        bool terrainLike = false;
        try
        {
            foreach (object block in Enumerate(InvokeNoArgs(positioned, "GetAllBlocks")))
            {
                if (string.Equals(SafeValue(block, "Stackable"), "UnfinishedGround", StringComparison.OrdinalIgnoreCase))
                {
                    terrainLike = true;
                    break;
                }
            }
        }
        catch { return false; }

        if (!terrainLike) return false;

        object? updater = null;
        try
        {
            foreach (object component in Enumerate(GetMember(upper, "AllComponents")))
            {
                if (component.GetType().FullName == "Timberborn.ConstructionSites.PhysicallySupportedConstructionSiteUpdater")
                {
                    updater = component;
                    break;
                }
            }
        }
        catch { return false; }

        if (updater == null) return false;

        object terrainPhysics;
        object coords;
        try
        {
            terrainPhysics = GetField(updater, "_terrainPhysicsService");
            coords = GetMember(upper, "Coordinates");
        }
        catch { return false; }

        bool valid;
        try { valid = Convert.ToBoolean(InvokeOneArg(terrainPhysics, "CanTerrainBeAdded", coords)); }
        catch { return false; }

        if (valid) return false;

        detail = "terrain-like support limit exceeded at " + FormatCoords(coords)
            + " (using Timberborn terrain physics / MaxSupportDistance=3)";
        return true;
    }

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

    static object? TryGetField(object instance, string name)
    {
        try { return GetField(instance, name); }
        catch { return null; }
    }

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

    static string SafeValue(object instance, string name)
    {
        try { return GetMember(instance, name)?.ToString() ?? "null"; }
        catch { return "<unknown>"; }
    }

    static bool SafeBool(object instance, string name)
    {
        try { return Convert.ToBoolean(GetMember(instance, name)); }
        catch { return false; }
    }

    static int GetInt(object instance, string name) => Convert.ToInt32(GetMember(instance, name));

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
}

internal static class HarmonyBridge
{
    static readonly Type HarmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: true)!;
    static readonly Type HarmonyMethodType = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", throwOnError: true)!;

    public static void PatchPrefix(MethodBase original, MethodInfo patchMethodInfo)
        => Patch(original, patchMethodInfo, true);

    public static void PatchPostfix(MethodBase original, MethodInfo patchMethodInfo)
        => Patch(original, patchMethodInfo, false);

    static void Patch(MethodBase original, MethodInfo patchMethodInfo, bool prefix)
    {
        object harmony = Activator.CreateInstance(HarmonyType, "ConstructionSupportGuard")!;
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
