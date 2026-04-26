// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using MissileGirl.Tabs;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
namespace MissileGirl
{
    public partial class RocketMod : Mod
    {
        private static RocketSettings Settings;
        public static RocketMod Instance;

        private const float ResetButtonWidth = 160f;
        private const float ResetButtonHeight = 40f;
        private static List<TabRecord> s_tabs;
        private static List<ITabContent> s_tabContents;
        private static int SelectedTab;

        public RocketMod(ModContentPack content) : base(content)
        {
            LongEventHandler.QueueLongEvent(() =>
            {
                Main.DefsLoaded();
            }, "MissileGirl.MissileGirl", doAsynchronously: false, exceptionHandler: null, showExtraUIInfo: true);

            LongEventHandler.QueueLongEvent(InitializeTabs, "MissileGirl_LongEvent_InitTabs", true, null);
            Finder.Mod = Instance = this;
            Finder.ModContentPack = content;

            if (!Directory.Exists(RocketEnvironmentInfo.CustomConfigFolderPath))
            {
                Directory.CreateDirectory(RocketEnvironmentInfo.CustomConfigFolderPath);
            }
            // Loading Settings

            Logger.Initialize();
            // Patch all core functions
            RocketStartupPatcher.PatchAll();
            // Program start here
            Finder.PluginsLoader = new RocketPluginsLoader();
            try
            {
                foreach (Assembly assembly in Finder.PluginsLoader.LoadAll())
                {
                    RocketAssembliesInfo.Assemblies.Add(assembly);
                    if (!content.assemblies.loadedAssemblies.Any(a => a.GetName().Name == assembly.GetName().Name))
                    {
                        content.assemblies.loadedAssemblies.Add(assembly);
                    }
                    if (Prefs.LogVerbose)
                    {
                        Logger.Message($"<color=orange>MissileGirl</color>: Loaded <color=red>{assembly.FullName}</color>");
                    }
                }
            }
            catch (Exception er)
            {
                Log.Error($"MissileGirl: loading plugin failed {er.Message}:{er.StackTrace}");
                Logger.Debug("Loading plugins failed", exception: er);
            }
            finally
            {
                RocketAssembliesInfo.Assemblies.AddRange(RocketAssembliesInfo.MissileGirlAssembliesInAppDomain);
                foreach (Assembly assembly in RocketAssembliesInfo.Assemblies)
                {
                    Logger.Debug($"Found in AppDomain after loading assembly {assembly.FullName}", file: "Assemblies.log");
                }
                Main.ReloadActions();
                foreach (Action action in Main.onInitialization)
                    action.Invoke();
            }

        }

        public override string SettingsCategory()
        {
            return "MissileGirl.ModNameShort".Translate();
        }

        private void InitializeTabs()
        {
            s_tabContents = [];
            foreach (Func<ITabContent> yield in Main.yieldModMenuTabContent ?? [])
            {
                try
                {
                    s_tabContents.Add(yield.Invoke());
                }
                catch (Exception er)
                {
                    Logger.Debug($"InitializeTabs: skipping tab, constructor threw {er.Message}", exception: er);
                }
            }

            s_tabs = [];
            for (int i = 0; i < s_tabContents.Count; i++)
            {
                int capturedIndex = i;
                s_tabs.Add(new TabRecord(s_tabContents[i].Label, delegate
                {
                    SelectedTab = capturedIndex;
                }, () => SelectedTab == capturedIndex));

            }
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);

            Rect windowRect = new Rect(inRect.x, inRect.y + 30f, inRect.width, inRect.height - 40f);
            Widgets.DrawMenuSection(windowRect);
            TabDrawer.DrawTabs(windowRect, s_tabs);

            if (s_tabContents != null && SelectedTab < s_tabContents.Count)
            {
                s_tabContents[SelectedTab].DoContent(windowRect);
            }

            Rect resetRect = new Rect(inRect.xMin + 200f, inRect.y + inRect.height + 5f, ResetButtonWidth, ResetButtonHeight);
            if (Widgets.ButtonText(resetRect, "FantasyOverhaul_ResetDefault".Translate()))
            {
                ResetToDefaults();
                SoundDefOf.Click.PlayOneShotOnCamera();
            }

            WriteSettings();
            GUIUtility.ClearGUIState();
        }

        private static void ResetToDefaults()
        {
            RocketPrefs.Enabled = true;
            RocketPrefs.MainButtonToggle = true;
            RocketPrefs.ShowWarmUpPopup = true;
            RocketPrefs.Learning = true;
            RocketPrefs.LearningAlertEnabled = true;
            RocketPrefs.StatGearCachingEnabled = true;
            RocketPrefs.FixBeauty = true;
            RocketPrefs.DeepDrillOptimize = true;
            RocketPrefs.TemperatureTickCheck = true;
            RocketPrefs.BuildingRepairCheck = true;
            RocketPrefs.NotifyPawnDamage = true;
            ResetRocketDebugPrefs();
        }

        public static void ResetRocketDebugPrefs()
        {
            RocketDebugPrefs.Debug = false;
            RocketDebugPrefs.LogData = false;
            RocketDebugPrefs.StatLogging = false;
            RocketDebugPrefs.FlashDilatedPawns = false;
            RocketDebugPrefs.AlwaysDilating = false;
            RocketStates.SingleTickIncrement = false;
        }
    }
}
