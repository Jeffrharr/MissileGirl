// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using RimWorld;
namespace MissileGirl
{
    public partial class RocketMod
    {
        [Main.OnTickRare]
        [Main.OnDefsLoaded]
        public static void UpdateExceptions()
        {
            if (StatDefOf.MarketValue != null && StatDefOf.MarketValueIgnoreHp != null)
            {
                RocketStates.StatExpiry[StatDefOf.MarketValue.index] = 0;
                RocketStates.StatExpiry[StatDefOf.MarketValueIgnoreHp.index] = 0;
            }
        }
    }
}
