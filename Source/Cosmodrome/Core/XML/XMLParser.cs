// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Linq;
using System.Xml;
using Verse;
namespace MissileGirl
{
    public static class XMLParser
    {
        public static string rocketRulesFolder = "Extras";

        public static void ParseXML()
        {
            Logger.Message("MissileGirl: XMLParser started");
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                LoadableXmlAsset[] assets = DirectXmlLoader.XmlAssetsInModFolder(mod, rocketRulesFolder);
                foreach (LoadableXmlAsset ass in assets)
                {
                    if (!ass.name.ToLower().StartsWith("rocket") || !ass.name.ToLower().EndsWith(".xml")) continue;
                    foreach (XmlElement element in ass.xmlDoc["RocketRules"].OfType<XmlElement>())
                        ProcessRocketRuleData(element);
                }
            }
        }

        private static void ProcessRocketRuleData(XmlElement node)
        {
            if (node.Name == "IgnoreMe")
            {
                if (node.HasAttribute("defname"))
                {
                    IgnoreMeDatabase.Add(node.GetAttribute("defname"));
                    return;
                }
                if (node.HasAttribute("packageId"))
                {
                    IgnoreMeDatabase.AddPackageId(node.GetAttribute("packageId"));
                }
            }
            else if (node.Name == "Incompatibility")
            {
                if (!node.HasAttribute("packageId"))
                    return;
                if (!node.HasAttribute("name"))
                    return;
                string name = node.GetAttribute("name").ToLower();
                string packageId = node.GetAttribute("packageId").ToLower();
                IncompatibilityHelper.Register(name, packageId);
            }
        }
    }
}
