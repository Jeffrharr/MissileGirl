// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using UnityEngine;
using Verse;

namespace MissileGirl
{
    public class Window_HiddenDebugMenu : Window
    {
        public const int count = 1;

        public Vector2 scrollPosition = Vector2.zero;

        public override Vector2 InitialSize => new Vector2(300, count * 70 + 25);

        public Window_HiddenDebugMenu()
        {
            draggable = true;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            resizeable = false;
            drawShadow = true;
            doCloseButton = false;
            doCloseX = true;
            layer = WindowLayer.Super;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect contentRect = new Rect(0, 0, inRect.width - 10, count * 60 + 5);
            GUIUtility.ExecuteSafeGUIAction(() =>
            {
                GUIFont.Font = GUIFontSize.Tiny;
                DoContent(new Rect(5, 5, contentRect.width - 5, 50));
            });
        }

        private void DoContent(Rect rect)
        {
            DoSingleTickIncrementsControls(rect);
            rect.y += 55;
        }

        private void DoSingleTickIncrementsControls(Rect inRect)
        {
            Widgets.DrawMenuSection(inRect.ExpandedBy(2));
            Widgets.CheckboxLabeled(inRect.TopHalf(), "Do singleTick increments controls", ref RocketStates.SingleTickIncrement);
            if (Widgets.ButtonText(inRect.BottomHalf(), "Do single tick"))
            {
                if (!RocketStates.SingleTickIncrement)
                {
                    RocketStates.SingleTickIncrement = true;
                }
                TickManager manager = Find.TickManager;
                RocketStates.SingleTickLeft += 1;
                if (!manager.Paused)
                    manager.Pause();
            }
        }
    }
}
