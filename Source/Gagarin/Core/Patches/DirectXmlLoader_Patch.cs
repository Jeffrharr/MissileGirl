// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Xml;
using MissileGirl;
using Verse;

namespace Gagarin
{
    public static class DirectXmlToObjectNew_Patch
    {
        [GagarinPatch(typeof(DirectXmlToObjectNew), nameof(DirectXmlToObjectNew.DefFromNodeNew))]
        public static class DirectXmlToObjectNew_DefFromNodeNew_Patch
        {
            public static void Postfix(XmlNode node, LoadableXmlAsset loadingAsset, Def __result)
            {
                if (!Context.IsUsingCache && __result != null)
                {
                    try
                    {
                        CachedDefHelper.Register(__result, node, loadingAsset);
                    }
                    catch (Exception er)
                    {
                        Logger.Debug("GAGARIN: Failed in LoadableXmlAsset", exception: er);
                        Context.IsUsingCache = false;
                    }
                }
            }
        }
    }

}
