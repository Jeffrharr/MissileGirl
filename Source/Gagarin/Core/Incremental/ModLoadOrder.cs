// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// ModLoadOrder.cs
//
// Contains: a small reusable pure helper to filter/reorder an arbitrary collection of packageIds
// to match a given real load-order sequence, instead of whatever incidental order an unordered
// caller collection (HashSet<string> and friends) happens to enumerate in.
//
// First need: TrialExecution.TryAdmit (issue #72) processes declined mods one at a time,
// accumulating each mod's trial-discovered ids into the next mod's scope -- that accumulation is
// only meaningful if mods are actually visited in the same order the real engine would apply
// their patches. The declinedMods set it was iterating had no such guarantee: HashSet<string>
// enumeration order is unspecified and not insertion-order-preserving, so two mods with an
// implicit order dependency between their trials could be visited in either order, run to run,
// for reasons unrelated to the actual mod list.
//
// Why pure: reordering one collection against another is plain set/list logic -- no RimWorld
// types needed. The real order itself (LoadedModManager.RunningMods) can only be read live,
// in-game -- TrialExecution.cs supplies it to Sort as a plain ordered list, keeping that one-line
// RimWorld touchpoint out of this file so the actual reorder logic is offline-tested instead of
// only provable live like the rest of trial execution.

using System;
using System.Collections.Generic;

namespace Gagarin
{
    public static class ModLoadOrder
    {
        // Returns packageIds ordered to match `order` (the real load sequence, caller-supplied).
        // An id absent from order is dropped (nothing to order it against). Membership is
        // case-insensitive, matching how packageIds are keyed elsewhere in this pipeline
        // (changedMods, declinedMods, modByPackageId).
        public static List<string> Sort(ICollection<string> packageIds, IReadOnlyList<string> order)
        {
            var result = new List<string>();
            if (packageIds == null || packageIds.Count == 0 || order == null)
                return result;

            var wanted = new HashSet<string>(packageIds, StringComparer.OrdinalIgnoreCase);
            foreach (string packageId in order)
                if (packageId != null && wanted.Contains(packageId))
                    result.Add(packageId);
            return result;
        }
    }
}
