// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using Verse;

namespace Gagarin
{
    public static class UIRoot_Entry_Patch
    {
        [GagarinPatch(typeof(UIRoot_Entry), nameof(UIRoot_Entry.Init))]
        public static class UIRoot_Entry_Init_Patch
        {
            // Verse.Root.Start() queues PlayDataLoader.LoadAllPlayData() (all mod/def loading,
            // including everything the rest of Patches/ touches) as one long event, then queues
            // UIRoot_Entry.Init() as a second, later long event -- so this Postfix is guaranteed to
            // fire only after startup has fully finished, never mid-load. Live test harnesses use
            // this marker to tell "RimWorld never even reached the main menu" (a broken/incompatible
            // modlist) apart from "it got there and our code is what's broken." Uses Log.Warning
            // directly (not Logger.Debug, which only writes a separate Rocket.log file, never
            // Player.log) -- matching how every other harness-polled marker
            // ("Provenance captured", "Recompute gate") is logged.
            public static void Postfix()
            {
                Log.Warning("GAGARIN: Main menu reached");
            }
        }
    }
}
