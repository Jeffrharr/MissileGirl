// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DirtySetComputer.cs (Piece D — Milestone 1: dirty-set diagnostic)
//
// Contains: the pure dirty-set algorithm — given a prior build's DependencyGraph and a
// description of what changed (changed def nodes, changed-patch mods, prior/current load
// order), it names the def nodes that would need recomputing.
//
// Used for: the M1 diagnostic. The RimWorld-facing DirtySetDiagnostic builds the GraphChange
// from the live load (asset-hash diff + load orders) and feeds it here. Kept free of any
// RimWorld dependency so it can be unit-tested offline against the C change-case matrix.
//
// Why / scope: this is the production re-implementation of the algorithm Piece C proved on
// synthetic fixtures. M1 computes the STRUCTURAL dirty set — changed defs, the nodes a
// changed mod's patches modify, nodes whose ordered patch sequence moved (reorder/remove),
// and the transitive inheritance closure of all of those. It deliberately does NOT do the
// precise wildcard-membership-flip re-test (the XPath-not-identity hazard: a changed def
// newly matching an unchanged mod's wildcard). That needs the actual def bodies and arrives
// with the real recompute in M2. So the M1 dirty set is a sound LOWER bound used to size the
// prize and validate the seeding/closure mechanics — not yet the superset-safe set a
// recompute requires.

using System.Collections.Generic;

namespace Gagarin
{
    // What changed since the build that produced the graph. Built by the diagnostic driver
    // from the asset-hash diff and the load orders; pure data so the algorithm stays testable.
    public sealed class GraphChange
    {
        // Def nodes whose own source file changed (and the defs contributed by newly-added
        // mods). Identity by node id, matching the graph's node ids.
        public HashSet<string> ChangedNodeIds = new HashSet<string>();

        // Mods whose PATCH files changed — every patch they declare is treated as suspect
        // (mod-granular: the graph records patch sourceMod but not sourceFile). A pure def
        // edit leaves this empty, so it does not over-dirty.
        public HashSet<string> ChangedMods = new HashSet<string>();

        // Package ids in load order, at graph-build time and now. Equal lists ⇒ no reorder.
        public List<string> PriorLoadOrder = new List<string>();
        public List<string> CurrentLoadOrder = new List<string>();
    }

    public sealed class DirtyResult
    {
        public readonly HashSet<string> Nodes = new HashSet<string>();
        public int SeedChangedDefs;     // nodes seeded by a changed def file
        public int SeedPatchModified;   // nodes seeded by a changed mod's patches
        public int SeedReorder;         // nodes seeded by an order change
        public int InheritanceAdded;    // nodes added by the inheritance closure
        public int Iterations;          // inheritance-closure frontier steps
    }

    public static class DirtySetComputer
    {
        public static DirtyResult Compute(DependencyGraphData graph, GraphChange change)
        {
            var result = new DirtyResult();
            var dirty = result.Nodes;

            // Seed 1 — changed def bodies.
            foreach (var id in change.ChangedNodeIds)
                if (id != null && dirty.Add(id))
                    result.SeedChangedDefs++;

            // Seed 2 — a changed mod's patches: dirty every node they modified in the baseline.
            // Mod-granular and conservative (we cannot tell which patch in the mod changed).
            if (change.ChangedMods.Count > 0)
            {
                foreach (var edge in graph.PatchEdges)
                {
                    if (edge.SourceMod == null || !change.ChangedMods.Contains(edge.SourceMod))
                        continue;
                    foreach (var n in edge.ModifiedNodeIds)
                        if (dirty.Add(n))
                            result.SeedPatchModified++;
                }
            }

            // Seed 3 — order change. A node's resolved value depends on the ORDERED sequence
            // of patches applied to it; moving a mod (or removing one) can change that order
            // without changing any def body or patch definition. Dirty exactly the nodes whose
            // per-node patch-id sequence differs. Gated behind a cheap load-order check so the
            // common single-mod content change pays nothing.
            if (!SameOrder(change.PriorLoadOrder, change.CurrentLoadOrder))
            {
                var priorSeq = PatchSequencesByNode(graph, change.PriorLoadOrder, dropRemoved: false);
                var curSeq = PatchSequencesByNode(graph, change.CurrentLoadOrder, dropRemoved: true);
                var nodes = new HashSet<string>(priorSeq.Keys);
                nodes.UnionWith(curSeq.Keys);
                foreach (var id in nodes)
                {
                    priorSeq.TryGetValue(id, out var a);
                    curSeq.TryGetValue(id, out var b);
                    if (!SequenceEqual(a, b) && dirty.Add(id))
                        result.SeedReorder++;
                }
            }

            // Propagation — inheritance closure to a fixpoint: dirtying a parent dirties its
            // children transitively. (Wildcard-flip propagation is M2; see file header.)
            var children = BuildInheritanceChildren(graph);
            var frontier = new Queue<string>(dirty);
            var queued = new HashSet<string>(dirty);
            while (frontier.Count > 0)
            {
                result.Iterations++;
                var id = frontier.Dequeue();
                if (!children.TryGetValue(id, out var kids))
                    continue;
                foreach (var child in kids)
                {
                    if (dirty.Add(child))
                        result.InheritanceAdded++;
                    if (queued.Add(child))
                        frontier.Enqueue(child);
                }
            }

            return result;
        }

        // ParentNodeId -> child node ids, from the resolved inheritance edges.
        private static Dictionary<string, List<string>> BuildInheritanceChildren(DependencyGraphData graph)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var e in graph.InheritanceEdges)
            {
                if (e.ParentNodeId == null || e.ChildNodeId == null)
                    continue;
                if (!map.TryGetValue(e.ParentNodeId, out var list))
                    map[e.ParentNodeId] = list = new List<string>();
                list.Add(e.ChildNodeId);
            }
            return map;
        }

        // node id -> ordered list of patch ids that modify it, in apply order: by the source
        // mod's position in the given load order, then by patch id (stable within a mod, since
        // the hierarchical id encodes declaration/nesting order). When dropRemoved is set,
        // edges whose source mod is absent from the load order (a removed mod) are skipped, so
        // a node that had a removed patch in its sequence registers a difference.
        private static Dictionary<string, List<string>> PatchSequencesByNode(
            DependencyGraphData graph, List<string> loadOrder, bool dropRemoved)
        {
            var orderIndex = new Dictionary<string, int>();
            for (int i = 0; i < loadOrder.Count; i++)
                orderIndex[loadOrder[i]] = i;

            // Order edges deterministically by (mod load position, patch id).
            var edges = new List<GraphPatchEdge>(graph.PatchEdges);
            edges.Sort((x, y) =>
            {
                int ix = ModPos(orderIndex, x.SourceMod);
                int iy = ModPos(orderIndex, y.SourceMod);
                if (ix != iy) return ix.CompareTo(iy);
                return string.CompareOrdinal(x.PatchId ?? "", y.PatchId ?? "");
            });

            var seqs = new Dictionary<string, List<string>>();
            foreach (var edge in edges)
            {
                int pos = ModPos(orderIndex, edge.SourceMod);
                if (dropRemoved && pos == int.MaxValue)
                    continue; // mod no longer present
                foreach (var node in edge.ModifiedNodeIds)
                {
                    if (!seqs.TryGetValue(node, out var list))
                        seqs[node] = list = new List<string>();
                    list.Add(edge.PatchId);
                }
            }
            return seqs;
        }

        private static int ModPos(Dictionary<string, int> orderIndex, string mod)
            => mod != null && orderIndex.TryGetValue(mod, out var i) ? i : int.MaxValue;

        private static bool SameOrder(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
                if (a[i] != b[i]) return false;
            return true;
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
    }
}
