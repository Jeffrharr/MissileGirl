// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using UnityEngine;
using Verse;
namespace MissileGirl
{
    [StaticConstructorOnStartup]
    public static class TexTab
    {
        public static readonly Texture2D Settings = ContentFinder<Texture2D>.Get("MissileGirl/UI/gear_icon");

        public static readonly Texture2D Alerts = ContentFinder<Texture2D>.Get("MissileGirl/UI/bell_icon");

        public static readonly Texture2D Dilation = ContentFinder<Texture2D>.Get("MissileGirl/UI/clock_icon");

        public static readonly Texture2D Stats = ContentFinder<Texture2D>.Get("MissileGirl/UI/stat_icon");

        public static readonly Texture2D Debug = ContentFinder<Texture2D>.Get("MissileGirl/UI/debug_icon");

        public static readonly Texture2D World = ContentFinder<Texture2D>.Get("MissileGirl/UI/world_icon");

        public static readonly Texture2D Graphing = ContentFinder<Texture2D>.Get("MissileGirl/UI/graph_icon");

        public static readonly Texture2D Gagarin = ContentFinder<Texture2D>.Get("MissileGirl/UI/gagarin_gear");
    }
}
