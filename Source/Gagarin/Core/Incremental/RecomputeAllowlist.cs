// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// RecomputeAllowlist.cs (recompute-fidelity ALLOWLIST — the safe-by-default MVP gate)
//
// Contains: a PURE static check that decides, from the dependency graph and the dirty+context sets,
// whether the sub-doc recompute can be trusted for THIS load. It is the INVERSE of a blocklist: the
// default answer is "fall back to a full rebuild", and we take the incremental path ONLY when every
// op that produces a dirty def is one of a small set of patterns proven faithful over a sub-doc.
//
// Why allowlist, not blocklist: DefRecompute re-applies every running mod's PatchOperations over a
// tiny <Defs> sub-doc holding only the dirty defs (+ SubDocExpander's sequence context + inheritance
// ancestors). That is faithful for ops whose effect is a pure, local function of the def they modify,
// but NOT for an op whose effect/branch depends on reading state absent from the sub-doc (cross-def
// conditional), whose match is positional (re-selects a different node in a smaller doc), or whose
// semantics we simply have not modelled (custom ops, capture gaps). A blocklist ("recompute unless we
// recognise a hazard") ships wrong data on any UNMODELLED hazard — a false negative with no full
// rebuild in production to catch it. An allowlist makes the unmodelled case SAFE: unknown => fall back
// => full rebuild => correct, just slower. Each newly-proven pattern only WIDENS the fast path; it can
// never introduce a correctness regression.
//
// v1 allowlist (exactly the patterns the live recompute gate has proven faithful — keeps every green
// test mode green and declines the cross-def gap, TestMods CASE 6):
//   (1) Pure LEAF ops {Add, Replace, Remove, AttributeSet/Add/Remove, SetName} with a NON-positional
//       xpath. A per-node leaf effect is identical and local, so a wildcard/defName selector is safe
//       even though it matches fewer nodes in the sub-doc (each matched dirty def still gets the right
//       local effect). Only a POSITIONAL def-selector ([n]/last()/position()/sibling-axis) can
//       re-match a different node in a smaller doc, so those fall back. (Covers CASE 1 + CASE 2.)
//   (2) SEQUENCE children — trusted because they are only reached after SubDocExpander pulled their
//       siblings into context (it would have set needsFullRebuild otherwise), so the sequence runs to
//       completion exactly as the full load did. The child op itself must still be a safe leaf.
//       (Covers CASE 3/4.)
//   (3) SAME-DEF / in-sub-doc CONDITIONALS — a conditional branch is allowed iff its parent's READ set
//       (the test's MatchedNodeIds) is wholly present in the sub-doc, so the test re-evaluates to the
//       same branch. A cross-def conditional (reads a def absent from the sub-doc) is NOT allowlisted.
//       (Covers CASE 5; declines CASE 6.)
// Everything else falls back with a category so the cause is logged and the backlog is frequency-
// ranked: unknown-op-kind (Insert, AddIf/ReplaceIf/RemoveIf, FindMod, AddModExtension, EditResearch,
// any custom op), positional-xpath, conditional-cross-def, dynamic-op (an op generated at runtime by an
// opaque enclosing op, attributed as "...generated[N]"), capture-gap (an empty/missing op type on a
// producing edge — an op the capture could not attribute).
//
// Producing edges: an op produces a dirty def D if it modifies D OR any of D's inheritance ANCESTORS
// (D inherits the patched ancestor's value). So we check every edge whose ModifiedNodeIds touches
// dirty ∪ ancestors(dirty). Parent conditional TEST edges are skipped (they do not modify anything;
// their .match/.nomatch children carry the effect and are checked there).
//
// Why pure: graph/set/string analysis over DependencyGraphData, no RimWorld types, unit-tested offline
// like SubDocExpander / DirtySetComputer. The live --expect-recompute-gap (declines) and default
// (admits) runs are the end-to-end proof.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Gagarin
{
    public static class RecomputeAllowlist
    {
        // Pure-leaf modifiers: effect is local to the matched node, so faithful over a sub-doc that
        // contains that node. (PatchOperationInsert is deliberately absent — it is positional.)
        private static readonly HashSet<string> SafeLeafOps = new HashSet<string>(StringComparer.Ordinal)
        {
            "PatchOperationAdd",
            "PatchOperationReplace",
            "PatchOperationRemove",
            "PatchOperationAttributeSet",
            "PatchOperationAttributeAdd",
            "PatchOperationAttributeRemove",
            "PatchOperationSetName",
        };

        // Positional / axis predicates that make def-SELECTION unstable when the same xpath is re-run
        // over a smaller sub-doc. Conservative: this also matches within-def predicates (e.g. li[2]),
        // which are actually safe — those merely fall back and are logged (a documented refinement),
        // never serve wrong data. Categories: positional-xpath.
        private static readonly Regex PositionalXpath = new Regex(
            @"\[\s*\d+\s*\]|\blast\s*\(|\bposition\s*\(|following-sibling|preceding-sibling",
            RegexOptions.Compiled);

        // Returns true when the sub-doc recompute is trusted for this load. When false, blockCategory
        // / blockReason explain why (for the metrics backlog) and the caller must full-rebuild.
        public static bool CanRecompute(
            DependencyGraphData graph,
            ICollection<string> dirtyIds,
            ICollection<string> contextIds,
            out string blockReason,
            out string blockCategory)
        {
            blockReason = null;
            blockCategory = null;

            var dirty = AsSet(dirtyIds);
            if (dirty.Count == 0)
                return true; // nothing to recompute — trivially safe (the splice changes nothing)

            if (graph == null)
            {
                blockCategory = "capture-gap";
                blockReason = "no dependency graph available to verify recompute safety";
                return false;
            }

            // The recompute sub-doc holds the dirty defs plus the context SubDocExpander pulled in.
            var subDoc = new HashSet<string>(dirty, StringComparer.Ordinal);
            if (contextIds != null)
                foreach (string id in contextIds)
                    subDoc.Add(id);

            // childNodeId -> parentNodeId, to walk a dirty def UP to its inheritance ancestors: an op
            // modifying an ancestor produces the dirty descendant's value, so it must be allowlisted too.
            var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (GraphInheritanceEdge e in graph.InheritanceEdges)
                if (!string.IsNullOrEmpty(e.ChildNodeId) && !string.IsNullOrEmpty(e.ParentNodeId))
                    parentOf[e.ChildNodeId] = e.ParentNodeId;

            // relevantTargets = dirty ∪ ancestors(dirty). An edge produces a dirty def iff it modifies
            // one of these.
            var relevantTargets = new HashSet<string>(dirty, StringComparer.Ordinal);
            foreach (string d in dirty)
            {
                string cur = d;
                while (parentOf.TryGetValue(cur, out string parent) && relevantTargets.Add(parent))
                    cur = parent;
            }

            // Conditional id -> its own test READ set, so a branch child can be checked against its
            // IMMEDIATE gating conditional. Keyed by every conditional edge's OWN PatchId — including a
            // conditional nested inside another conditional's branch, whose id (e.g. "mod#1.match") also
            // looks like a branch child of its OUTER parent. That outer-branch-child-ness is irrelevant
            // here: what matters is that THIS op is a Conditional, so it has its own read set to record.
            var conditionalReads = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (GraphPatchEdge edge in graph.PatchEdges)
                if (IsConditional(edge.OperationType))
                    conditionalReads[edge.PatchId] = edge.MatchedNodeIds;

            foreach (GraphPatchEdge edge in graph.PatchEdges)
            {
                // Skip conditional TEST edges entirely — they modify nothing (their spurious self-mark is
                // not a real effect); the effect is in their .match/.nomatch children, checked below. This
                // must skip EVERY conditional, not just top-level ones: a nested conditional's own id can
                // also look like a branch child of its outer parent (see conditionalReads above), but it
                // is still a test edge, not a producing edge.
                if (IsConditional(edge.OperationType))
                    continue;

                if (!TouchesRelevant(edge.ModifiedNodeIds, relevantTargets))
                    continue; // this op does not produce any dirty def — irrelevant

                // Capture gap: an op the capture could not attribute a kind to. Cannot prove safe.
                if (string.IsNullOrEmpty(edge.OperationType))
                {
                    Block("capture-gap",
                        $"producing op {edge.PatchId ?? "<unknown>"} has no recorded operation type", out blockReason, out blockCategory);
                    return false;
                }

                // Capture gap: an UNATTRIBUTED op. ProvenanceRecorder buckets any op it could not
                // map to a stable patchId at all (no enclosing op to attribute it to) under
                // "unindexed#{type}". Such an op keeps a real OperationType (e.g. PatchOperationReplace)
                // so without this it would sail through the safe-leaf test below — but we can't even say
                // which mod it belongs to, let alone reproduce it. Capture gap -> fall back.
                if (IsUnattributed(edge.PatchId))
                {
                    Block("capture-gap",
                        $"producing op {edge.PatchId} is unattributed (no enclosing op) — cannot prove recompute-safe", out blockReason, out blockCategory);
                    return false;
                }

                // A DYNAMICALLY GENERATED op (created during an enclosing op's Apply, now attributed to
                // it as "{parentId}.generated[N]"). It carries a real op type + sourceMod, but it is
                // still recompute-unsafe: we cannot reproduce the OPAQUE GENERATOR over a sub-doc (it
                // may read cross-def state or emit different children there). Distinct category from
                // capture-gap because the RISK IS ATTRIBUTED to a mod (which is what the deterministic
                // per-mod serve rule needs) — it just isn't admittable until the generator is modelled.
                if (IsGenerated(edge.PatchId))
                {
                    Block("dynamic-op",
                        $"producing op {edge.PatchId} is dynamically generated by an opaque op — cannot prove recompute-safe", out blockReason, out blockCategory);
                    return false;
                }

                // Not a known-safe leaf op (custom op, Insert, AddIf/ReplaceIf/RemoveIf, FindMod, …).
                if (!SafeLeafOps.Contains(edge.OperationType))
                {
                    Block("unknown-op-kind",
                        $"producing op {edge.PatchId} is {edge.OperationType}, not a proven-safe leaf op", out blockReason, out blockCategory);
                    return false;
                }

                // Positional def-selection re-matches a different node in a smaller sub-doc.
                if (edge.Xpath != null && PositionalXpath.IsMatch(edge.Xpath))
                {
                    Block("positional-xpath",
                        $"producing op {edge.PatchId} has a positional xpath ({edge.Xpath})", out blockReason, out blockCategory);
                    return false;
                }

                // A conditional branch's safe-leaf effect is still only faithful if the parent test
                // re-evaluates to the same branch — i.e. its read set is wholly in the sub-doc.
                if (IsBranchChild(edge.PatchId))
                {
                    string parentId = BranchParentId(edge.PatchId, conditionalReads);
                    if (parentId == null)
                    {
                        Block("capture-gap",
                            $"conditional branch {edge.PatchId} has no captured parent conditional", out blockReason, out blockCategory);
                        return false;
                    }
                    if (!ReadSetInSubDoc(conditionalReads[parentId], subDoc))
                    {
                        Block("conditional-cross-def",
                            $"cross-def conditional {parentId} reads a def absent from the sub-doc; its branch {edge.PatchId} produces a dirty def", out blockReason, out blockCategory);
                        return false;
                    }
                }
                // else: a top-level or sequence-child safe leaf op with a stable xpath — allowlisted.
                // (Sequence children are only reached because SubDocExpander already pulled their
                // siblings into context; otherwise it would have set needsFullRebuild upstream.)
            }

            return true; // every producing op is allowlisted
        }

        private static void Block(string category, string reason, out string blockReason, out string blockCategory)
        {
            blockCategory = category;
            blockReason = reason;
        }

        private static bool TouchesRelevant(List<string> modifiedNodeIds, HashSet<string> relevant)
        {
            if (modifiedNodeIds == null)
                return false;
            foreach (string id in modifiedNodeIds)
                if (relevant.Contains(id))
                    return true;
            return false;
        }

        private static bool ReadSetInSubDoc(List<string> readSet, HashSet<string> subDoc)
        {
            if (readSet == null)
                return true; // empty read set: the test selected nothing — reproducible in the sub-doc
            foreach (string id in readSet)
                if (!subDoc.Contains(id))
                    return false;
            return true;
        }

        // ProvenanceRecorder.RecordPatch buckets any op missing from the patch-id index under the
        // literal id "unindexed#{type}" (see its comment). The prefix is fixed and a real packageId
        // can never be "unindexed" with no dots before '#', so a prefix test is unambiguous.
        private static bool IsUnattributed(string patchId) =>
            patchId != null && patchId.StartsWith("unindexed#", StringComparison.Ordinal);

        // A dynamically-generated op's id carries ".generated[" (ProvenanceRecorder.EnterApply
        // synthesizes "{parentId}.generated[N]" when an op applied at runtime was never in the static
        // index). Look only after '#' so a packageId containing the literal can't false-positive.
        private static bool IsGenerated(string patchId)
        {
            if (string.IsNullOrEmpty(patchId))
                return false;
            int hash = patchId.IndexOf('#');
            string suffix = hash >= 0 ? patchId.Substring(hash + 1) : patchId;
            return suffix.IndexOf(".generated[", StringComparison.Ordinal) >= 0;
        }

        private static bool IsConditional(string opType) =>
            !string.IsNullOrEmpty(opType) &&
            opType.IndexOf("Conditional", StringComparison.Ordinal) >= 0;

        // A conditional branch child's patchId carries ".match"/".nomatch" after the parent id; a
        // parent conditional edge does not. Look only after '#' so a packageId containing the literal
        // cannot false-positive (mirrors SubDocExpander.ContainerOpType).
        private static bool IsBranchChild(string patchId)
        {
            if (string.IsNullOrEmpty(patchId))
                return false;
            int hash = patchId.IndexOf('#');
            string suffix = hash >= 0 ? patchId.Substring(hash + 1) : patchId;
            return suffix.IndexOf(".match", StringComparison.Ordinal) >= 0
                || suffix.IndexOf(".nomatch", StringComparison.Ordinal) >= 0;
        }

        // The parent conditional id of a branch child ("<parentId>.match[...]" / ".nomatch[...]"), or
        // null when it is not a child of a known conditional parent.
        //
        // Must cut at the LAST ".match"/".nomatch" segment, not the first: a conditional nested inside
        // another conditional's branch (or inside a sequence inside that branch) produces an id like
        // "mod#1.match.match" or "mod#1.match.operations[2].nomatch". Cutting at the first occurrence
        // would resolve to the OUTER conditional and check its (possibly in-sub-doc) read set instead of
        // the immediate gating conditional's — silently admitting a cross-def conditional nested under a
        // same-def one. That is a false "safe to recompute", not just an over-conservative fallback.
        //
        // Ids build outer-to-inner left-to-right, so the LAST ".match"/".nomatch" is the boundary right
        // before the leaf's own position — like a file path, the immediate parent is the last segment,
        // not the first. See RecomputeAllowlistTests.NestedConditional_InnerCrossDef_Declined.
        private static string BranchParentId(string patchId, Dictionary<string, List<string>> parents)
        {
            if (string.IsNullOrEmpty(patchId) || !IsBranchChild(patchId))
                return null;
            int match = patchId.LastIndexOf(".match", StringComparison.Ordinal);
            int nomatch = patchId.LastIndexOf(".nomatch", StringComparison.Ordinal);
            int cut = Math.Max(match, nomatch);
            string parentId = patchId.Substring(0, cut);
            return parents.ContainsKey(parentId) ? parentId : null;
        }

        private static HashSet<string> AsSet(ICollection<string> ids) =>
            ids is HashSet<string> set ? set
            : ids == null ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(ids, StringComparer.Ordinal);
    }
}
