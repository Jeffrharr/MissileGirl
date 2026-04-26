// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
namespace MissileGirl.Tabs
{
    public class TabContent_Stats : ITabContent
    {
        private IEnumerable<StatDef> stats;

        private Vector2 scrollPosition = Vector2.zero;

        public override string Label => "Statistics";

        public override Texture2D Icon => TexTab.Stats;

        public override bool ShouldShow => RocketPrefs.Enabled && RocketDebugPrefs.Debug;

        private string searchString = "";

        public TabContent_Stats()
        {
            stats = DefDatabase<StatDef>.AllDefs;
        }

        public override void DoContent(Rect rect)
        {
            string tempStr = Widgets.TextField(rect.TopPartPixels(25), searchString).ToLower();
            if (tempStr != searchString)
            {
                scrollPosition = Vector2.zero;
                searchString = tempStr;
            }
            rect.yMin += 30;
            GUIUtility.ScrollView(rect, ref scrollPosition, stats,
                                              stat =>
                                              {
                                                  if (searchString == null || searchString.Trim().NullOrEmpty())
                                                  {
                                                      return 40.0f;
                                                  }
                                                  return (stat.label?.ToLower().Contains(searchString) ?? false) ? 40.0f : -1.0f;
                                              },
                                              (rect, stat) =>
                                              {
                                                  GUIUtility.StashGUIState();
                                                  GUIFont.Font = GUIFontSize.Tiny;
                                                  Widgets.Label(rect.TopPartPixels(20), stat.label.CapitalizeFirst());
                                                  GUIFont.Anchor = TextAnchor.UpperRight;
                                                  GUIFont.CurFontStyle.fontStyle = FontStyle.Italic;
                                                  Widgets.Label(rect, $"{RocketStates.StatExpiry[stat.index]} Ticks");
                                                  GUIUtility.RestoreGUIState();
                                                  Widgets.HorizontalSlider(rect.BottomPartPixels(20), ref RocketStates.StatExpiry[stat.index], new FloatRange(0, 1024), $"{RocketStates.StatExpiry[stat.index]}");
                                              });
        }

        public override void OnSelect()
        {
            base.OnSelect();
        }

        public override void OnDeselect()
        {
            base.OnDeselect();
        }
    }
}
