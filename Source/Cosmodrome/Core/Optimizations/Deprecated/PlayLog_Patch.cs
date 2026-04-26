// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using HarmonyLib;
using Verse;
namespace MissileGirl.Optimizations
{
    [RocketPatch(typeof(PlayLog), nameof(PlayLog.ReduceToCapacity))]
    public class PlayLog_ReduceToCapacity_Patch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (CodeInstruction code in instructions)
            {
                if (code.OperandIs(150))
                    code.operand = 75;
                yield return code;
            }
        }
    }
}
