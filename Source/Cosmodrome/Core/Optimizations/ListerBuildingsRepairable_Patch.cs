// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using RimWorld;
using Verse;

namespace MissileGirl.Optimizations
{
    [RocketPatch(typeof(ListerBuildingsRepairable), nameof(ListerBuildingsRepairable.UpdateBuilding))]
    internal class ListerBuildingsRepairable_Patch
    {
        public static bool Prepare()
        {
            return RocketPrefs.BuildingRepairCheck;
        }
        public static bool Prefix(Building b)
        {
            return b.def.building.repairable && b.def.useHitPoints;
        }
    }
}
