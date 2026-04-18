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
