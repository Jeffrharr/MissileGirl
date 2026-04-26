// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

namespace MissileGirl.Patches
{
    //[RocketPatch]
    //public static class RoomStatWorker_Patch
    //{
    //    public static IEnumerable<MethodBase> TargetMethods()
    //    {
    //        foreach (Type t in typeof(RoomStatWorker).AllSubclassesNonAbstract())
    //        {
    //            MethodBase method = AccessTools.Method(t, nameof(RoomStatWorker.GetScore));
    //            if (method.IsValidTarget())
    //                yield return method;
    //        }
    //    }

    //    private static CachedKey key;

    //    private static CachedDict<CachedKey, float> cache = new CachedDict<CachedKey, float>();

    //    public static bool Prefix(RoomStatWorker __instance, Room room, ref float __result, out bool __state)
    //    {
    //        if (RocketPrefs.Enabled && RocketStates.Context == ContextFlag.Ticking)
    //        {
    //            return cache.TryGetValue(key = new CachedKey(room, __instance.def), out __result, expiry: 128)
    //                ? __state = false : __state = true;
    //        }
    //        __state = false;
    //        return true;
    //    }

    //    public static void Postfix(RoomStatWorker __instance, Room room, float __result, bool __state)
    //    {
    //        if (__state) cache[key] = __result;
    //    }
    //}
}
