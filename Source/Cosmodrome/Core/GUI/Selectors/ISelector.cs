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
    public abstract class ISelector : Window
    {
        protected readonly Action closeAction;

        protected bool integrated;

        public Vector2 scrollPosition = Vector2.zero;

        public ISelector(bool integrated = false, Action closeAction = null)
        {
            this.integrated = integrated;
            this.closeAction = closeAction;
        }

        public override void DoWindowContents(Rect inRect)
        {
            integrated = false;
            GUIUtility.ExecuteSafeGUIAction(() =>
            {
                if (Widgets.ButtonText(inRect.BottomPartPixels(30), KeyedResources.MissileGirl_Close))
                {
                    Close();
                }
                inRect.yMax -= 35;
                DoContent(inRect);
            });
        }

        public void DoIntegratedContents(Rect inRect)
        {
            GUIUtility.ExecuteSafeGUIAction(() =>
            {
                integrated = true;
                DoContent(inRect);
            });
        }

        public abstract void DoContent(Rect inRect);

        public override void Close(bool doCloseSound = true)
        {
            if (!integrated)
                base.Close(doCloseSound);
            else
                closeAction.Invoke();
        }
    }
}
