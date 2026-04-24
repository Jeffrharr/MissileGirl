// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using RimWorld;
using Verse;
using Verse.AI.Group;
namespace MissileGirl.Optimizations
{
    [RocketPatch(typeof(Lord), nameof(Lord.Notify_PawnDamaged))]
    internal class Lord_Notify_PawnDamaged_Patch
    {

        public static bool Prepare()
        {
            return RocketPrefs.NotifyPawnDamage;
        }

        public static bool Prefix(Pawn victim)
        {
            LordToil L = victim.GetLord().CurLordToil;
            return L is not LordToil_AssaultColony && L is not LordToil_AssaultColonySappers;

        }
    }
}
