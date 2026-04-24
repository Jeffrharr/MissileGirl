// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld.BaseGen;
using MissileGirl;
using Verse;

namespace Proton
{
    public static class Analyzer_H_AlertsReadoutUpdate_Patch
    {
        [Main.OnDefsLoaded]
        public static void Patch()
        {
            if (AccessTools.Method("H_AlertsReadoutUpdate:CheckAddOrRemoveAlert") == null)
                return;
            try
            {
                HarmonyMethod spiler = new HarmonyMethod(AccessTools.Method(typeof(Analyzer_H_AlertsReadoutUpdate_Patch), nameof(Analyzer_H_AlertsReadoutUpdate_Patch.Transpiler)));
                foreach (MethodBase method in TargetMethods())
                    Finder.Harmony.Patch(method, transpiler: spiler);
            }
            catch (Exception er)
            {
                Log.Error($"PROTON: Failed to patch the analyzer! {er}");
            }
        }

        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method("H_AlertsReadoutUpdate:CheckAddOrRemoveAlert");
            yield return AccessTools.Method("H_AlertsReadoutUpdate:AlertsReadoutOnGUI");
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            yield return new CodeInstruction(OpCodes.Ldc_I4_1);
            yield return new CodeInstruction(OpCodes.Ret);
        }
    }
}
