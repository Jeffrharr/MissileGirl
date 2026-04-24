// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using UnityEngine;
using Verse;

namespace MissileGirl
{
    [StaticConstructorOnStartup]
    public static class TexTab
    {
        public static readonly Texture2D Settings = ContentFinder<Texture2D>.Get("MissileGirl/UI/gear_icon", true);

        public static readonly Texture2D Alerts = ContentFinder<Texture2D>.Get("MissileGirl/UI/bell_icon", true);

        public static readonly Texture2D Dilation = ContentFinder<Texture2D>.Get("MissileGirl/UI/clock_icon", true);

        public static readonly Texture2D Stats = ContentFinder<Texture2D>.Get("MissileGirl/UI/stat_icon", true);

        public static readonly Texture2D Debug = ContentFinder<Texture2D>.Get("MissileGirl/UI/debug_icon", true);

        public static readonly Texture2D World = ContentFinder<Texture2D>.Get("MissileGirl/UI/world_icon", true);

        public static readonly Texture2D Graphing = ContentFinder<Texture2D>.Get("MissileGirl/UI/graph_icon", true);

        public static readonly Texture2D Gagarin = ContentFinder<Texture2D>.Get("MissileGirl/UI/gagarin_gear", true);
    }
}
