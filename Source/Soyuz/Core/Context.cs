// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using MissileGirl;
using Soyuz.Profiling;
using Verse;

namespace Soyuz
{
    public static class Context
    {
        public static CameraZoomRange ZoomRange;

        public static CellRect CurViewRect;

        public static Pawn ProfiledPawn;

        public static int DilationRate
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                switch (Context.ZoomRange)
                {
                    default:
                        return 1;
                    case CameraZoomRange.Closest:
                        return 60;
                    case CameraZoomRange.Close:
                        return 20;
                    case CameraZoomRange.Middle:
                        return 15;
                    case CameraZoomRange.Far:
                        return 15;
                    case CameraZoomRange.Furthest:
                        return 7;
                }
            }
        }
    }
}
