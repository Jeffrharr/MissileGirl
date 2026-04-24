// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Gagarin
{
    [GagarinPatch(typeof(TKeySystem), nameof(TKeySystem.Parse))]
    public static class TKeySystem_Profiler
    {
        private static Stopwatch stopwatch = new Stopwatch();

        [HarmonyPriority(1000)]
        public static void Prefix()
        {
            stopwatch.Reset();
            stopwatch.Start();
        }

        [HarmonyPriority(Priority.Last)]
        public static void Postfix()
        {
            stopwatch.Stop();
            if (Prefs.LogVerbose)
            {
                Log.Warning($"GAGARIN: <color=white>TKeySystem.Parse</color> took <color=red>{Math.Round((float)stopwatch.ElapsedMilliseconds / 1000f, 4)}</color> seconds");
            }
        }
    }
}
