// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// RecomputeSafetyPredictor.cs (recompute-fidelity safety predictor — the "never serve wrong data" gate)
//
// Contains: a PURE static check that, from the dependency graph and the dirty+context sets alone,
// decides whether the sub-doc recompute can be trusted for THIS load — or whether some dirty def's
// correct value depends on patch context the sub-doc cannot reproduce, so the load must fall back to
// a full rebuild.
//
// Why this exists: DefRecompute re-applies every running mod's PatchOperations over a tiny <Defs>
// sub-doc holding only the dirty defs (+ the sequence context SubDocExpander pulls in). That is
// faithful for ops whose effect is a pure function of the def they modify, but NOT for an op whose
// effect/branch depends on reading a DIFFERENT def that is absent from the sub-doc. The proven case
// (TestMods CASE 6, run_test.sh --expect-recompute-gap) is a cross-def PatchOperationConditional: it
// TESTS def P but its branch MODIFIES def E. When E is dirty it is recomputed over a sub-doc that
// lacks P, so the test flips to its nomatch branch and the recomputed E silently diverges from the
// full rebuild — with the dirty-set gate still a clean superset. Production has no full rebuild to
// diff against, so the only safety is to predict "this can't be recomputed faithfully" statically and
// fall back.
//
// The rule (conservative, superset-safe — false positives only cost an avoided incremental load, a
// false negative ships wrong data): a PatchOperationConditional is a fidelity hazard when
//   (a) its READ set (the parent edge's MatchedNodeIds — the nodes its test xpath selected) contains
//       any node NOT present in the recompute sub-doc (dirty + context), so the test would evaluate
//       differently there; AND
//   (b) its EFFECT (the ModifiedNodeIds of its .match/.nomatch child edges, propagated DOWN
//       inheritance so an effect on an abstract base reaches its concrete descendants) intersects the
//       DIRTY set — i.e. the unreliable branch choice actually changes a def we are recomputing.
// If any conditional is a hazard, the load is unsafe and must full-rebuild.
//
// Why this spares the SAFE same-def conditional (CASE 5): a conditional whose test reads the very def
// it modifies (or reads nothing) has its read set ⊆ the sub-doc (the def is dirty, hence present, and
// its own patches are re-applied), so (a) is false and it recomputes faithfully. This precision is
// what lets the default recompute path keep working instead of falling back on every conditional.
//
// Why pure: it is graph/set analysis over DependencyGraphData with no RimWorld types, unit-tested
// offline exactly like SubDocExpander / DirtySetComputer. The live --expect-recompute-gap run is the
// end-to-end proof that the hazard it predicts is the one the recompute gate observes.
//
// Scope (v1): PatchOperationConditional only — the proven gap and the dominant context-dependent op
// in real captures. Other potentially-unfaithful ops (positional PatchOperationInsert, mod-gated
// PatchOperationFindMod) are not yet modelled; they are follow-ups, and until then remain a known
// residual the recompute gate would catch in testing.

using System;
using System.Collections.Generic;

namespace Gagarin
{
    public static class RecomputeSafetyPredictor
    {
        // Returns true when the sub-doc recompute cannot be trusted for this load (some dirty def's
        // value depends on a cross-def read the sub-doc can't reproduce) and the caller must fall back
        // to the full rebuild. reason is a human-readable explanation for the metrics / gate report;
        // null when safe.
        public static bool IsUnsafe(
            DependencyGraphData graph,
            ICollection<string> dirtyIds,
            ICollection<string> contextIds,
            out string reason)
        {
            reason = null;
            if (graph == null)
                return false;

            var dirty = AsSet(dirtyIds);
            if (dirty.Count == 0)
                return false;

            // The recompute sub-doc holds exactly the dirty defs plus the context defs SubDocExpander
            // pulled in. A node a conditional reads is reproducible iff it is one of these.
            var subDoc = new HashSet<string>(dirty, StringComparer.Ordinal);
            if (contextIds != null)
                foreach (string id in contextIds)
                    subDoc.Add(id);

            // parentNodeId -> children, to propagate a conditional's effect DOWN the inheritance tree
            // (an effect on an abstract base changes every concrete def that inherits from it).
            var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (GraphInheritanceEdge e in graph.InheritanceEdges)
            {
                if (string.IsNullOrEmpty(e.ParentNodeId) || string.IsNullOrEmpty(e.ChildNodeId))
                    continue;
                if (!children.TryGetValue(e.ParentNodeId, out List<string> list))
                    children[e.ParentNodeId] = list = new List<string>();
                list.Add(e.ChildNodeId);
            }

            // Index every conditional PARENT edge by its patchId so we can attach its branch (child)
            // edges. A parent conditional's own xpath/Matched set is its TEST (read) set; its branch
            // ops are separate child edges keyed "<parentId>.match" / ".nomatch".
            var conditionalReads = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (GraphPatchEdge edge in graph.PatchEdges)
            {
                if (IsConditional(edge.OperationType) && !IsBranchChild(edge.PatchId))
                    conditionalReads[edge.PatchId] = edge.MatchedNodeIds;
            }
            if (conditionalReads.Count == 0)
                return false;

            // Collect each conditional's effect (its branch children's modified nodes).
            var conditionalEffects = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (GraphPatchEdge edge in graph.PatchEdges)
            {
                string parentId = BranchParentId(edge.PatchId, conditionalReads);
                if (parentId == null)
                    continue;
                if (!conditionalEffects.TryGetValue(parentId, out HashSet<string> eff))
                    conditionalEffects[parentId] = eff = new HashSet<string>(StringComparer.Ordinal);
                foreach (string nid in edge.ModifiedNodeIds)
                    eff.Add(nid);
            }

            foreach (KeyValuePair<string, List<string>> kv in conditionalReads)
            {
                List<string> readSet = kv.Value;
                // (a) does the test read a node the sub-doc cannot reproduce?
                if (!ReadsAbsentNode(readSet, subDoc))
                    continue;
                // (b) does the unreliable branch choice change a def we are recomputing?
                if (!conditionalEffects.TryGetValue(kv.Key, out HashSet<string> effect))
                    continue; // a conditional with no captured branch effect cannot change a dirty def
                if (EffectReachesDirty(effect, dirty, children))
                {
                    reason = $"cross-def conditional {kv.Key} reads a def absent from the sub-doc and " +
                             "its branch modifies a dirty def — recompute would diverge from a full rebuild";
                    return true;
                }
            }
            return false;
        }

        // True if any read-target node is not present in the sub-doc (so the test would select
        // differently when re-evaluated there). An empty read set is trivially reproducible.
        private static bool ReadsAbsentNode(List<string> readSet, HashSet<string> subDoc)
        {
            if (readSet == null)
                return false;
            foreach (string id in readSet)
                if (!subDoc.Contains(id))
                    return true;
            return false;
        }

        // True if the effect set — propagated down inheritance — intersects the dirty set. The branch
        // may modify an abstract base, so a dirty concrete descendant is reached transitively.
        private static bool EffectReachesDirty(
            HashSet<string> effect, HashSet<string> dirty, Dictionary<string, List<string>> children)
        {
            var stack = new Stack<string>(effect);
            var seen = new HashSet<string>(effect, StringComparer.Ordinal);
            while (stack.Count > 0)
            {
                string cur = stack.Pop();
                if (dirty.Contains(cur))
                    return true;
                if (children.TryGetValue(cur, out List<string> kids))
                    foreach (string ch in kids)
                        if (seen.Add(ch))
                            stack.Push(ch);
            }
            return false;
        }

        private static bool IsConditional(string opType) =>
            !string.IsNullOrEmpty(opType) &&
            opType.IndexOf("Conditional", StringComparison.Ordinal) >= 0;

        // A branch child's patchId carries a ".match" / ".nomatch" token after the parent id; a
        // parent conditional edge does not. We look only after '#' so a packageId containing the
        // literal cannot produce a false positive (mirrors SubDocExpander.ContainerOpType).
        private static bool IsBranchChild(string patchId)
        {
            if (string.IsNullOrEmpty(patchId))
                return false;
            int hash = patchId.IndexOf('#');
            string suffix = hash >= 0 ? patchId.Substring(hash + 1) : patchId;
            return suffix.IndexOf(".match", StringComparison.Ordinal) >= 0
                || suffix.IndexOf(".nomatch", StringComparison.Ordinal) >= 0;
        }

        // If patchId is a direct branch child of one of the known conditional parents, return that
        // parent's id; otherwise null. The branch id is "<parentId>.match[...]" / ".nomatch[...]".
        private static string BranchParentId(string patchId, Dictionary<string, List<string>> parents)
        {
            if (string.IsNullOrEmpty(patchId) || !IsBranchChild(patchId))
                return null;
            int match = patchId.IndexOf(".match", StringComparison.Ordinal);
            int nomatch = patchId.IndexOf(".nomatch", StringComparison.Ordinal);
            int cut = match >= 0 ? match : nomatch;
            if (nomatch >= 0 && (match < 0 || nomatch < match))
                cut = nomatch;
            string parentId = patchId.Substring(0, cut);
            return parents.ContainsKey(parentId) ? parentId : null;
        }

        private static HashSet<string> AsSet(ICollection<string> ids) =>
            ids is HashSet<string> set ? set
            : ids == null ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(ids, StringComparer.Ordinal);
    }
}
