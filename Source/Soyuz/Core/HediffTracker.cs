// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using RimWorld;
using Verse;

namespace Soyuz.Core
{
    public class HediffTracker
    {
        private Pawn pawn;
        private bool pregnant = false;

        public Pawn Pawn
        {
            get => pawn;
        }

        public bool Pregnant
        {
            get => pregnant;
            set => pregnant = value;
        }

        public HediffTracker(Pawn pawn)
        {
            this.pawn = pawn;
            if (this.pawn?.health?.hediffSet?.HasHediff(HediffDefOf.Pregnant) ?? false)
            {
                this.pregnant = true;
            }
        }

        public void ExposeData()
        {
            Scribe_Values.Look<bool>(ref pregnant, "pregnant", false);
        }
    }
}
