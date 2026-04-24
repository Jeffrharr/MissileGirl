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
    public class FlagArray
    {
        private int[] map;

        private const int Bit = 1;
        private const int ChunkSize = 32;

        public int Length
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => map.Length;
        }

        public FlagArray(int size)
        {
            this.map = new int[(size / ChunkSize) + ChunkSize];
        }

        public bool this[int key]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Get(key);
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set => Set(key, value);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Get(int key)
        {
            return (map[key / ChunkSize] & GetOp(key)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FlagArray Set(int key, bool value)
        {
            map[key / ChunkSize] = value ?
                    map[key / ChunkSize] | GetOp(key) :
                    map[key / ChunkSize] & ~GetOp(key);
            return this;
        }

        public void Expand(int targetLength)
        {
            targetLength = (targetLength / ChunkSize) + ChunkSize;
            if (targetLength > Length * 0.75f)
            {
                int[] expanded = new int[targetLength];
                Array.Copy(map, 0, expanded, 0, map.Length);
                this.map = expanded;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int GetOp(int key)
        {
            return Bit << (key % ChunkSize);
        }
    }
}
