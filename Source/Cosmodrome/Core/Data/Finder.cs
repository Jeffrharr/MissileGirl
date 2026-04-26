// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using HarmonyLib;
using RimWorld.Planet;
using Verse;
namespace MissileGirl
{
    public static class Finder
    {
        public static readonly string HarmonyID = "VR.MissileGirl";

        public static RocketMod Mod;

        public static ModContentPack ModContentPack;

        public static Harmony Harmony = new Harmony(HarmonyID);

        public static RocketShip.SkipperPatcher Rocket = new RocketShip.SkipperPatcher(HarmonyID);

        public static Window_Main MissileGirlWindow;

        public static RocketPluginsLoader PluginsLoader;

        public static StatSettingsGroup StatSettings;

        private static int _ticks = -1;
        private static World _world;
        /// <summary>
        /// Returns the ticks eplased since the game started/loaded.
        /// </summary>
        public static int SessionTicks
        {
            get
            {
                if (Current.Game == null)
                {
                    return 0;
                }
                World world = Find.World;
                if (world == null || Find.CurrentMap == null)
                {
                    return 0;
                }
                if (_world != world || _ticks == -1)
                {
                    _ticks = GenTicks.TicksGame;
                    _world = world;
                }
                return GenTicks.TicksGame - _ticks;
            }
        }
    }
}
