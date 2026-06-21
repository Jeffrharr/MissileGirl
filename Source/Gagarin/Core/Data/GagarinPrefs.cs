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

        public static bool CacheExpires = true;
        public static int CacheRetentionTime = 3;

        // Dev-only flag for Piece A of the incremental-cache prototype. When ON
        // (and only on a cold/cache-miss load) Gagarin captures the def
        // dependency graph to DependencyGraph.json. Default OFF so normal users
        // and the shipped cache are unaffected. Not scribed to settings.
        public static bool CaptureProvenance = false;

        // Dev-only flag for Piece D Milestone 1. When ON, and a prior DependencyGraph.json
        // (from a CaptureProvenance run) + asset hashes exist, Gagarin computes — alongside
        // its normal rebuild — which defs WOULD need recomputing given what changed, and
        // writes DirtySet.json. Pure diagnostic: it changes no cache behaviour. Default OFF.
        public static bool DirtySetDiagnostic = false;
    }
}
