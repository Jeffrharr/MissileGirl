// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MissileGirl
{
    public class Selector_DefSelection : ISelector_GenericSelection<Def>
    {
        public Selector_DefSelection(IEnumerable<Def> defs, Action<Def> selectionAction, bool integrated = false,
            Action closeAction = null) : base(defs, selectionAction, integrated, closeAction)
        {
        }

        protected override void DoSingleItem(Rect rect, Def item)
        {
            Widgets.DrawHighlightIfMouseover(rect);
            Widgets.DefLabelWithIcon(rect, item, 2);
        }

        protected override bool ItemMatchSearchString(Def item)
        {
            return item.label?.ToLower()?.Contains(searchString.ToLower()) ?? true;
        }
    }
}
