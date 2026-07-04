// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DefOverrideRematch.cs (issue #43 — add-direction rematch)
//
// Contains: pure re-test of a NEWLY ADDED mod's current def ids against the baseline
// graph's recorded owners, to catch a def-override (Verse.DefDatabase<T>.Add:
// last-loaded registration wins on a duplicate defName, no PatchOperation involved)
// that the baseline graph could not have captured because the overriding mod was not
// present when that graph was written.
//
// Why this is needed in addition to DirtySetComputer's Seed 7: Seed 7 dirties a
// def-override's ids when the OWNING mod recorded in graph.DefOverrides flips
// presence (added or removed). That works for REMOVAL — the mod was there at capture
// time, so the baseline graph already knows it owns the override. It cannot work for
// ADD: a mod entering the load for the first time contributes an override the
// baseline graph structurally never saw, so there is nothing in graph.DefOverrides to
// XOR against. This mirrors the exact problem WildcardRematch solves for Seed 4
// (a widened patch predicate only exists in the changed mod's CURRENT content, not
// the baseline) — re-test current content instead of relying on stored history.
//
// Why pure: this is a dictionary comparison, not RimWorld-coupled, so it is
// unit-testable offline like the rest of the incremental pipeline. The RimWorld-facing
// driver (DirtySetDiagnostic) supplies currentIdOwner (current concrete def id -> the
// mod whose Defs file currently registers it) and newlyAddedMods (packageIds present
// this load but absent from the prior load order).

using System.Collections.Generic;

namespace Gagarin
{
    public static class DefOverrideRematch
    {
        // Def ids where a NEWLY ADDED mod's current content overrides a def the
        // baseline graph recorded as owned by a DIFFERENT mod. Ids absent from the
        // baseline are skipped -- those are genuinely new defs (the added-defs
        // channel, Seed 5's job), not overrides of existing content. Ids whose
        // current owner is not in newlyAddedMods are skipped too -- an EXISTING
        // mod's Defs file changing its own content is an ordinary file edit,
        // already covered by Seed 1.
        public static HashSet<string> NewlyDetectedOverrides(
            DependencyGraphData graph,
            IDictionary<string, string> currentIdOwner,
            ISet<string> newlyAddedMods)
        {
            var result = new HashSet<string>();
            if (graph == null || currentIdOwner == null || currentIdOwner.Count == 0
                || newlyAddedMods == null || newlyAddedMods.Count == 0)
                return result;

            var baselineOwner = new Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var n in graph.Nodes)
                if (n.Id != null)
                    baselineOwner[n.Id] = n.SourceMod;

            foreach (var kv in currentIdOwner)
            {
                string id = kv.Key;
                string owner = kv.Value;
                if (owner == null || !newlyAddedMods.Contains(owner))
                    continue;
                if (!baselineOwner.TryGetValue(id, out var baselineMod))
                    continue; // not in the baseline at all -- a genuinely new def (Seed 5), not an override
                if (!string.Equals(baselineMod, owner, System.StringComparison.OrdinalIgnoreCase))
                    result.Add(id);
            }
            return result;
        }
    }
}
