// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using Verse;
namespace MissileGirl.Optimizations
{
    [RocketPatch(typeof(GenTemperature), nameof(GenTemperature.ComfortableTemperatureRange), parameters = [typeof(Pawn)])]
    internal class GenTemperature_Patch
    {
        // ReSharper disable once FieldCanBeMadeReadOnly.Local
        private static Dictionary<int, FloatRange> tempCache = new Dictionary<int, FloatRange>();
        private static int LastTick;

        public static bool Prepare()
        {
            return RocketPrefs.TemperatureTickCheck;
        }

        public static bool Prefix(Pawn p, ref FloatRange __result)
        {

            if (LastTick != Find.TickManager.TicksGame)
            {
                LastTick = Find.TickManager.TicksGame;
                tempCache.Clear();
            }

            if (!tempCache.TryGetValue(p.thingIDNumber, out FloatRange result)) return true;
            __result = result;
            return false;

        }

        public static void Postfix(Pawn p, FloatRange __result)
        {
            // ReSharper disable once CanSimplifyDictionaryLookupWithTryAdd
            if (!tempCache.ContainsKey(p.thingIDNumber))
            {
                tempCache.Add(p.thingIDNumber, __result);
            }
        }
    }
}
