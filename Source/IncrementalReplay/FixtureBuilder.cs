// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// FixtureBuilder.cs (Piece C — replay harness)
//
// Contains: BuildGraph and BuildClassification — they replay a fixture through the
// apply model to discover real patch matches and inheritance edges, emitting the
// Piece A DependencyGraph and Piece B PatchClassification objects.
//
// Used for: synthesizing real-shaped Piece A/B inputs for the harness so the
// dirty-set algorithm consumes contract data rather than fabricated stubs.
//
// Why: Pieces A and B may not be merged when this harness runs, so it stands in
// for them; building the graph by actually replaying patches (rather than guessing
// matches) ensures the cross-mod match sets are exactly what Piece A would record.

using System.Collections.Generic;

namespace Gagarin.IncrementalReplay
{
    // Derives the Piece A graph and Piece B classification from a fixture by actually
    // running the apply model and recording what each patch matched/modified. This is
    // the harness standing in for Pieces A/B (which "may not be merged yet"): the JSON
    // contracts it emits are exactly the schemas those pieces promise, so the dirty-set
    // algorithm consumes real-shaped data.

    internal static class FixtureBuilder
    {
        public static DependencyGraph BuildGraph(Fixture fixture)
        {
            var g = new DependencyGraph();

            // Nodes: every declared def (last-writer-wins on duplicate ids).
            var seen = new Dictionary<string, GraphNode>();
            foreach (var mod in fixture.InLoadOrder())
                foreach (var d in mod.Defs)
                    seen[d.NodeId] = new GraphNode
                    {
                        Id = d.NodeId, DefType = d.DefType,
                        DefName = d.DefName, SourceMod = mod.PackageId
                    };
            g.Nodes.AddRange(seen.Values);

            // Patch edges: replay patches against the raw doc to discover real matches,
            // exactly the cross-mod-aware matching Piece A would record.
            var raw = ApplyModel.BuildPatched(EmptyPatches(fixture)); // raw, no patches applied
            foreach (var mod in fixture.InLoadOrder())
                foreach (var p in mod.Patches)
                {
                    var edge = new PatchEdge
                    {
                        PatchId = p.PatchId, SourceMod = mod.PackageId,
                        OperationType = p.Kind == PatchKind.Identity
                            ? "PatchOperationReplace" : "PatchOperationReplace",
                        Xpath = XpathFor(p)
                    };
                    if (p.Kind == PatchKind.Identity)
                    {
                        var id = p.DefType + "/" + p.TargetDefName;
                        if (raw.ContainsKey(id))
                        {
                            edge.MatchedNodeIds.Add(id);
                            edge.ModifiedNodeIds.Add(id);
                        }
                    }
                    else
                    {
                        foreach (var d in raw.Values)
                            if (ApplyModel.WildcardMatches(p, d))
                            {
                                edge.MatchedNodeIds.Add(d.NodeId);
                                edge.ModifiedNodeIds.Add(d.NodeId);
                            }
                    }
                    g.PatchEdges.Add(edge);
                }

            // Inheritance edges: child -> parent, parent resolved by Name + load order.
            var byName = new Dictionary<string, List<(string id, int order)>>();
            foreach (var mod in fixture.InLoadOrder())
                foreach (var d in mod.Defs)
                    if (!string.IsNullOrEmpty(d.Name))
                    {
                        if (!byName.TryGetValue(d.Name, out var list))
                            byName[d.Name] = list = new List<(string, int)>();
                        list.Add((d.NodeId, mod.LoadOrder));
                    }
            foreach (var mod in fixture.InLoadOrder())
                foreach (var d in mod.Defs)
                    if (!string.IsNullOrEmpty(d.ParentName) && byName.TryGetValue(d.ParentName, out var cands))
                    {
                        string parentId = null;
                        int bestOrder = int.MinValue;
                        foreach (var c in cands)
                            if (c.order <= mod.LoadOrder && c.order > bestOrder)
                            {
                                bestOrder = c.order;
                                parentId = c.id;
                            }
                        g.InheritanceEdges.Add(new InheritanceEdge
                        {
                            ChildNodeId = d.NodeId, ParentName = d.ParentName, ParentNodeId = parentId
                        });
                    }
            return g;
        }

        public static PatchClassification BuildClassification(Fixture fixture)
        {
            var c = new PatchClassification();
            foreach (var mod in fixture.InLoadOrder())
                foreach (var p in mod.Patches)
                {
                    var info = new PatchInfo
                    {
                        PatchId = p.PatchId, SourceMod = mod.PackageId,
                        Classification = p.Kind == PatchKind.Identity ? "identity" : "wildcard",
                        Predicate = p.Kind == PatchKind.Wildcard ? p.PredicateElement : null
                    };
                    if (p.Kind == PatchKind.Identity)
                    {
                        info.TargetDefNames.Add(p.TargetDefName);
                        var id = p.DefType + "/" + p.TargetDefName;
                        if (!c.IdentityIndex.TryGetValue(id, out var list))
                            c.IdentityIndex[id] = list = new List<string>();
                        list.Add(p.PatchId);
                    }
                    c.Patches.Add(info);
                }
            return c;
        }

        // Build a fixture clone with all patches removed, to obtain the raw (pre-patch)
        // working set for match discovery.
        private static Fixture EmptyPatches(Fixture fixture)
        {
            var f = fixture.DeepCopy();
            foreach (var m in f.Mods) m.Patches.Clear();
            return f;
        }

        private static string XpathFor(FixturePatch p)
        {
            return p.Kind == PatchKind.Identity
                ? $"Defs/{p.DefType}[defName=\"{p.TargetDefName}\"]/{p.SetElement}"
                : $"Defs/{p.DefType}[{p.PredicateElement}]/{p.SetElement}";
        }
    }
}
