// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// PatchCoverageAudit.cs (capture-completeness detector — the "no invisible op" evidence)
//
// Contains: a PURE check that, given a full rebuild to compare against, flags defs whose value
// CHANGED for a reason no captured op accounts for — the signature of an op INVISIBLE to provenance
// capture.
//
// Why this exists: the recompute allowlist is only as safe as the dependency graph is complete. It
// declines what it can see and cannot vouch for (unattributed ops -> capture-gap); but an op the
// capture never recorded — a custom PatchOperation that mutates XML without going through
// SelectNodes/SelectSingleNode (so its edge has empty modifiedNodeIds), or one that overrides Apply
// and never hits our hook (so it has no edge at all) — is invisible to the allowlist's producer
// detection. Such an op could change a def the allowlist then admits, and in production (no full
// rebuild to diff against) that ships silently. We have no evidence any exist, but "no evidence" is
// not "proof of absence." This audit turns it into a measurable signal: run it wherever a full
// rebuild IS available (the diagnostic gate), and a persistent zero across real captures is the
// evidence that lets us trust serving.
//
// How: every def that changed between the prior cache and the fresh full rebuild must be EXPLAINED by
// a visible cause — either the def (or an inheritance ancestor) was touched by a recorded patch edge
// (modifiedNodeIds), or the def (or an ancestor) had its raw source file change this load (Seed 1, a
// non-patch channel). A changed def explained by neither is "unattributed": nothing we captured
// accounts for its change. Added/removed defs are excluded for free — this only considers defs
// present in BOTH cache and rebuild (a value change, not an add/drop).
//
// Why pure: set/graph analysis over plain collections, no RimWorld types — unit-tested offline like
// SubDocExpander / RecomputeAllowlist. The gate supplies the real changed-set, edge set, inheritance
// map, and Seed-1 set; this decides attribution.

using System;
using System.Collections.Generic;

namespace Gagarin
{
    public static class PatchCoverageAudit
    {
        // Returns the changed defs that NO captured cause explains (candidate invisible-op victims).
        //   changedIds       : defs present in both prior cache and rebuild whose serialized value differs
        //   modifiedByEdges  : union of every patch edge's modifiedNodeIds (what capture saw ops modify)
        //   parentOf         : child node id -> parent node id (inheritance), to walk ancestors
        //   rawChangedIds    : defs whose own raw source file changed this load (Seed 1, non-patch channel)
        // A def is EXPLAINED when it, or any inheritance ancestor, is in modifiedByEdges or
        // rawChangedIds. Everything else is returned, sorted for stable reporting.
        public static List<string> Unattributed(
            ICollection<string> changedIds,
            ICollection<string> modifiedByEdges,
            IDictionary<string, string> parentOf,
            ICollection<string> rawChangedIds)
        {
            var result = new List<string>();
            if (changedIds == null || changedIds.Count == 0)
                return result;

            // A def's change is attributable if it OR an ancestor was patched (an edge modified it) or
            // had its raw body change. Union both visible causes into one membership set.
            var explained = new HashSet<string>(StringComparer.Ordinal);
            if (modifiedByEdges != null)
                foreach (string id in modifiedByEdges)
                    explained.Add(Canonical(id));
            if (rawChangedIds != null)
                foreach (string id in rawChangedIds)
                    explained.Add(Canonical(id));

            // An abstract def that also declares a <defName> (e.g. <TerrainDef Name="Carpet_Mindbend"
            // Abstract="True"><defName>Carpet_Mindbend</defName>...) gets keyed two different ways by
            // two different capture paths for the SAME node: KeyForNode (patch-edge capture) prefers
            // defName -> "TerrainDef/Carpet_Mindbend", while RegisterAbstract (inheritance
            // registration) deliberately forces Name-attr keying for abstract nodes ->
            // "TerrainDef@Carpet_Mindbend" (see its own comment — needed so a ParentName reference
            // resolves to the right shape). Both strings name the same node, so canonicalize the
            // inheritance map to the same '/' form before walking it, or a patch captured under one
            // spelling never reconciles with an inheritance edge recorded under the other and a
            // legitimately-explained def is false-flagged as an invisible op (#47).
            var canonParentOf = new Dictionary<string, string>(StringComparer.Ordinal);
            if (parentOf != null)
                foreach (KeyValuePair<string, string> kv in parentOf)
                    canonParentOf[Canonical(kv.Key)] = Canonical(kv.Value);

            foreach (string id in changedIds)
            {
                if (!IsExplained(id, explained, canonParentOf))
                    result.Add(id);
            }
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        // Normalizes the two node-id spellings ("{Type}/{name}" vs "{Type}@{name}") for the same
        // node onto one shape, so membership/parent-map lookups agree regardless of which capture
        // path produced the id. See the comment in Unattributed for why both forms exist.
        private static string Canonical(string id) => id?.Replace('@', '/');

        // Walk the def and its inheritance ancestors; explained if any is a visible cause. A cycle
        // guard keeps a malformed ParentName chain from looping (mirrors the other walkers).
        private static bool IsExplained(
            string id, HashSet<string> explained, IDictionary<string, string> parentOf)
        {
            string cur = Canonical(id);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            while (cur != null && seen.Add(cur))
            {
                if (explained.Contains(cur))
                    return true;
                if (parentOf == null || !parentOf.TryGetValue(cur, out string parent))
                    return false;
                cur = parent; // already canonicalized when canonParentOf was built
            }
            return false;
        }
    }
}
