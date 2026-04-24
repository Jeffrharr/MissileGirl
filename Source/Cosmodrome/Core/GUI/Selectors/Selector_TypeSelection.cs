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
using UnityEngine;
using Verse;

namespace MissileGirl
{
    public class Selector_TypeSelection : ISelector_GenericSelection<Type>
    {
        private static readonly Dictionary<Type, string> cache = new Dictionary<Type, string>();
        private readonly int count;
        private readonly Type[] types;
        private Rect viewRect = Rect.zero;

        public Selector_TypeSelection(Type t, Action<Type> selectionAction, bool integrated = false,
            Action closeAction = null) : base(t.AllSubclassesNonAbstract(), selectionAction, integrated, closeAction)
        {
            types = t.AllSubclassesNonAbstract().ToArray();
            count = types.Length;
        }

        public override float RowHeight => 24f;

        public override void DoContent(Rect inRect)
        {
            FillTypeContent(inRect);
        }

        protected void FillTypeContent(Rect inRect)
        {
            try
            {
                GUIUtility.ScrollView(inRect, ref scrollPosition, types,
                                      heightLambda: (type) => !searchString.NullOrEmpty() ? (ItemMatchSearchString(type) ? -1f : RowHeight) : RowHeight,
                                      elementLambda: (rect, type) =>
                                      {
                                          DoSingleItem(rect, type);
                                          if (Widgets.ButtonInvisible(rect))
                                          {
                                              selectionAction.Invoke(type);
                                              if (!integrated) Close();
                                          }
                                      });
            }
            catch (Exception er)
            {
                Log.Error(er.ToString());
            }
        }

        protected override void DoSingleItem(Rect rect, Type item)
        {
            string name;
            if (!cache.TryGetValue(item, out name))
                name = cache[item] = item.Name.Translate();
            Widgets.DrawHighlightIfMouseover(rect);
            Widgets.Label(rect, name);
        }

        protected override bool ItemMatchSearchString(Type item)
        {
            return true;
        }
    }
}
