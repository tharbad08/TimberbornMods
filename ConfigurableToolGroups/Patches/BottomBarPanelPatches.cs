namespace ConfigurableToolGroups.Patches;

[HarmonyPatch(typeof(BottomBarPanel))]
public static class BottomBarPanelPatches
{
    private const float MinimapCompatibilityOffset = -120f;
    private const string MinimapAssemblyNameFragment = "Minimap";

    [HarmonyPostfix, HarmonyPatch(nameof(BottomBarPanel.Load))]
    public static void ApplyMinimapCompatibilityOffset(BottomBarPanel __instance)
    {
        if (!IsMinimapLoaded())
        {
            return;
        }

        __instance._mainElements.style.translate =
            new UnityEngine.UIElements.Translate(MinimapCompatibilityOffset, 0f);
    }

    private static bool IsMinimapLoaded()
    {
        foreach (var assembly in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var assemblyName = assembly.GetName().Name;
            if (assemblyName is not null &&
                assemblyName.IndexOf(MinimapAssemblyNameFragment, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    [HarmonyPrefix, HarmonyPatch(nameof(BottomBarPanel.InitializeLeftSection))]
    public static bool RearrangeLeftSection(BottomBarPanel __instance) => PatchSection(__instance, RootElementLocation.Left);

    [HarmonyPrefix, HarmonyPatch(nameof(BottomBarPanel.InitializeMiddleSection))]
    public static bool RearrangeMiddleSection(BottomBarPanel __instance) => PatchSection(__instance, RootElementLocation.Middle);

    [HarmonyPrefix, HarmonyPatch(nameof(BottomBarPanel.InitializeRightSection))]
    public static bool RearrangeRightSection(BottomBarPanel __instance) => PatchSection(__instance, RootElementLocation.Right);

    static bool PatchSection(BottomBarPanel instance, RootElementLocation location)
    {
        if (ModdableCustomToolButtonService.Instance is null)
        {
            throw new InvalidOperationException("ModdableRootElementService is not initialized.");
        }

        ModdableCustomToolButtonService.Instance.InitializeSection(instance, location);
        return false;
    }

}
