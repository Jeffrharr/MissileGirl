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

        // Mods whose DEFS files changed this load (not patch-scoped, unlike ChangedMods above).
        // Feeds ComputeDefOverrideFlips (issue #50): a mod already present in both prior and
        // current load order can still become a def's new real owner if its own Defs file is
        // edited to newly declare a defName another mod already owns — that has no bearing on
        // patch edges, so it's tracked separately from ChangedMods rather than widening it.
        public HashSet<string> ChangedDefFileMods = new HashSet<string>();

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
        public int SeedDefOverride;     // nodes seeded by a def-override owning-mod add/remove (issue #43)
        public int SeedDefOverrideRematch; // nodes seeded by a newly-added mod's current-content override (issue #43 add-direction)
        public int SeedOwnerModRemoved;  // nodes seeded because their sole recorded owner mod left the load order
        public int SeedTypeProvider;    // nodes seeded by a custom-Def-type provider mod add/remove (issue #86)
        public int InheritanceAdded;    // nodes added by the inheritance closure
        public int SeedUnresolvedFanout; // children fanned out via the unresolved-edge safety net (Change C)
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
            IEnumerable<string> wildcardFlipSeeds = null,
            IEnumerable<string> defOverrideRematchSeeds = null)
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

            // Seed 7 — def-override owning-mod flips (issue #43). A def can be replaced
            // outright by a later mod re-declaring the same defName (Verse.DefDatabase<T>.Add:
            // last-loaded registration wins, remove-then-add, no PatchOperation involved). Such
            // a def lives in an UNCHANGED vanilla/earlier-mod file with no patch edge at all, so
            // seeds 1-6 never reach it. Mirrors Seed 6's XOR: dirty every def the baseline
            // recorded as owned (last-registered) by a packageId present in exactly one of the
            // two load orders. NOTE: this only covers REMOVAL -- it requires the BASELINE graph
            // to already have a defOverrides entry for the flipping mod, true only if that mod
            // was present when the baseline was captured. For a mod ADDED for the first time
            // this load, see the defOverrideRematchSeeds fold further below.
            if (graph.DefOverrides != null && graph.DefOverrides.Count > 0)
            {
                var prior = new HashSet<string>(
                    change.PriorLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                var current = new HashSet<string>(
                    change.CurrentLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var kv in graph.DefOverrides)
                {
                    if (prior.Contains(kv.Key) == current.Contains(kv.Key))
                        continue;
                    foreach (var id in kv.Value)
                        if (id != null && dirty.Add(id))
                            result.SeedDefOverride++;
                }
            }

            // Seed 7b — def-override REMATCH (issue #43 add-direction). Seed 7 above can only
            // dirty an override the BASELINE graph already knows about (the overriding mod was
            // present at capture time). A mod added for the FIRST time this load contributes an
            // override the baseline graph never saw, so Seed 7 has nothing to XOR against. The
            // driver re-tests each newly-added mod's CURRENT def ids against the baseline's
            // recorded owners (see DefOverrideRematch) and passes any newly-detected override
            // here. Folded in before inheritance closure, like the wildcard-flip seeds.
            if (defOverrideRematchSeeds != null)
            {
                foreach (var id in defOverrideRematchSeeds)
                    if (id != null && dirty.Add(id))
                        result.SeedDefOverrideRematch++;
            }

            // Seed 8 — sole-owner mod removed. A def defined by exactly ONE mod (no override,
            // so it never gets a defOverrides entry -- Seed 7 can't reach it) still needs to
            // disappear from the splice when that mod leaves the load order entirely. The def
            // lives in that mod's own UNCHANGED file with no patch edge pointing at it, so
            // seeds 1-7 never reach it either. Mirrors Seed 6/7's XOR, but keyed on every
            // captured node's own SourceMod rather than a dedicated override/MayRequire index
            // -- this is the general "the raw file that defines this node is no longer loaded"
            // case. A mod ADDED for the first time has no baseline node to XOR against here
            // (Seed 5 already handles a newly-added def as a fresh node), so this seed is
            // asymmetric by design: it only fires on removal.
            //
            // Fallback owner for patch-injected top-level defs: RimWorld's own
            // ParseAndProcessXML keys `loadingAsset` by original-file-node identity
            // (assetlookup, populated once per file in CombineIntoUnifiedXML). A brand-new
            // top-level def element spliced in by a PatchOperationAdd (xpath targeting the
            // <Defs> root, not an existing def) was never one of those original per-file
            // nodes, so the lookup misses and `loadingAsset` is null -- RegisterNode then
            // captures `SourceMod=null/SourceFile=null` even though the def is very much
            // owned by whichever mod's patch created it. That left such nodes permanently
            // unreachable by the SourceMod-XOR above (a null SourceMod never satisfies
            // `prior.Contains`), so removing their real owning mod silently failed to dirty
            // them -- the RBM_UnguligradeLegs live gap (2026-07-09 EDGE_CASES_LOOP_PROGRESS).
            // We recover an effective owner from the capture's own patch edges: any edge
            // whose id appears in the capture's patchInjectedOwners index (populated by
            // ProvenanceRecorder.RecordAddedChildren for Add-shaped operations -- see that
            // method's comment) was created directly by that mod's patch, so its SourceMod
            // is the real owner for the XOR check below, not an approximation.
            //
            // Gated behind the same load-order check as Seed 3: the XOR below can only ever
            // fire when the mod set differs between loads, so a pure content-only change
            // (mod set identical) is guaranteed to add nothing -- skip the O(N) walk over
            // every captured node entirely rather than pay it for a certain no-op (issue #63).
            if (!SameOrder(change.PriorLoadOrder, change.CurrentLoadOrder)
                && graph.Nodes != null && graph.Nodes.Count > 0)
            {
                var prior = new HashSet<string>(
                    change.PriorLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                var current = new HashSet<string>(
                    change.CurrentLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var node in graph.Nodes)
                {
                    if (node.Id == null)
                        continue;
                    string owner = node.SourceMod;
                    if (string.IsNullOrEmpty(owner))
                        graph.PatchInjectedOwners.TryGetValue(node.Id, out owner);
                    if (string.IsNullOrEmpty(owner))
                        continue;
                    if (!prior.Contains(owner) || current.Contains(owner))
                        continue;
                    if (dirty.Add(node.Id))
                        result.SeedOwnerModRemoved++;
                }
            }

            // Seed 9 — custom Def-type provider mod add/remove (issue #86). A def instance's
            // .NET Type can be supplied by a DIFFERENT mod's C# assembly than the mod whose
            // XML declares the instance (e.g. FacialAnimation.EyeballColorDef, a type from
            // nals.facialanimation, instantiated 34 times in oppey.eyegenes2's own unpatched
            // XML). When the type-providing mod leaves the load, that Type disappears, so
            // RimWorld can no longer construct ANY instance of it in ANY mod's XML -- even one
            // whose own file never changed, has no patches touching these defs, and carries no
            // MayRequire anywhere. Seeds 1-8 have nothing to hook: the consuming mod is
            // unchanged and the dependency is purely "this def's Type came from mod X's
            // assembly" (see AssemblyOwnerLookup / ProvenanceRecorder.RegisterNode). Mirrors
            // Seed 6's XOR exactly, just keyed on TypeProviderIndex instead of MayRequireIndex.
            // Placed before the inheritance closure so a flipped type-provided base still fans
            // out to descendants.
            if (graph.TypeProviderIndex != null && graph.TypeProviderIndex.Count > 0)
            {
                var prior = new HashSet<string>(
                    change.PriorLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                var current = new HashSet<string>(
                    change.CurrentLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var kv in graph.TypeProviderIndex)
                {
                    // XOR: only a packageId that entered or left the load flips the defs
                    // whose Type it provides.
                    if (prior.Contains(kv.Key) == current.Contains(kv.Key))
                        continue;
                    foreach (var id in kv.Value)
                        if (id != null && dirty.Add(id))
                            result.SeedTypeProvider++;
                }
            }

            // Propagation — inheritance closure to a fixpoint: dirtying a parent dirties its
            // children transitively. Runs over ALL seeds above (including the wildcard flips),
            // so a newly-matched abstract base still fans out to its descendants.
            var children = BuildInheritanceChildren(graph);
            var frontier = new Queue<string>(dirty);
            var queued = new HashSet<string>(dirty);
            PropagateClosure(children, dirty, frontier, queued, ref result.Iterations,
                ref result.InheritanceAdded);

            // Change C — conservative over-dirty safety net for UNRESOLVED edges only. Defects
            // A/B should resolve every real inheritance edge, but if any ParentName still has
            // no parentNodeId (e.g. a base whose node we never recorded), the resolved-edge
            // closure above cannot reach its children — they'd be silently dropped from the
            // dirty set, the exact subset error this fix targets. As a fail-safe, we match a
            // dirtied node's NAME (the segment after '@' or '/' in its id) against the
            // parentName of unresolved edges and dirty those children. This is strictly gated
            // to unresolved edges (resolved-edge propagation is untouched), so it can only
            // over-dirty in the unresolved-name-collision case, never under-dirty.
            var unresolvedByParentName = BuildUnresolvedChildrenByParentName(graph);
            if (unresolvedByParentName.Count > 0)
            {
                // Snapshot the currently-dirty ids: only EXISTING dirty nodes seed the
                // fan-out, and we re-feed any additions through the resolved closure below.
                foreach (var id in new List<string>(dirty))
                {
                    string name = NameFromId(id);
                    if (name == null || !unresolvedByParentName.TryGetValue(name, out var kids))
                        continue;
                    foreach (var child in kids)
                    {
                        if (dirty.Add(child))
                            result.SeedUnresolvedFanout++;
                        if (queued.Add(child))
                            frontier.Enqueue(child);
                    }
                }

                // A child fanned out here may itself be a resolved parent of further nodes,
                // so push the additions back through the resolved closure to a fixpoint.
                PropagateClosure(children, dirty, frontier, queued, ref result.Iterations,
                    ref result.InheritanceAdded);
            }

            return result;
        }

        // BFS the resolved-edge inheritance closure to a fixpoint, draining frontier. Shared
        // by the main propagation and the Change C re-propagation so both use identical logic.
        private static void PropagateClosure(Dictionary<string, List<string>> children,
            HashSet<string> dirty, Queue<string> frontier, HashSet<string> queued,
            ref int iterations, ref int inheritanceAdded)
        {
            while (frontier.Count > 0)
            {
                iterations++;
                var id = frontier.Dequeue();
                if (!children.TryGetValue(id, out var kids))
                    continue;
                foreach (var child in kids)
                {
                    if (dirty.Add(child))
                        inheritanceAdded++;
                    if (queued.Add(child))
                        frontier.Enqueue(child);
                }
            }
        }

        // Change C: parentName -> child node ids, built ONLY from UNRESOLVED edges (null
        // parentNodeId). These are edges the resolved-edge closure cannot follow; the safety
        // net keys them by the bare parentName so a dirtied node whose Name matches can still
        // reach them.
        private static Dictionary<string, List<string>> BuildUnresolvedChildrenByParentName(
            DependencyGraphData graph)
        {
            var map = new Dictionary<string, List<string>>();
            foreach (var e in graph.InheritanceEdges)
            {
                if (e.ParentNodeId != null || e.ChildNodeId == null || string.IsNullOrEmpty(e.ParentName))
                    continue;
                if (!map.TryGetValue(e.ParentName, out var list))
                    map[e.ParentName] = list = new List<string>();
                list.Add(e.ChildNodeId);
            }
            return map;
        }

        // The bare Name of a node from its id: the segment after '@' for an abstract node
        // ("{DefType}@{Name}") or after '/' for a concrete node ("{DefType}/{defName}").
        // Returns null for an id with neither separator. This is the value a child's
        // ParentName would name, so it is what the unresolved-edge map is keyed by.
        private static string NameFromId(string id)
        {
            if (id == null)
                return null;
            int at = id.IndexOf('@');
            if (at >= 0)
                return id.Substring(at + 1);
            int slash = id.IndexOf('/');
            if (slash >= 0)
                return id.Substring(slash + 1);
            return null;
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
