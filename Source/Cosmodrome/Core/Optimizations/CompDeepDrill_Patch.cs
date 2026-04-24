// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

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
