// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;
namespace MissileGirl.Patches
{
    [RocketStartupPatch(typeof(Game), nameof(Game.FinalizeInit))]
    public static class Game_FinalizeInit_Patch
    {
        [HarmonyPriority(int.MinValue)]
        public static void Postfix()
        {
            Main.WorldLoaded();
        }
    }

    [RocketStartupPatch(typeof(Game), nameof(Game.DeinitAndRemoveMap))]
    public static class Game_DeinitAndRemoveMap_Patch
    {
        [HarmonyPriority(int.MinValue)]
        public static void Postfix(Map map)
        {
            Main.MapDiscarded(map);
        }
    }

    [RocketStartupPatch(typeof(Game), nameof(Game.UpdatePlay))]
    public static class Game_UpdatePlay_Patch
    {
        [HarmonyPriority(Priority.First)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Postfix()
        {
            RocketStates.Context = ContextFlag.Updating;
        }
    }
}
