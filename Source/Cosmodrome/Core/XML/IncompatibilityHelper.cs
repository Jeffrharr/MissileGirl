// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace MissileGirl
{
    public static class IncompatibilityHelper
    {
        private static List<ModMetaData> metaDatabase = new List<ModMetaData>();

        private struct ModMetaData
        {
            public string name;
            public string packageId;
        }

        public static List<string> incompatibleMods = new List<string>();

        public static void Register(string name, string packageId)
        {
            metaDatabase.Add(new ModMetaData()
            {
                name = name,
                packageId = packageId
            });
        }

        public static void Prepare()
        {
            foreach (ModMetaData info in metaDatabase)
            {
                if (ModsConfig.ActiveModsInLoadOrder
                        .Any(m => (m.Name.ToLower() == info.name || m.PackageIdPlayerFacing.ToLower() == info.packageId || m.PackageIdNonUnique.ToLower() == info.packageId)))
                {
                    incompatibleMods.Add(info.name);
                    Log.Warning($"MissileGirl: Detected {info.name} ({info.packageId}) which is an incompatible mod!");
                }
            }
            if (incompatibleMods.Count > 0)
            {
                RocketEnvironmentInfo.IncompatibilityUnresolved = true;
                Log.Warning("MissileGirl: Incompatiblity found!");
            }
        }
    }
}
