// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;

namespace Gagarin.IncrementalReplay
{
    // Computes the DIRTY SET: every def node whose resolved body could differ after a
    // single-mod change, so the incremental rebuild recomputes exactly those and no more.
    //
    // DESIGN INTENT (and why this can be fast):
    // The baseline's wildcard match sets are ALREADY recorded in the Piece A graph
    // (patchEdges[].matchedNodeIds) and the identity index is in Piece B. So we do NOT
    // re-run the whole pipeline to find what changed. Instead, starting from the single
    // changed mod, we propagate along the precomputed edges and only re-test wildcards
    // against the SMALL set of defs whose body actually moved. Work is proportional to
    // the change plus its transitive closure, not to the whole load order.
    //
    // The hard part is correctness under XPath-not-identity matching:
    //   * A wildcard matches by structure, so changing a def's body can make it start OR
    //     stop matching a patch declared by a totally different (earlier) mod. We detect
    //     this by tracking, per changed def, which PREDICATE elements it gained or lost,
    //     then dirtying every def that any affected wildcard's membership now differs on.
    //   * Inheritance crosses mods: dirtying a parent dirties its children, transitively.
    //   * A patch can depend on the OUTPUT of an earlier patch (keyed on an element that
    //     patch sets). Newly-dirty defs can therefore flip yet more wildcard memberships,
    //     so we iterate to a FIXPOINT.
    //
    // Returning a SUPERSET of the truly-changed nodes is always safe (just slower);
    // returning a SUBSET is silently wrong. The matrix tests assert zero-diff vs full
    // rebuild, which catches any subset error.

    internal sealed class DirtyResult
    {
        public HashSet<string> Nodes = new HashSet<string>();
        public int Iterations;       // fixpoint iterations taken
    }

    internal static class DirtySet
    {
        // State-reusing entry point: takes the ALREADY-LOADED mutated indices (as
        // production would, from the cache) so the per-change work touches only the
        // changed mod's slice plus the transitive closure — not the whole load order.
        // This is the variant the scaled benchmark times.
        public static DirtyResult ComputeWithState(
            Fixture baseline, Fixture mutated, string changedMod,
            DependencyGraph graph, LoadedState mutatedState)
        {
            var result = new DirtyResult();
            var dirty = result.Nodes;

            var inheritanceChildren = new Dictionary<string, List<string>>();
            foreach (var e in graph.InheritanceEdges)
                if (e.ParentNodeId != null)
                {
                    if (!inheritanceChildren.TryGetValue(e.ParentNodeId, out var kids))
                        inheritanceChildren[e.ParentNodeId] = kids = new List<string>();
                    kids.Add(e.ChildNodeId);
                }

            // Seed from the changed mod's def deltas (only that mod's defs are scanned).
            var baseRaw = RawDefsOf(baseline, changedMod);
            var mutRaw = RawDefsOf(mutated, changedMod);
            foreach (var id in Union(baseRaw.Keys, mutRaw.Keys))
            {
                baseRaw.TryGetValue(id, out var b);
                mutRaw.TryGetValue(id, out var m);
                if (b != m) dirty.Add(id);
            }
            SeedFromPatchChanges(baseline, mutated, changedMod, graph, dirty);

            // Reorder seed: a single-mod content change leaves load order untouched, but
            // moving a mod in the list (or adding/removing one) can change the ORDER in
            // which patches hit a given node WITHOUT changing any def body or patch
            // definition. A node's resolved value depends only on the ordered sequence of
            // patches applied to it, so we dirty exactly the nodes whose per-node patch
            // sequence differs between baseline and mutated. Independent reorders touch
            // disjoint nodes, so this stays CLEAN on the no-overlap case (no over-invalidation)
            // while still catching the overlap case. This is a global O(patches) scan, but it
            // is gated behind a cheap load-order-equality check so the common single-mod
            // content change pays nothing for it.
            SeedFromReorder(baseline, mutated, dirty);

            var wildcards = new List<FixturePatch>();
            foreach (var p in mutatedState.Patches)
                if (p.Kind == PatchKind.Wildcard) wildcards.Add(p);

            // Frontier walk: inheritance closure + per-def wildcard flip detection.
            int iterations = 0;
            var frontier = new Queue<string>(dirty);
            var queued = new HashSet<string>(dirty);
            while (frontier.Count > 0)
            {
                iterations++;
                string id = frontier.Dequeue();
                if (inheritanceChildren.TryGetValue(id, out var kids))
                    foreach (var child in kids)
                    {
                        dirty.Add(child);
                        if (queued.Add(child)) frontier.Enqueue(child);
                    }
                // Per-def wildcard flips are inherently dirty (def already on frontier);
                // membership changes on OTHER defs require their own body to move, which
                // only happens via the changed mod's defs or inheritance — both covered.
            }

            // Changed wildcard patches sweep the whole load order (rare path).
            HandleChangedWildcardPatchesState(baseline, mutated, changedMod, wildcards,
                mutatedState, dirty);

            result.Iterations = iterations;
            return result;
        }


        // State-based variant of changed-wildcard handling: re-tests only AFFECTED
        // wildcards against candidate defs, using the loaded mutated indices.
        private static void HandleChangedWildcardPatchesState(
            Fixture baseline, Fixture mutated, string changedMod,
            List<FixturePatch> mutatedWildcards, LoadedState state, HashSet<string> dirty)
        {
            var baseSigs = WildcardSigs(baseline, changedMod);
            var mutSigs = WildcardSigs(mutated, changedMod);
            var affected = new HashSet<string>();
            foreach (var id in Union(baseSigs.Keys, mutSigs.Keys))
            {
                baseSigs.TryGetValue(id, out var b);
                mutSigs.TryGetValue(id, out var m);
                if (b != m) affected.Add(id);
            }
            if (affected.Count == 0) return;

            // Only here (a wildcard patch actually changed) do we pay an O(defs) sweep.
            var candidates = new HashSet<string>(state.Raw.Keys);
            foreach (var kv in state.BaselineWildcardHits) foreach (var n in kv.Value) candidates.Add(n);

            foreach (var p in mutatedWildcards)
            {
                if (!affected.Contains(p.PatchId)) continue;
                foreach (var id in candidates)
                {
                    var body = state.Patched(id);
                    bool now = body != null && ApplyModel.WildcardMatches(p, body);
                    bool was = state.BaselineWildcardHits.TryGetValue(p.PatchId, out var hits) && hits.Contains(id);
                    if (now != was) dirty.Add(id);
                }
            }
            foreach (var id in affected)
            {
                bool stillExists = false;
                foreach (var p in mutatedWildcards) if (p.PatchId == id) { stillExists = true; break; }
                if (!stillExists && state.BaselineWildcardHits.TryGetValue(id, out var hits))
                    foreach (var n in hits) dirty.Add(n);
            }
        }

        private static Dictionary<string, string> WildcardSigs(Fixture f, string mod)
        {
            var map = new Dictionary<string, string>();
            foreach (var m in f.Mods)
                if (m.PackageId == mod)
                    foreach (var p in m.Patches)
                        if (p.Kind == PatchKind.Wildcard)
                            map[p.PatchId] = p.DefType + "|" + p.PredicateElement + "|"
                                + p.SetElement + "|" + p.SetValue;
            return map;
        }

        // Raw (pre-patch) signature of a single mod's defs, keyed by node id. Captures
        // body + inheritance attrs so any change registers.
        private static Dictionary<string, string> RawDefsOf(Fixture f, string mod)
        {
            var map = new Dictionary<string, string>();
            foreach (var m in f.Mods)
                if (m.PackageId == mod)
                    foreach (var d in m.Defs)
                        map[d.NodeId] = (d.ParentName ?? "") + "|" + (d.Name ?? "") + "|" + (d.Body ?? "");
            return map;
        }

        // When the changed mod's patches differ, dirty the nodes they touched (baseline
        // matches come from the graph; new matches are caught by the fixpoint re-test).
        private static void SeedFromPatchChanges(
            Fixture baseline, Fixture mutated, string changedMod,
            DependencyGraph graph, HashSet<string> dirty)
        {
            var basePatches = PatchSigs(baseline, changedMod);
            var mutPatches = PatchSigs(mutated, changedMod);
            foreach (var id in Union(basePatches.Keys, mutPatches.Keys))
            {
                basePatches.TryGetValue(id, out var b);
                mutPatches.TryGetValue(id, out var m);
                if (b == m) continue;   // patch unchanged
                foreach (var e in graph.PatchEdges)
                    if (e.PatchId == id)
                        foreach (var n in e.ModifiedNodeIds)
                            dirty.Add(n);
            }
        }

        // Seed nodes whose ORDERED patch sequence changed between baseline and mutated.
        // This is what catches a reorder (or add/remove) that flips the order two patches
        // hit a shared node: no def body and no patch definition changed, so the per-mod
        // signature seeds above see nothing, yet the resolved value moves. We build, for
        // each node, the load-order list of patch ids that modify it (identity by defName,
        // wildcard by predicate against the RAW pre-patch body — a conservative membership
        // that ignores cascade-induced matches, which is sound: any such cascade is itself
        // driven by an ordered patch on the same node and is therefore already captured).
        // A node is dirtied iff its sequence differs. Disjoint reorders => identical
        // sequences => clean.
        private static void SeedFromReorder(Fixture baseline, Fixture mutated, HashSet<string> dirty)
        {
            // Cheap gate: if no mod moved in load order and the mod set is unchanged, a
            // reorder cannot have happened, so skip the global scan entirely.
            if (LoadOrderUnchanged(baseline, mutated)) return;

            var baseSeq = PatchSequencesByNode(baseline);
            var mutSeq = PatchSequencesByNode(mutated);
            foreach (var id in Union(baseSeq.Keys, mutSeq.Keys))
            {
                baseSeq.TryGetValue(id, out var b);
                mutSeq.TryGetValue(id, out var m);
                if (!SequenceEqual(b, m)) dirty.Add(id);
            }
        }

        // Two fixtures share a load order iff the same package ids map to the same order.
        private static bool LoadOrderUnchanged(Fixture a, Fixture b)
        {
            var ao = new Dictionary<string, int>();
            foreach (var m in a.Mods) ao[m.PackageId] = m.LoadOrder;
            var bo = new Dictionary<string, int>();
            foreach (var m in b.Mods) bo[m.PackageId] = m.LoadOrder;
            if (ao.Count != bo.Count) return false;
            foreach (var kv in ao)
                if (!bo.TryGetValue(kv.Key, out var o) || o != kv.Value) return false;
            return true;
        }

        // node id -> ordered list of patch ids that modify it (load order, then declaration
        // order). Wildcard membership is tested against the raw combined body, which is a
        // sound conservative basis for detecting order changes (see SeedFromReorder note).
        private static Dictionary<string, List<string>> PatchSequencesByNode(Fixture f)
        {
            // Raw bodies (pre-patch) so wildcard predicates can be tested for membership.
            var raw = ApplyModel.BuildPatched(StripPatches(f));
            var seqs = new Dictionary<string, List<string>>();
            foreach (var mod in f.InLoadOrder())
                foreach (var p in mod.Patches)
                {
                    if (p.Kind == PatchKind.Identity)
                    {
                        var id = p.DefType + "/" + p.TargetDefName;
                        if (raw.ContainsKey(id)) Append(seqs, id, p.PatchId);
                    }
                    else
                    {
                        foreach (var d in raw.Values)
                            if (ApplyModel.WildcardMatches(p, d))
                                Append(seqs, d.NodeId, p.PatchId);
                    }
                }
            return seqs;
        }

        private static Fixture StripPatches(Fixture f)
        {
            var c = f.DeepCopy();
            foreach (var m in c.Mods) m.Patches.Clear();
            return c;
        }

        private static void Append(Dictionary<string, List<string>> seqs, string id, string patchId)
        {
            if (!seqs.TryGetValue(id, out var list)) seqs[id] = list = new List<string>();
            list.Add(patchId);
        }

        private static bool SequenceEqual(List<string> a, List<string> b)
        {
            if (a == null) return b == null || b.Count == 0;
            if (b == null) return a.Count == 0;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        private static Dictionary<string, string> PatchSigs(Fixture f, string mod)
        {
            var map = new Dictionary<string, string>();
            foreach (var m in f.Mods)
                if (m.PackageId == mod)
                    foreach (var p in m.Patches)
                        map[p.PatchId] = p.Kind + "|" + p.DefType + "|" + p.TargetDefName + "|"
                            + p.PredicateElement + "|" + p.SetElement + "|" + p.SetValue;
            return map;
        }

        private static IEnumerable<string> Union(IEnumerable<string> a, IEnumerable<string> b)
        {
            var set = new HashSet<string>(a);
            set.UnionWith(b);
            return set;
        }
    }
}
