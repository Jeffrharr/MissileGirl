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
using Verse;

namespace MissileGirl
{
    [StaticConstructorOnStartup]
    public static class RocketPrefs
    {
        public static FieldInfo[] SettingsFields;

        public static bool WarmingUp
        {
            get => WarmUpMapComponent.settingsBeingStashed;
        }

        [Main.SettingsField(warmUpValue: false)]
        public static bool Enabled = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool Learning = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool FixBeauty = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool DeepDrillOptimize = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool TemperatureTickCheck = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool BuildingRepairCheck = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool NotifyPawnDamage = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool LearningAlertEnabled = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool AlertThrottling = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool DisableAllAlert = false;

        [Main.SettingsField(warmUpValue: false)]
        public static bool TranslationCaching = false;

        [Main.SettingsField(warmUpValue: false)]
        public static bool ThoughtsCaching = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool StatGearCachingEnabled = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool CorpsesRemovalEnabled = false;

        [Main.SettingsField(warmUpValue: true)]
        public static bool MainButtonToggle = true;

        [Main.SettingsField(warmUpValue: false)]
        public static bool DisableForcedSlowdowns = false;

        public static bool PauseAfterWarmup = false;

        public static bool ShowWarmUpPopup = true;

        public const float LearningRate = 0.0005f;
    }
}
