// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

namespace MissileGirl
{
    public static class RocketDebugPrefs
    {
        public static bool Debug = false;

        public static bool StatLogging = false;

        [Main.SettingsField(warmUpValue: false)]
        public static bool FlashDilatedPawns = false;

        [Main.SettingsField(warmUpValue: false)]
        public static bool AlwaysDilating = false;

        public static bool DrawGlowerUpdates = false;

        public static bool LogData = false;

    }
}
