// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using RimWorld;
using MissileGirl;
using MissileGirl.Tabs;
using UnityEngine;
using Verse;

namespace Gagarin
{
    public class TabContent_Gagarin : ITabContent
    {
        private List<FilterMode> optionsFilter;

        private List<Action<Rect>> columnsFilter;

        private Listing_Collapsible collapsible = new Listing_Collapsible(expanded: true);

        public override Texture2D Icon => TexTab.Gagarin;

        public override bool ShouldShow => true;

        public override string Label => KeyedResources.Gagarin_Tab;

        public TabContent_Gagarin()
        {
            optionsFilter = new List<FilterMode>()
            {
                FilterMode.Bilinear,
                FilterMode.Trilinear
            };
            this.columnsFilter = new List<Action<Rect>>()
            {
                (rect) =>
                {
                    GUIFont.Anchor = TextAnchor.MiddleLeft;

                    Widgets.Label(rect, KeyedResources.Gagarin_FilterMode);
                },
                (rect) =>
                {
                    if (Widgets.ButtonText(rect, (FilterMode)GagarinPrefs.FilterMode == FilterMode.Bilinear ? "Bilinear" : "Trilinear"))
                    {
                        FloatMenuUtility.MakeMenu(optionsFilter,
                                                  (mode) =>
                                                  {
                                                      if (mode == FilterMode.Bilinear)
                                                          return "Bilinear";
                                                      if (mode == FilterMode.Trilinear)
                                                          return "Trilinear";
                                                      return "";
                                                  },
                                                  (mode) =>
                                                  {
                                                      return () =>
                                                      {
                                                          GagarinPrefs.FilterMode = (int)mode;
                                                          GagarinSettings.WriteSettings();
                                                          ClearCache();
                                                      };
                                                  }
                        );
                    }
                }
            };
        }

        public override void DoContent(Rect rect)
        {
            collapsible.Begin(rect, KeyedResources.MissileGirl_Settings);
            collapsible.Label(KeyedResources.MissileGirl_EnableGagarin_Tip);
            if (collapsible.CheckboxLabeled(KeyedResources.MissileGirl_EnableGagarin, ref GagarinPrefs.Enabled) && !GagarinPrefs.Enabled)
            {
                ClearCache();
            }
            if (GagarinPrefs.Enabled)
            {
                collapsible.Line(1);
                collapsible.Label(KeyedResources.Gagarin_Expiry.Formatted(3 - DateTime.Now.Subtract(GagarinPrefs.CacheCreationTime).Days));
                collapsible.Gap(4);
                collapsible.Label(KeyedResources.Gagarin_Tip);
                collapsible.Label(KeyedResources.Gagarin_Tip, invert: true);
                collapsible.Line(1);
                collapsible.Label(KeyedResources.Gagarin_ClearCache_Description);
                collapsible.Lambda(25, (rect) =>
                {
                    if (Widgets.ButtonText(rect, label: KeyedResources.Gagarin_ClearCache))
                    {
                        ClearCache();
                        GagarinSettings.WriteSettings();
                    }
                }, useMargins: true);
                if (RocketEnvironmentInfo.IsDevEnv)
                {
                    collapsible.Line(1);
                    collapsible.Label(KeyedResources.Gagarin_EnableTextureCaching_Description);
                    if (collapsible.CheckboxLabeled(KeyedResources.Gagarin_EnableTextureCaching, ref GagarinPrefs.TextureCachingEnabled))
                    {
                        ClearCache();
                        GagarinSettings.WriteSettings();
                    }
                    collapsible.Line(1);
                    collapsible.Label(KeyedResources.Gagarin_AdvancedSettings, fontSize: GUIFontSize.Smaller);
                    collapsible.Gap(3);
                    collapsible.Label(KeyedResources.Gagarin_AdvancedSettings_Description);
                    collapsible.Line(1);
                    collapsible.Columns(20, columnsFilter, useMargins: true);
                }
            }
            collapsible.End(ref rect);
            if (GUI.changed)
            {
                GagarinSettings.WriteSettings();
            }
        }

        private static void ClearCache()
        {
            foreach (string file in new[]
            {
                GagarinEnvironmentInfo.UnifiedXmlFilePath, GagarinEnvironmentInfo.ModListFilePath, GagarinEnvironmentInfo.UnifiedPatchedOriginalXmlPath,
            })
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
        }

        public override void OnSelect()
        {
            base.OnSelect();

            GagarinSettings.WriteSettings();
        }

        public override void OnDeselect()
        {
            base.OnDeselect();

            GagarinSettings.WriteSettings();
        }

        [Main.YieldTabContent]
        [Main.YieldModMenuTab]
        public static ITabContent YieldTab() => new TabContent_Gagarin();
    }
}
