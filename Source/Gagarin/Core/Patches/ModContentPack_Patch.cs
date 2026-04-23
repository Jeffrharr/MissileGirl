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
                if (!RocketMod.rocketModSettings.xmlCaching) return;
                Context.CurrentLoadingMod = __instance;
            }

            public static void Postfix(ModContentPack __instance)
            {
                if (!RocketMod.rocketModSettings.xmlCaching) return;
                Context.CurrentLoadingMod = null;

                CheckPatches(__instance);
            }

            private static void CheckPatches(ModContentPack mod)
            {
                if (!RocketMod.rocketModSettings.xmlCaching) return;
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
