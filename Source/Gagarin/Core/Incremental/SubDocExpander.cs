// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// SubDocExpander.cs (Piece D — Milestone 2b-2b: sub-doc sibling expansion)
//
// Contains: the pure expansion that turns the dirty set into the larger CONTEXT set the
// recompute sub-doc must also contain — plus a conservative fallback flag for the cases the
// sub-doc recompute cannot do faithfully.
//
// Why this exists: the first sub-doc recompute (dead-end/m2b-2b-subdoc) put only the dirty
// defs into the recompute <Defs> document, then re-ran every mod's PatchOperations over it.
// That breaks PatchOperationSequence: a sequence applies its child ops in order and ABORTS the
// whole sequence the moment one child's xpath finds no match (RimWorld's
// PatchOperationSequence.ApplyWorker returns on the first failure). When only one def from a
// bundle is dirty, the sequence's other child ops target sibling defs that are absent from the
// sub-doc, so the sequence aborts early and the dirty def silently keeps an UN-patched value.
// The recompute gate caught this as 12 recompute mismatches: 12 defs whose recomputed value
// lacked patches the full rebuild had applied.
//
// The fix (proven bounded by scripts/closure.py against real data — 245 dirty -> 337 sub-doc
// total, 1.17% of 28,682 defs): for every sequence that touches a dirty def, also pull in the
// defs its SIBLING child ops touch, so the sequence finds all its targets and runs to
// completion exactly as the full load did. Those sibling defs are CONTEXT only — their resolved
// values are discarded (the baseline cache already holds them); they exist purely so the
// sequence does not abort.
//
// The fallback: sibling expansion trusts the BASELINE graph's execution paths, which is sound
// for UNCHANGED mods (their sequence/conditional structure is exactly what the snapshot
// captured). But if the CHANGED mod itself owns a container op (Sequence / Conditional), the
// baseline path for that op is stale — the edit may have reordered ops, changed a conditional's
// test, or moved which defs the sequence reaches — and the captured siblings no longer describe
// the current execution. Rather than risk an unfaithful recompute, we flag needsFullRebuild so
// the caller falls back to the full rebuild for that load. This is conservative (a changed mod
// with a container op is rare) and never wrong: the full rebuild is always faithful.
//
// Why pure: it is just graph/string analysis over DependencyGraphData, no RimWorld types, so it
// is unit-tested offline exactly like DirtySetComputer. It mirrors scripts/closure.py one-to-one
// so the offline analysis and the in-game expansion cannot drift.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Gagarin
{
    public static class SubDocExpander
    {
        // Matches a patchId whose trailing segment is a direct sequence child, e.g.
        // "...operations[2]". The parent key is everything BEFORE this match (mirrors
        // closure.py's sequence_parent regex). A conditional branch ("...match") is NOT a
        // sequence child and yields no parent here — siblings only make sense within a list.
        private static readonly Regex SeqChildSuffix =
            new Regex(@"\.operations\[\d+\]$", RegexOptions.Compiled);

        // Expand the dirty set into the additional CONTEXT defs the recompute sub-doc needs so
        // that PatchOperationSequences touching a dirty def don't abort on absent siblings, and
        // decide whether the load must fall back to a full rebuild.
        //
        // Returns the context ids (disjoint from dirtyIds). When needsFullRebuild is true the
        // returned set is empty and the caller must NOT recompute — the changed mod owns a
        // container op whose baseline execution path is stale (see file header).
        public static HashSet<string> Expand(
            DependencyGraphData graph,
            ICollection<string> dirtyIds,
            ICollection<string> changedModIds,
            out bool needsFullRebuild,
            out string fallbackReason)
        {
            needsFullRebuild = false;
            fallbackReason = null;
            var context = new HashSet<string>(StringComparer.Ordinal);
            if (graph == null)
                return context;

            var dirty = AsSet(dirtyIds);
            var changedMods = AsSet(changedModIds);

            // One pass over the patch edges builds both lookups AND checks the fallback:
            //   nodeToEdges:      modifiedNodeId -> edges that modified it (to find a dirty def's
            //                     sequences)
            //   parentToSiblings: sequence parent key -> its child edges (to collect the siblings)
            var nodeToEdges = new Dictionary<string, List<GraphPatchEdge>>(StringComparer.Ordinal);
            var parentToSiblings = new Dictionary<string, List<GraphPatchEdge>>(StringComparer.Ordinal);
            foreach (GraphPatchEdge edge in graph.PatchEdges)
            {
                foreach (string nid in edge.ModifiedNodeIds)
                {
                    if (!nodeToEdges.TryGetValue(nid, out List<GraphPatchEdge> list))
                        nodeToEdges[nid] = list = new List<GraphPatchEdge>();
                    list.Add(edge);
                }

                string parentKey = SequenceParentKey(edge.PatchId);
                if (parentKey != null)
                {
                    if (!parentToSiblings.TryGetValue(parentKey, out List<GraphPatchEdge> sibs))
                        parentToSiblings[parentKey] = sibs = new List<GraphPatchEdge>();
                    sibs.Add(edge);
                }

                // Fallback: the changed mod itself owns a container op. We keep scanning (so the
                // lookups stay complete for callers that ignore the flag) but record the first hit.
                if (!needsFullRebuild && changedMods.Contains(edge.SourceMod))
                {
                    string opType = ContainerOpType(edge.PatchId);
                    if (opType != null)
                    {
                        needsFullRebuild = true;
                        fallbackReason =
                            $"changed mod {edge.SourceMod} has container op ({opType}) at {edge.PatchId}";
                    }
                }
            }

            if (needsFullRebuild)
                return context; // caller falls back; the context set is meaningless here

            // For each dirty def, find the sequences it participates in and union their siblings'
            // modified defs into the context. seenSequences dedupes so a sequence touched by
            // several dirty defs is expanded once (mirrors closure.py's seen_sequences).
            var seenSequences = new HashSet<string>(StringComparer.Ordinal);
            foreach (string dirtyId in dirty)
            {
                if (!nodeToEdges.TryGetValue(dirtyId, out List<GraphPatchEdge> edges))
                    continue;
                foreach (GraphPatchEdge edge in edges)
                {
                    string parentKey = SequenceParentKey(edge.PatchId);
                    if (parentKey == null || !seenSequences.Add(parentKey))
                        continue; // not a sequence child, or this sequence is already expanded
                    if (!parentToSiblings.TryGetValue(parentKey, out List<GraphPatchEdge> siblings))
                        continue;
                    foreach (GraphPatchEdge sib in siblings)
                        foreach (string nid in sib.ModifiedNodeIds)
                            if (!dirty.Contains(nid))
                                context.Add(nid); // already-dirty defs are not "context"
                }
            }
            return context;
        }

        // If patchId names a direct sequence child ("...operations[N]"), return the patchId of
        // the parent sequence container (everything before the trailing ".operations[N]");
        // otherwise null. Operates on the full id: a packageId can contain dots but never the
        // literal ".operations[", so stripping the trailing match is equivalent to closure.py's
        // split-on-'#'-then-regex.
        private static string SequenceParentKey(string patchId)
        {
            if (string.IsNullOrEmpty(patchId))
                return null;
            Match m = SeqChildSuffix.Match(patchId);
            return m.Success ? patchId.Substring(0, m.Index) : null;
        }

        // The container op type implied by a patchId's nesting tokens, or null if the op is
        // top-level (no container ancestor). Used only to phrase the changed-mod fallback. The
        // tokens come from ProvenanceRecorder's child labels (".operations[i]" for a
        // PatchOperationSequence, ".match"/".nomatch" for a PatchOperationConditional). We look
        // only at the suffix after '#' so a packageId that happens to contain ".match" can't
        // produce a false positive.
        private static string ContainerOpType(string patchId)
        {
            if (string.IsNullOrEmpty(patchId))
                return null;
            int hash = patchId.IndexOf('#');
            string suffix = hash >= 0 ? patchId.Substring(hash + 1) : patchId;
            if (suffix.IndexOf(".operations[", StringComparison.Ordinal) >= 0)
                return "PatchOperationSequence";
            if (suffix.IndexOf(".match", StringComparison.Ordinal) >= 0
                || suffix.IndexOf(".nomatch", StringComparison.Ordinal) >= 0)
                return "PatchOperationConditional";
            return null;
        }

        private static HashSet<string> AsSet(ICollection<string> ids)
        {
            if (ids is HashSet<string> set)
                return set;
            return ids == null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(ids, StringComparer.Ordinal);
        }
    }
}
