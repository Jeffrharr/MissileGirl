// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using MissileGirl;
using Verse;

namespace Gagarin
{
    public static class StartupHelper
    {
        [Main.OnInitialization]
        public static void StartUpStarted()
        {
            Context.RunningMods = LoadedModManager.RunningMods.ToList();
            Context.Core = LoadedModManager.RunningMods.First(m => m.IsCoreMod);

            if (!Directory.Exists(GagarinEnvironmentInfo.CacheFolderPath))
            {
                Directory.CreateDirectory(GagarinEnvironmentInfo.CacheFolderPath);
            }
            if (!Directory.Exists(GagarinEnvironmentInfo.TexturesFolderPath))
            {
                Directory.CreateDirectory(GagarinEnvironmentInfo.TexturesFolderPath);
            }
            if (Prefs.LogVerbose)
            {
                Log.Message("GAGARIN: <color=green>StartUpStarted called!</color>");
            }
            if (GagarinEnvironmentInfo.CacheExists)
            {
                if (Prefs.LogVerbose)
                {
                    Log.Warning("GAGARIN: <color=green>Cache found</color>");
                }

                Context.IsUsingCache = true;

                if (GagarinEnvironmentInfo.ModListChanged)
                {
                    Context.IsUsingCache = false;
                    Log.Warning("GAGARIN: Mod list changed! Deleting cache");
                }
            }
            if (!Context.IsUsingCache && GagarinPrefs.Enabled)
            {
                Log.Warning("GAGARIN: <color=green>Cache not found or got purged!</color>");
            }
            Logger.Message("GAGARIN: <color=green>Loading cache settings!</color>");
            RunningModsSetUtility.Dump(Context.RunningMods, GagarinEnvironmentInfo.ModListFilePath);

            GagarinSettings.LoadSettings();
            if (DateTime.Now.Subtract(GagarinPrefs.CacheCreationTime).Days >= 2)
            {
                GagarinPrefs.CacheCreationTime = default(DateTime);
                Context.IsUsingCache = false;
                Log.Warning("GAGARIN: Cache expired!");
                GagarinSettings.WriteSettings();
            }
            if (GagarinPrefs.Enabled)
            {
                GagarinPatcher.PatchAll();
            }
            else
            {
                Log.Message("GAGARIN: <color=red>Missile Girl's XML Caching is disabled!</color>");
            }
        }


        private static Assembly ResolveHandler(object sender, ResolveEventArgs e)
        {
            Log.Error($"MissileGirl: Trying to resolve {e.Name}");

            Logger.Debug($"MissileGirl: Trying to resolve {e.Name}", file: "ResolveHandler.log");

            return null;
        }

        [Main.OnStaticConstructor]
        public static void StartUpFinished()
        {
            if (Prefs.LogVerbose)
            {
                Log.Message("GAGARIN: <color=green>StartUpFinished called!</color>");
            }

            Context.Assets.Clear();
            Context.AssetsHashes.Clear();
            Context.AssetsHashesInt.Clear();
            Context.DefsXmlAssets.Clear();
            Context.XmlAssets.Clear();
            Context.CurrentLoadingMod = null;

            CachedDefHelper.Clean();
        }

    }
}
