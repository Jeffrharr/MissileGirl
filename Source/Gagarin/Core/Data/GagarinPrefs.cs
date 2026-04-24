// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
namespace Gagarin
{
    public static class GagarinPrefs
    {
        public static bool Enabled = true;

        public static int FilterMode = (int)UnityEngine.FilterMode.Trilinear;

        public static bool TextureCachingEnabled = false;

        public static float MipMapBias = float.MinValue;

        public static DateTime CacheCreationTime;
    }
}
