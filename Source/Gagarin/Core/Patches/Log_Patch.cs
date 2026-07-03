// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// Log_Patch.cs (testing-only) — see GagarinPrefs.DisableLogCap for the full rationale.
//
// Verse.Log.Notify_MessageReceivedThreadedInternal counts every Debug.Log/Warning/Error
// in the process (any mod, Harmony, RimWorld itself) and, once it sees 10000 total,
// permanently disables Debug.unityLogger -- silently swallowing all subsequent logging,
// including our own live-test markers ("Provenance captured", "Recompute gate") that the
// harness polls Player.log for. This looks exactly like a hang from the harness's point of
// view, and hides whatever the real symptom is on a heavy (hundreds-of-real-mods) run.
//
// Skipping the original method body means messageCount never increments and the cap never
// trips -- this is a blunt instrument (it also disables the "pause on error"/log-window-open
// side effects RimWorld normally gets at message #10000), acceptable because the flag is
// OFF by default and only the live-test harness turns it on.
using Verse;

namespace Gagarin
{
    [GagarinPatch(typeof(Log), nameof(Log.Notify_MessageReceivedThreadedInternal))]
    public static class Log_Notify_MessageReceivedThreadedInternal_Patch
    {
        public static bool Prefix() => !GagarinPrefs.DisableLogCap;
    }
}
