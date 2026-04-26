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
using HarmonyLib;
using RimWorld;
using Verse;
namespace MissileGirl
{
    public static class KeyBinder
    {
        private static bool success;

        private static KeyBindingDef ToggleMissileGirl;

        private static KeyBindingDef ToggleAlerts;

        private static KeyBindingDef ToggleDebugging;

        private static KeyBindingDef ToggleSlowdowns;

        private static MethodBase mtarget = AccessTools.Method(typeof(UIRoot_Play), nameof(UIRoot_Play.UIRootOnGUI));

        private static MethodBase mOnGUI = AccessTools.Method(typeof(KeyBinder), nameof(OnGUI));

        [Main.OnDefsLoaded]
        public static void Start()
        {
            try
            {
                ToggleMissileGirl = KeyBindingDef.Named("RocketKeyBindingDisable");

                ToggleAlerts = KeyBindingDef.Named("RocketKeyToggleAlerts");

                ToggleDebugging = KeyBindingDef.Named("RocketKeyToggleDebugging");

                ToggleSlowdowns = KeyBindingDef.Named("RocketKeyToggleForcedSlowdowns");

                Finder.Harmony.Patch(mtarget, postfix: new HarmonyMethod(mOnGUI as MethodInfo));

                Logger.Message("MissileGirl: Patched KeyBinder!");

                success = true;
            }
            catch (Exception er)
            {
                Logger.Debug("MissileGirl: Failed to initialize the KeyBinder", exception: er);
            }
        }

        private static void OnGUI()
        {
            if (!success)
            {
                return;
            }
            try
            {
                if (ToggleMissileGirl.KeyDownEvent)
                {
                    RocketPrefs.Enabled = !RocketPrefs.Enabled;
                }
                if (ToggleAlerts.KeyDownEvent)
                {
                    RocketPrefs.DisableAllAlert = !RocketPrefs.DisableAllAlert;
                    if (RocketPrefs.DisableAllAlert)
                    {
                        List<Alert> alerts = (Find.UIRoot as UIRoot_Play)?.alerts?.AllAlerts;
                        if (alerts != null)
                        {
                            foreach (Alert alert in alerts)
                            {
                                alert.cachedActive = false;
                                alert.cachedLabel = string.Empty;
                            }
                        }
                    }
                }
                if (ToggleDebugging.KeyDownEvent)
                {
                    RocketDebugPrefs.Debug = !RocketDebugPrefs.Debug;
                }
                if (ToggleSlowdowns.KeyDownEvent)
                {
                    RocketPrefs.DisableForcedSlowdowns = !RocketPrefs.DisableForcedSlowdowns;
                }
            }
            catch (Exception er)
            {
                Logger.Debug("MissileGirl: KeyBinder failed!", exception: er);
            }
        }
    }
}
