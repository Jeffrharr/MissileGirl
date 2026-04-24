// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using RimWorld;
using UnityEngine;
using Verse;

namespace MissileGirl
{
    internal class MainButton_Toggle : MainButtonWorker
    {
        public override bool Disabled
        {
            get
            {
                this.def.buttonVisible = RocketPrefs.MainButtonToggle;
                return !RocketPrefs.MainButtonToggle
                        && Find.CurrentMap == null
                        && (!def.validWithoutMap || def == MainButtonDefOf.World) || Find.WorldRoutePlanner.Active
                        && Find.WorldRoutePlanner.FormingCaravan
                        && (!def.validWithoutMap || def == MainButtonDefOf.World);
            }
        }

        public override float ButtonBarPercent => RocketPrefs.MainButtonToggle ? base.ButtonBarPercent : 0f;

        public override void Activate()
        {
            if (Event.current.button == 0)
            {
                if (Find.WindowStack.WindowOfType<Window_Main>() != null)
                {
                    Find.WindowStack.RemoveWindowsOfType(typeof(Window_Main));
                    Finder.MissileGirlWindow = null;
                }
                else
                {
                    Find.WindowStack.Add(
                        Finder.MissileGirlWindow == null ? Finder.MissileGirlWindow = new Window_Main() : Finder.MissileGirlWindow);
                }
            }
            else
            {
                if (Find.WindowStack.WindowOfType<Window_Main>() == null)
                    Find.WindowStack.Add(
                        Finder.MissileGirlWindow == null ? Finder.MissileGirlWindow = new Window_Main() : Finder.MissileGirlWindow);
            }
        }
    }
}
