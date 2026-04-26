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
using MissileGirl.Tabs;
using UnityEngine;
using Verse;
using static MissileGirl.Main;

namespace MissileGirl
{
    public class Window_Main : Window
    {
        private TabHolder tabs;

        private int _errors;

        public override Vector2 InitialSize => new Vector2(685, 700);

        public Window_Main()
        {
            draggable = true;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            resizeable = true;
            drawShadow = true;
            doCloseButton = false;
            doCloseX = true;
            resizeLater = false;
            layer = WindowLayer.SubSuper;
            resizer = new WindowResizer();
            resizer.minWindowSize = new Vector2(InitialSize.x, 450);
            tabs = new TabHolder(new List<ITabContent>
            {
                new TabContent_Settings
                {
                    Selected = false
                },
            }, useSidebar: true);
            yieldTabContent = FunctionsUtility.GetFunctions<YieldTabContent, ITabContent>().ToList();
            for (int i = 0; i < yieldTabContent.Count; i++)
            {
                ITabContent tab = yieldTabContent[i].Invoke();
                Logger.Message($"MissileGirl: Found a new tab {tab.Label}");
                tabs.AddTab(tab);
            }
            Finder.MissileGirlWindow = this;
        }

        public override void DoWindowContents(Rect inRect)
        {
            GUIUtility.StashGUIState();
            Rect rect = inRect.TopPartPixels(25);
            try
            {
                // TODO fix this mess
                // For profiling reasons...
                RocketStates.LastFrame = Time.frameCount;
                // Actual work                                
                //GUI.color = Color.white;
                //// Create the MissileGirl stamp
                //GUIFont.Font = GameFont.Small;
                //Text.CurFontStyle.fontStyle = FontStyle.Bold;
                //Widgets.Label(rect, "MissileGirl");
                //// Create the version string
                //rect.xMin += 90;
                //rect.xMax -= 45;
                //rect.y += 2;
                //Text.CurFontStyle.fontStyle = FontStyle.Normal;
                //GUIFont.Font = GameFont.Tiny;
                //Widgets.Label(rect.TopPartPixels(25), $"Version <color=grey>{RocketAssembliesInfo.Version}</color>");
                //Widgets.DrawBoxSolid(new Rect(rect.position.x, rect.position.y + 23, rect.width, 1), Color.gray);
                //// Do the window content
                //inRect.yMin += 25;
                tabs.DoContent(inRect);
                // Reduce the error counter
                _errors = Math.Max(_errors - 1, 0);
            }
            catch (Exception er)
            {
                if (_errors <= 60 && _errors % 2 == 0) Log.Warning($"MissileGirl: UI Minor error:{er}\n{er.StackTrace}\nError count:{_errors}");
                else if (_errors <= 60) Log.Warning($"MissileGirl: UI error:{er}\n{er.StackTrace}\nError count:{_errors}");
                else Log.Error($"MissileGirl: UI Major error:{er}\n{er.StackTrace}\nError count:{_errors}");
                _errors += 3;
            }
            finally
            {
                GUIUtility.RestoreGUIState();
                GUIUtility.ClearGUIState();
            }
        }

        public override void Close(bool doCloseSound = true)
        {
            base.Close(doCloseSound);
            RocketDebugPrefs.LogData = false;
            Finder.MissileGirlWindow = null;
            if (!RocketPrefs.WarmingUp)
            {
                RocketMod.Instance.WriteSettings();
            }
        }
    }
}
