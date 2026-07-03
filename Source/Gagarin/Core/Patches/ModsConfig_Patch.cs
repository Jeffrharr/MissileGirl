// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Diagnostics;
using System.IO;
using JetBrains.Annotations;
using MissileGirl;
using Verse;

namespace Gagarin
{
    public static class ModsConfig_Patch
    {
        [GagarinPatch(typeof(ModsConfig), nameof(ModsConfig.Reset))]
        public static class ModsConfig_Reset_Patch
        {
            public static bool Prefix()
            {
                if (Directory.Exists(GagarinEnvironmentInfo.CacheFolderPath))
                {
                    Directory.Delete(GagarinEnvironmentInfo.CacheFolderPath, recursive: true);

                    Logger.Debug("GAGARIN: Removed cache to recover from error!");
                }
                // RimWorld calls ModsConfig.Reset() as part of its own play-data-load recovery
                // (Verse.PlayDataLoader), then immediately retries the load on the same pass.
                // Leaving the folder missing means the very next Cache/ write (e.g.
                // AssetHashingUtility.Dump) throws DirectoryNotFoundException, and Gagarin gives
                // up mid-retry -- collapsing the whole load instead of recovering from it.
                Directory.CreateDirectory(GagarinEnvironmentInfo.CacheFolderPath);

                Logger.Debug("GAGARIN: Recreated cache folder ahead of the retry pass.");
                if (File.Exists(RocketEnvironmentInfo.DevKeyFilePath))
                {
                    File.Delete(RocketEnvironmentInfo.DevKeyFilePath);

                    Logger.Debug("GAGARIN: Removed dev key to recover from error!");
                }
                return !RocketEnvironmentInfo.IsDevEnv;
            }

            public static void Postfix()
            {
                if (RocketEnvironmentInfo.IsDevEnv)
                {
                    Logger.Debug("GAGARIN: Restarting!");

                    GenCommandLine.Restart();
                }
            }
        }
    }
}
