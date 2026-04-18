using RimWorld;

namespace MissileGirl.Optimizations
{
    [RocketPatch(typeof(CompDeepDrill), nameof(CompDeepDrill.CanDrillNow))]
    internal class CompDeepDrill_Patch
    {

        public static bool Prepare()
        {
            return RocketPrefs.DeepDrillOptimize;
        }

        public static bool Prefix(CompDeepDrill __instance, ref bool __result)
        {
            __result = (__instance.powerComp == null || __instance.powerComp.PowerOn) && (__instance.parent.Map.Biome.hasBedrock || __instance.ValuableResourcesPresent());
            return false;
        }
    }
}
