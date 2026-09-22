namespace ExtendedBuilderReach.Patches;

/// <summary>
/// Extended upward builder reach can make a construction site reachable before the
/// stackable structure supporting it has finished. Keep preview placement and material
/// delivery unchanged, but prevent hammering/finishing until the required support is done.
/// </summary>
[HarmonyPatch(typeof(ConstructionSite))]
public static class ConstructionSiteSupportPatch
{
    [HarmonyPostfix, HarmonyPatch(nameof(ConstructionSite.ReadyToBuild), MethodType.Getter)]
    public static void PreventBuildingOnUnfinishedSupport(ConstructionSite __instance, ref bool __result)
        => ApplyUnfinishedSupportGuard(__instance, ref __result);

    [HarmonyPostfix, HarmonyPatch(nameof(ConstructionSite.IsReadyToFinish), MethodType.Getter)]
    public static void PreventFinishingOnUnfinishedSupport(ConstructionSite __instance, ref bool __result)
        => ApplyUnfinishedSupportGuard(__instance, ref __result);

    static void ApplyUnfinishedSupportGuard(ConstructionSite constructionSite, ref bool result)
    {
        if (!result || (!MSettings.UnlimitedAbove && MSettings.RangeAbove <= 1))
        {
            return;
        }

        var blockObject = constructionSite._blockObject;
        var groundedConstructionSite = constructionSite.GetComponent<GroundedConstructionSite>();
        if (groundedConstructionSite is null)
        {
            return;
        }

        var matterBelowValidator = groundedConstructionSite._matterBelowValidator;
        var baseZ = blockObject.CoordinatesAtBaseZ.z;

        foreach (var block in blockObject.PositionedBlocks.GetOccupiedBlocks())
        {
            if (block.Coordinates.z != baseZ || !block.MatterBelow.IsSolidMatter())
            {
                continue;
            }

            var foundationBlock = block;

            // Normal placement validation intentionally accepts unfinished supports.
            // If the same block becomes invalid when unfinished supports are ignored,
            // this construction site currently depends on one.
            if (matterBelowValidator.Validate(in foundationBlock)
                && !matterBelowValidator.ValidateIgnoringUnfinishedStackable(in foundationBlock))
            {
                result = false;
                return;
            }
        }
    }
}
