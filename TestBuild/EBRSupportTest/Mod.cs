using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using Timberborn.ModManagerScene;

namespace EBRSupportTest;

public sealed class ModStarter : IModStarter
{
    public void StartMod(IModEnvironment modEnvironment)
    {
        try
        {
            Type constructionSiteType = Type.GetType(
                "Timberborn.ConstructionSites.ConstructionSite, Timberborn.ConstructionSites",
                throwOnError: true)!;

            MethodInfo readyToBuild = constructionSiteType
                .GetProperty("ReadyToBuild", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetGetMethod(nonPublic: true)!;

            MethodInfo isReadyToFinish = constructionSiteType
                .GetProperty("IsReadyToFinish", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
                .GetGetMethod(nonPublic: true)!;

            MethodInfo postfix = typeof(SupportGuard)
                .GetMethod(nameof(SupportGuard.Postfix), BindingFlags.Static | BindingFlags.Public)!;

            HarmonyBridge.PatchPostfix(readyToBuild, postfix);
            HarmonyBridge.PatchPostfix(isReadyToFinish, postfix);

            Console.WriteLine("[EBRSupportTest] Unfinished-support guard installed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EBRSupportTest] Failed to install patch: {ex}");
        }
    }
}

public static class SupportGuard
{
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    public static void Postfix(object __instance, ref bool __result)
    {
        if (!__result)
        {
            return;
        }

        try
        {
            object blockObject = GetField(__instance, "_blockObject");

            // ConstructionSite exists only while unfinished, but keep this explicit
            // so already-completed buildings can never be affected by this patch.
            if (!(bool)GetMember(blockObject, "IsUnfinished"))
            {
                return;
            }

            if (DependsOnUnfinishedSupport(blockObject))
            {
                __result = false;
            }
        }
        catch (Exception ex)
        {
            // Fail open: if a future Timberborn update changes internals, do not
            // globally block construction.
            Console.WriteLine($"[EBRSupportTest] Support check failed: {ex.Message}");
        }
    }

    private static bool DependsOnUnfinishedSupport(object blockObject)
    {
        int baseZ = GetIntMember(GetMember(blockObject, "CoordinatesAtBaseZ"), "z");
        object positionedBlocks = GetMember(blockObject, "PositionedBlocks");
        object occupiedBlocks = InvokeNoArgs(positionedBlocks, "GetOccupiedBlocks");
        object blockService = GetField(blockObject, "_blockService");

        foreach (object block in Enumerate(occupiedBlocks))
        {
            object coordinates = GetMember(block, "Coordinates");
            if (GetIntMember(coordinates, "z") != baseZ)
            {
                continue;
            }

            string matterBelow = GetMember(block, "MatterBelow").ToString() ?? string.Empty;
            if (matterBelow != "Ground"
                && matterBelow != "GroundOrStackable"
                && matterBelow != "Stackable")
            {
                continue;
            }

            object below = CreateCoordinatesBelow(coordinates);
            object objectsAt = InvokeOneArg(blockService, "GetObjectsAt", below);

            foreach (object support in Enumerate(objectsAt))
            {
                if (!(bool)GetMember(support, "IsUnfinished"))
                {
                    continue;
                }

                object supportBlocks = GetMember(support, "PositionedBlocks");
                object supportBlock = InvokeOneArg(supportBlocks, "GetBlock", below);
                string stackable = GetMember(supportBlock, "Stackable").ToString() ?? "None";

                if (stackable != "None")
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static object CreateCoordinatesBelow(object coordinates)
    {
        Type type = coordinates.GetType();
        int x = GetIntMember(coordinates, "x");
        int y = GetIntMember(coordinates, "y");
        int z = GetIntMember(coordinates, "z") - 1;
        return Activator.CreateInstance(type, x, y, z)!;
    }

    private static int GetIntMember(object instance, string name)
    {
        return Convert.ToInt32(GetMember(instance, name));
    }

    private static object GetField(object instance, string name)
    {
        FieldInfo? field = instance.GetType().GetField(name, InstanceFlags);
        if (field == null)
        {
            throw new MissingFieldException(instance.GetType().FullName, name);
        }
        return field.GetValue(instance)!;
    }

    private static object GetMember(object instance, string name)
    {
        Type type = instance.GetType();

        PropertyInfo? property = type.GetProperty(name, InstanceFlags);
        if (property != null)
        {
            return property.GetValue(instance)!;
        }

        FieldInfo? field = type.GetField(name, InstanceFlags);
        if (field != null)
        {
            return field.GetValue(instance)!;
        }

        throw new MissingMemberException(type.FullName, name);
    }

    private static object InvokeNoArgs(object instance, string name)
    {
        MethodInfo? method = instance.GetType().GetMethods(InstanceFlags)
            .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == 0);

        if (method == null)
        {
            throw new MissingMethodException(instance.GetType().FullName, name);
        }

        return method.Invoke(instance, null)!;
    }

    private static object InvokeOneArg(object instance, string name, object argument)
    {
        MethodInfo? method = instance.GetType().GetMethods(InstanceFlags)
            .FirstOrDefault(m =>
            {
                if (m.Name != name)
                {
                    return false;
                }

                ParameterInfo[] parameters = m.GetParameters();
                return parameters.Length == 1
                    && parameters[0].ParameterType.IsInstanceOfType(argument);
            });

        if (method == null)
        {
            throw new MissingMethodException(instance.GetType().FullName, name);
        }

        return method.Invoke(instance, new[] { argument })!;
    }

    private static IEnumerable Enumerate(object value)
    {
        if (value is IEnumerable enumerable)
        {
            return enumerable;
        }

        throw new InvalidOperationException($"{value.GetType().FullName} is not enumerable.");
    }
}

internal static class HarmonyBridge
{
    public static void PatchPostfix(MethodBase original, MethodInfo postfix)
    {
        Type harmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: true)!;
        Type harmonyMethodType = Type.GetType("HarmonyLib.HarmonyMethod, 0Harmony", throwOnError: true)!;

        object harmony = Activator.CreateInstance(harmonyType, "EBRSupportTest")!;
        object harmonyPostfix = Activator.CreateInstance(harmonyMethodType, postfix)!;

        MethodInfo patchMethod = harmonyType
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "Patch")
            .First(m =>
            {
                ParameterInfo[] p = m.GetParameters();
                return p.Length >= 3 && typeof(MethodBase).IsAssignableFrom(p[0].ParameterType);
            });

        ParameterInfo[] parameters = patchMethod.GetParameters();
        object?[] args = new object?[parameters.Length];
        args[0] = original;

        // Harmony.Patch(original, prefix, postfix, transpiler, finalizer, ...)
        // postfix is parameter index 2 on Harmony 2.x.
        args[2] = harmonyPostfix;

        patchMethod.Invoke(harmony, args);
    }
}
