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
using System.Runtime.CompilerServices;
using RimWorld;
using Verse;

namespace MissileGirl
{
    [RocketPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.DoMainMenuControls))]
    public static class MainMenuDrawer_DoMainMenuControls_Patch
    {
        private static bool finished = false;

        private static bool confirming = false;

        public static bool Prefix()
        {
            if (false
                    || Current.ProgramState == ProgramState.Playing
                    || finished)
                return true;
            if (IncompatibilityHelper.incompatibleMods.Count == 0)
                return finished = true;
            if (!confirming && !Find.WindowStack.windows.Any(w => w.GetType() == typeof(Dialog_MessageBox)
                                                                     && w is Dialog_MessageBox dialog
                                                                     && dialog.title == KeyedResources.MissileGirl_IncompatibilityWindow_Title))
            {
                ShowWarning();
            }
            return false;
        }

        private static void ShowWarning()
        {
            string description = KeyedResources.MissileGirl_IncompatibilityWindow_Description;
            for (int i = 0; i < IncompatibilityHelper.incompatibleMods.Count; i++)
            {
                description += $"\n\n<color=orange>{i + 1}.</color> {IncompatibilityHelper.incompatibleMods[i]}\n";
            }
            description += "\n<color=red>" + KeyedResources.MissileGirl_IncompatibilityWindow_Disclaimer + "</color>";
            Find.WindowStack.Add(new Dialog_MessageBox(description,
                                                       buttonBText: KeyedResources.MissileGirl_IncompatibilityWindow_OpenModManager, buttonBAction: () =>
                                                       {
                                                           finished = true;
                                                           Find.WindowStack.Add(new Page_ModsConfig());
                                                       },
                                                       buttonAText: KeyedResources.MissileGirl_IncompatibilityWindow_Continue, buttonAAction: () =>
                                                       {
                                                           confirming = true;
                                                           Dialog_MessageBox confirm = Dialog_MessageBox.CreateConfirmation(KeyedResources.MissileGirl_IncompatibilityWindow_Disclaimer, delegate
                                                           {
                                                               confirming = false;
                                                               finished = true;
                                                           }, destructive: true);
                                                           confirm.buttonBAction = () =>
                                                           {
                                                               confirming = false;
                                                               ShowWarning();
                                                           };
                                                           confirm.interactionDelay = 10;
                                                           Find.WindowStack.Add(confirm);
                                                       }, title: KeyedResources.MissileGirl_IncompatibilityWindow_Title, buttonADestructive: true)
                {
                    interactionDelay = 10,
                }
            );
        }
    }
}
