// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Runtime.CompilerServices;
using Verse;

namespace MissileGirl
{
    public static class SignatureUtility
    {
        private const int CacheSize = 31397;
        private const int A = 0x45d9f3b;
        private const int B = 0x119de1f3;
        private const int C = 0x17;

        private static readonly Thing[] owners = new Thing[CacheSize];
        private static readonly int[] signatures = new int[CacheSize];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Int32 GetSignature(this Thing thing, bool dirty = false)
        {
            Int32 key = thing.thingIDNumber;
            unchecked
            {
                key = ((key << 30) ^ key) * A;
                key = ((key << 27) ^ key) * B;
                key = ((key << 31) ^ key) * C;
                key = (key > 0 ? key : -key) % CacheSize;
            }

            if (dirty || owners[key] != thing)
            {
                owners[key] = thing;
                return signatures[key] = Rand.Int;
            }
            return signatures[key];
        }
    }
}
