// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
namespace MissileGirl
{
    public static class RocketStates
    {
        public static int LastFrame;

        public static ContextFlag Context = ContextFlag.Unknown;

        public static int TicksSinceStarted = 0;

        public static bool DefsLoaded = false;

        public static bool SingleTickIncrement = false;

        public static int SingleTickLeft = 0;

        public static float[] StatExpiry = new float[ushort.MaxValue];

        public static FlagArray DilatedDefs = new FlagArray(ushort.MaxValue);

        public static object LOCKER = new object();
    }
}
