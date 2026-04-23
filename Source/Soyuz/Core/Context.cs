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