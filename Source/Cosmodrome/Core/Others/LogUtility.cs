// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

namespace MissileGirl
{
    public static class LogUtility
    {
        private static readonly int _MissileGirl_HEADER_LENGHT = "MissileGirl:".Length;
        private static readonly int _SOYUZ_HEADER_LENGHT = "SOYUZ:".Length;
        private static readonly int _ROCKETERR_HEADER_LENGHT = "ROCKETEER:".Length;
        private static readonly int _PROTON_HEADER_LENGHT = "PROTON:".Length;
        private static readonly int _GAGARIN_HEADER_LENGHT = "GAGARIN:".Length;
        private static string rocketColor = "orange";

        public static string StylizeRocketLog(this string text)
        {
            int startIndex;
            string replacement;
            try
            {
                if (text.StartsWith("MissileGirl:"))
                {
                    replacement = $"<color={rocketColor}>MissileGirl:</color> ";
                    startIndex = _MissileGirl_HEADER_LENGHT;
                }
                else if (text.StartsWith("SOYUZ:"))
                {
                    replacement = $"<color={rocketColor}>MissileGirl</color>+<color=red>SOYUZ:</color> ";
                    startIndex = _SOYUZ_HEADER_LENGHT;
                }
                else if (text.StartsWith("ROCKETEER:"))
                {
                    replacement = $"<color={rocketColor}>MissileGirl</color>+<color=yellow>ROCKETEER:</color> ";
                    startIndex = _ROCKETERR_HEADER_LENGHT;
                }
                else if (text.StartsWith("PROTON:"))
                {
                    replacement = $"<color={rocketColor}>MissileGirl</color>+<color=green>PROTON:</color> ";
                    startIndex = _PROTON_HEADER_LENGHT;
                }
                else if (text.StartsWith("GAGARIN:"))
                {
                    replacement = $"<color={rocketColor}>MissileGirl</color>+<color=blue>GAGARIN:</color>[<color=red>EXPERIMENTAL</color>] ";
                    startIndex = _GAGARIN_HEADER_LENGHT;
                }
                else return text;
                return replacement + text.Substring(startIndex).Trim();
            }
            catch { }
            return text;
        }
    }
}
