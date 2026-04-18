using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using HarmonyLib;
using RimWorld;
using MissileGirl;
using MissileGirl.Tabs;
using UnityEngine;

namespace Soyuz
{
    public enum JobThrottleMode
    {
        Partial = 1,

        Full = 3,

        None = 4,
    }
}