// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using MissileGirl;
using Verse;

namespace Proton
{
    [ProtonPatch]
    public class Alert_Constructor_Patch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (Type type in typeof(Alert).AllLeafSubclasses())
            {
                if (!(type == typeof(Alert_Custom)) && !(type == typeof(Alert_CustomCritical)))
                {
                    MethodBase target = AccessTools.Constructor(type);
                    if (target.IsValidTarget())
                        yield return target;
                }
            }
        }

        public static void Postfix(Alert __instance)
        {
            if (Context.TypeIdToSettings.TryGetValue(__instance.GetType().FullName, out AlertSettings settings))
            {
                settings.alert = __instance;
            }
        }
    }
}
