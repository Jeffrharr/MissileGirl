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
// synthetic fixtures. It computes the STRUCTURAL dirty set — changed defs, the nodes a
// changed mod's patches modify, nodes whose ordered patch sequence moved (reorder/remove),
// and the transitive inheritance closure of all of those. The one part it cannot do alone is
// the wildcard-membership-flip re-test (the XPath-not-identity hazard: a changed mod's patch
// predicate newly matching an otherwise-unchanged def), because that needs the actual def
// bodies and this computer is deliberately RimWorld/XML-free. M2a closes that gap by having
// the driver compute those flips with WildcardRematch and pass them in as wildcardFlipSeeds,
// which are folded in before the closure. With them the result is the SUPERSET a recompute
// requires; without them (the M1 path) it is a sound LOWER bound for sizing the prize.

using System;
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

        // ADDED defs (P2 — the added-defs channel): concrete def ids that exist in the CURRENT
        // load but are absent from the baseline graph (a brand-new mod, or new defs in an edited
        // file). They have no baseline node, so none of the structural seeds below reach them and
        // the real-engine gate would correctly FAIL them as silently-stale. We seed them directly
        // (Seed 5) so the recompute/splice path produces and inserts them on THIS load. Only
        // CONCRETE ids belong here (abstract @Name bases are never cache items; a dirty concrete
        // descendant pulls its abstract ancestors in via DefRecompute.AddAncestors). The driver
        // restricts membership to ids whose owning file actually changed, so this stays orthogonal
        // to P1 (uncaptured def TYPES whose files didn't change) and never mass-over-dirties.
        public HashSet<string> AddedNodeIds = new HashSet<string>();

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
        public int SeedWildcardFlip;    // nodes seeded by a wildcard membership flip (M2a)
        public int SeedAddedDefs;       // nodes seeded as newly-added defs absent from baseline (P2)
        public int SeedMayRequire;      // nodes seeded by a MayRequire flip on a mod add/remove (P4)
        public int InheritanceAdded;    // nodes added by the inheritance closure
        public int Iterations;          // inheritance-closure frontier steps
    }

    public static class DirtySetComputer
    {
        // wildcardFlipSeeds (M2a): def ids a changed mod's patch NEWLY matches against the
        // current raw def bodies, which the structural seeds below cannot see (they need the
        // actual def XML; this computer is deliberately RimWorld/XML-free). The driver
        // computes them via WildcardRematch and passes them here so they are folded in BEFORE
        // the inheritance closure — a newly-matched abstract parent must still fan out to its
        // descendants. Null/empty for the pure-structural M1 path and its tests.
        public static DirtyResult Compute(DependencyGraphData graph, GraphChange change,
            IEnumerable<string> wildcardFlipSeeds = null)
        {
            var result = new DirtyResult();
            var dirty = result.Nodes;

            // Seed 1 — changed def bodies.
            foreach (var id in change.ChangedNodeIds)
                if (id != null && dirty.Add(id))
                    result.SeedChangedDefs++;

            // Seed 5 — ADDED defs (P2). Defs present in the current load but absent from the
            // baseline graph (a newly-added mod, or new defs in an edited file). Folded in here
            // alongside Seed 1 — BEFORE the inheritance closure — so a newly-added concrete def
            // is dirtied, recomputed, and spliced in on this load. We deliberately seed it as a
            // single concrete node and rely on DefRecompute.AddAncestors to pull its (possibly
            // also-new) abstract parents from the CURRENT raw bodies; the baseline closure below
            // cannot fan out to a node that has no baseline inheritance edges anyway. Numbered 5
            // (after the structural seeds 1-4) to keep the existing seed identities stable while
            // ordering it with Seed 1 in the algorithm — both seed concrete def bodies directly.
            foreach (var id in change.AddedNodeIds)
                if (id != null && dirty.Add(id))
                    result.SeedAddedDefs++;

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

            // Seed 4 — wildcard membership flips (M2a). Supplied by the driver from a re-test
            // of changed mods' patch xpaths against the current raw def bodies: defs a changed
            // predicate now matches that it did not before. These are otherwise-unchanged defs,
            // so none of seeds 1-3 reach them — this is the one gap that keeps M1 a lower bound
            // rather than the superset a recompute requires. See WildcardRematch.
            if (wildcardFlipSeeds != null)
            {
                foreach (var id in wildcardFlipSeeds)
                    if (id != null && dirty.Add(id))
                        result.SeedWildcardFlip++;
            }

            // Seed 6 — MayRequire flips (P4). When a mod enters or leaves the active set, any
            // def the baseline indexed against that packageId (via a MayRequire /
            // MayRequireAnyOf on its own content, or on patch-injected content) flips inclusion
            // or value. Such a def lives in an UNCHANGED mod with an UNCHANGED file, so seeds
            // 1-5 never reach it — this is the gap that silently serves stale FactionDef /
            // ThingStyleDef content on a mod add/remove. We dirty every indexed def whose gating
            // mod is present in exactly one of the two load orders (a true add or remove); mods
            // present in both (or neither) cannot have flipped, so they are skipped. Placed
            // before the inheritance closure so a flipped abstract base still fans out.
            if (graph.MayRequireIndex != null && graph.MayRequireIndex.Count > 0)
            {
                var prior = new HashSet<string>(
                    change.PriorLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                var current = new HashSet<string>(
                    change.CurrentLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var kv in graph.MayRequireIndex)
                {
                    // XOR: only a packageId that entered or left the load flips its gated defs.
                    if (prior.Contains(kv.Key) == current.Contains(kv.Key))
                        continue;
                    foreach (var id in kv.Value)
                        if (id != null && dirty.Add(id))
                            result.SeedMayRequire++;
                }
            }

            // Propagation — inheritance closure to a fixpoint: dirtying a parent dirties its
            // children transitively. Runs over ALL seeds above (including the wildcard flips),
            // so a newly-matched abstract base still fans out to its descendants.
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
