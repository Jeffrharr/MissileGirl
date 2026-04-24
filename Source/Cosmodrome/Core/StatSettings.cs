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
using Verse;

namespace MissileGirl
{
    public class StatSettings : IExposable
    {
        public StatDef statDef;
        private string statDefName;

        public float expiryAfter;

        const int SETTINGS_VERSION = 1;

        private int version = -1;

        public StatSettings()
        {
        }

        public StatSettings(StatDef statDef)
        {
            this.statDef = statDef;
            this.expiryAfter = Tools.PredictStatExpiryFromString(statDef.defName);
        }

        // public void ExposeData()
        // {
        //     if (Scribe.mode == LoadSaveMode.Saving && statDef != null)
        //     {
        //         Resolve();
        //     }
        //     try
        //     {
        //         Log.Message("Attempting to load" + statDef);
        //         Scribe_Defs.Look(ref statDef, "statDef");
        //         if (Scribe.mode == LoadSaveMode.LoadingVars)
        //         {
        //             if (!statDef.defName.NullOrEmpty())
        //             {
        //                 statDef = DefDatabase<StatDef>.GetNamedSilentFail(statDef.defName);
        //             }
        //         }
        //
        //     }
        //     finally
        //     {
        //         Scribe_Values.Look(ref expiryAfter, "expiryAfter");
        //         Scribe_Values.Look(ref version, "version", -1);
        //         if (version != SETTINGS_VERSION)
        //             Notify_VersionChanged();
        //     }
        // }


        public void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving && statDef != null)
            {
                Resolve();
                statDefName = statDef.defName;
            }
            try
            {
                Scribe_Values.Look(ref statDefName, "statDef");
                if (Scribe.mode == LoadSaveMode.PostLoadInit)
                {
                    statDef = DefDatabase<StatDef>.GetNamedSilentFail(statDefName);
                }

            }
            finally
            {
                Scribe_Values.Look(ref expiryAfter, "expiryAfter");
                Scribe_Values.Look(ref version, "version", -1);
                if (version != SETTINGS_VERSION)
                {
                    Notify_VersionChanged();
                }
            }
        }

        private void Notify_VersionChanged()
        {
            version = SETTINGS_VERSION;
            expiryAfter = statDef?.label?.PredictStatExpiryFromString() ?? 240;
        }

        public void Prepare()
        {
            RocketStates.StatExpiry[statDef.index] = this.expiryAfter;
        }

        public void Resolve()
        {
            this.expiryAfter = RocketStates.StatExpiry[statDef.index];
        }
    }

    public class StatSettingsGroup : IExposable
    {
        public List<StatSettings> AllSettings = new List<StatSettings>();

        public void ExposeData()
        {
            Scribe_Collections.Look(ref AllSettings, "AllSettings", LookMode.Deep);

            if (AllSettings == null)
            {
                AllSettings = new List<StatSettings>();
            }
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                for (int i = AllSettings.Count - 1; i >= 0; i--)
                {
                    StatSettings settings = AllSettings[i];

                    if (settings == null || settings.statDef == null)
                    {
                        AllSettings.RemoveAt(i);
                    }
                }
            }
        }
    }
}
