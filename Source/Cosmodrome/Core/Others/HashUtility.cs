// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Runtime.CompilerServices;

namespace MissileGirl
{
    public static class HashUtility
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int HashOne(int numberToHash, int previousHash = 17)
        {
            return previousHash * 7919 + numberToHash;
        }
    }
}
