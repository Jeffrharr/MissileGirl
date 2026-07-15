// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// SubDocExpander.cs (Piece D — Milestone 2b-2b: sub-doc sibling expansion)
//
// Contains: pure expansion that turns the dirty set into the larger CONTEXT set the
// recompute sub-doc must also contain — plus conservative fallback flags for cases the
// sub-doc recompute cannot do faithfully.
//
// Why it exists: the first sub-doc recompute (dead-end/m2b-2b-subdoc) put only dirty
// defs into the recompute <Defs> document, then re-ran every mod's PatchOperations over it.
// That breaks PatchOperationSequence: a sequence applies its child ops in order and ABORTS the
// whole sequence the moment one child's xpath finds no match (RimWorld's
// PatchOperationSequence.ApplyWorker returns on first failure). When only one def from a
// bundle is dirty, the sequence's other child ops target sibling defs absent from the
// sub-doc, so the sequence aborts early and the dirty def silently keeps an UN-patched value.
// The recompute gate caught 12 recompute mismatches this way: 12 defs recomputed to a value
// that lacked patches the full rebuild had applied.
//
// The fix (proven bounded via scripts/closure.py against real data — 245 dirty -> 337 sub-doc
// total, 1.17% of 28,682 defs): when a sequence touches a dirty def, also pull in the
// defs its SIBLING child ops touch, so the sequence finds all its targets and runs to
// completion exactly as the full load did. Those sibling defs are CONTEXT only — their resolved
// values are discarded (the baseline cache already holds them); they exist purely so the
// sequence doesn't abort.
//
// First fallback: sibling expansion trusts the BASELINE snapshot, which is sound for
// UNCHANGED mods (their sequence/conditional structure is exactly what was captured). But if a
// CHANGED mod itself owns the container op (Sequence / Conditional), the baseline execution
// path for it is stale — the edit may have reordered ops, changed a conditional's test, or
// moved which defs the sequence reaches — so the captured siblings no longer describe the
// current execution. Rather than risk an unfaithful recompute, we flag needsFullRebuild so the
// caller falls back to a full rebuild for this load. This is conservative (a changed mod owning
// a container op is rare) but never wrong: a full rebuild is always faithful.
//
// Second fallback (new-mod blind spot, run 20260714-160405): the container-op check above only
// walks the PRIOR graph's PatchEdges, so it is structurally blind to a mod that is wholly new
// this load — such a mod has ZERO edges in that graph, "changed mod owns a container op" can
// never fire for it, and a PatchOperationSequence it owns silently proceeds through recompute
// with its siblings un-replayed. (Live case: regrowth.botr.core's own PatchOperationSequence
// retextures Plant_Rice/Plant_Potato/etc — recompute produced the vanilla, un-retextured value
// for every def in that sequence except the ones already dirtied by other seeds.) We treat
// "changed AND absent from the prior load order entirely" the same as "owns a container op":
// both mean the baseline execution path for that mod cannot be trusted, so we decline rather
// than guess.
//
// Why pure: it is just graph/string analysis over DependencyGraphData, no RimWorld types, so it
// is unit-tested offline exactly like DirtySetComputer. It mirrors scripts/closure.py one-to-one
// so offline analysis and in-game expansion cannot drift.

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
        // newModIds (may be null): packageIds present in the current load order but absent from
        // the PRIOR one — mods the graph parameter has never captured at all. When one of them is
        // also in changedModIds (i.e. it owns patch content this load, not just raw defs), we
        // decline exactly like the container-op check: there is zero baseline evidence for what
        // its ops do, so trusting the incremental path would be a guess, not a proof.
        //
        // Returns the context ids (disjoint from dirtyIds). When needsFullRebuild is true the
        // returned set is empty and the caller must NOT recompute — either the changed mod owns a
        // container op, or it has no baseline representation at all (see file header).
        public static HashSet<string> Expand(
            DependencyGraphData graph,
            ICollection<string> dirtyIds,
            ICollection<string> changedModIds,
            out bool needsFullRebuild,
            out string fallbackReason,
            ICollection<string> newModIds = null)
        {
            needsFullRebuild = false;
            fallbackReason = null;
            var context = new HashSet<string>(StringComparer.Ordinal);
            if (graph == null)
                return context;

            var dirty = AsSet(dirtyIds);
            var changedMods = AsSet(changedModIds);
            var newMods = AsSet(newModIds);

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

            // Fallback: a changed mod that is wholly new this load has no edges anywhere in the
            // loop above to trip the container-op check — it simply never appears as a
            // GraphPatchEdge.SourceMod, whether or not it owns a container op. Check membership
            // directly against newModIds instead of relying on edge absence.
            if (!needsFullRebuild)
            {
                foreach (string mod in newMods)
                {
                    if (!changedMods.Contains(mod))
                        continue;
                    needsFullRebuild = true;
                    fallbackReason = $"new mod {mod} has no baseline representation in the prior graph";
                    break;
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
