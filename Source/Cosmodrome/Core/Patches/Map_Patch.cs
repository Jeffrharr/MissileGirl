// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using HarmonyLib;
using Verse;
namespace MissileGirl.Patches
{
    [RocketStartupPatch(typeof(Map), nameof(Map.FinalizeInit))]
    public static class Map_FinalizeInit_Patch
    {
        [HarmonyPriority(int.MinValue)]
        public static void Postfix(Map __instance)
        {
            Main.MapLoaded(__instance);
        }
    }

    [RocketStartupPatch(typeof(Map), nameof(Map.ConstructComponents))]
    public static class Map_ConstructComponents_Patch
    {
        [HarmonyPriority(int.MinValue)]
        public static void Postfix(Map __instance)
        {
            Main.MapComponentsInitializing(__instance);
        }
    }
}
