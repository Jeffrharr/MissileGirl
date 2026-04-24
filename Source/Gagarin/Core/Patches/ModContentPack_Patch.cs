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
using MissileGirl;
using Verse;

namespace Gagarin
{
    public static class ModContentPack_Patch
    {
        [GagarinPatch(typeof(ModContentPack), nameof(ModContentPack.LoadDefs))]
        public class ModContentPack_LoadDefs_Patch
        {
            public static void Prefix(ModContentPack __instance)
            {
                Context.CurrentLoadingMod = __instance;
            }

            public static void Postfix(ModContentPack __instance)
            {
                Context.CurrentLoadingMod = null;

                CheckPatches(__instance);
            }

            private static void CheckPatches(ModContentPack mod)
            {
                Context.IsLoadingPatchXML = true;
                Context.CurrentLoadingMod = mod;
                Exception error = null;
                try
                {
                    DirectXmlLoader.XmlAssetsInModFolder(mod, "Patches/").ToList();
                }
                catch (Exception er)
                {
                    error = er;
                }
                finally
                {
                    Context.IsLoadingPatchXML = false;
                    Context.CurrentLoadingMod = null;
                }
                if (error != null)
                {
                    throw error;
                }
            }
        }
    }
}
