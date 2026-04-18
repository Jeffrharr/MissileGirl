using System;
using System.Linq;
using HarmonyLib;
using Verse;

namespace MissileGirl
{
    public static class RocketCompatibilityInfo
    {
        public static bool SmartSpeedLoaded
        {
            get => false
                || LoadedModManager.RunningMods.All(m => !m.Name.Contains("Smart Speed"))
                || AccessTools.Method("SmartSpeed.AcSmartSpeed:ModIdentifier") == null;
        }
    }
}
