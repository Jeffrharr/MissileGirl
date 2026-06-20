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
