// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using RimWorld;
using MissileGirl;
using Verse.Noise;

namespace Proton
{
    public static class AlertsUtility
    {
        public static string GetName(this Alert alert)
        {
            string typeName = alert.GetType().Name;
            return typeName.Replace("Alert_", string.Empty).SplitStringByCapitalLetters();
        }

        public static string GetNameLower(this Alert alert)
        {
            return alert.GetName().ToLower();
        }
    }
}
