// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DirtySetComputerTests.cs (Piece D — Milestone 1)
//
// Contains: the change-case matrix from Piece C (update / changed-patch / reorder-overlap /
// reorder-independent / remove / inheritance closure / no-op), re-expressed against the real
// DirtySetComputer + DependencyGraphData, plus a DependencyGraph.json parse test.
//
// Why: M1's correctness gate. Each case asserts the right nodes are dirty AND that
// independent changes do NOT over-dirty. (The precise wildcard-flip hazard is M2; not here.)

using System.Collections.Generic;
using System.Linq;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class DirtySetComputerTests
    {
        // A small graph shared by the matrix:
        //   patchEdges: modB#0 modifies Steel; modA#0 modifies Shared + X; modC#0 modifies Shared + Y.
        //   inheritance: Wall, Door inherit @BuildingBase; SteelWall inherits Wall (grandchild).
        private static DependencyGraphData BuildGraph()
        {
            var g = new DependencyGraphData { Version = 1 };
            foreach (var id in new[] { "ThingDef/Steel", "ThingDef/Shared", "ThingDef/X",
                                       "ThingDef/Y", "ThingDef/Wall", "ThingDef/Door",
                                       "ThingDef/SteelWall", "ThingDef@BuildingBase" })
                g.Nodes.Add(new GraphNode { Id = id, DefType = "ThingDef" });

            g.PatchEdges.Add(Edge("modB#0", "modB", "ThingDef/Steel"));
            g.PatchEdges.Add(Edge("modA#0", "modA", "ThingDef/Shared", "ThingDef/X"));
            g.PatchEdges.Add(Edge("modC#0", "modC", "ThingDef/Shared", "ThingDef/Y"));

            g.InheritanceEdges.Add(Inh("ThingDef/Wall", "ThingDef@BuildingBase"));
            g.InheritanceEdges.Add(Inh("ThingDef/Door", "ThingDef@BuildingBase"));
            g.InheritanceEdges.Add(Inh("ThingDef/SteelWall", "ThingDef/Wall"));
            return g;
        }

        private static GraphPatchEdge Edge(string patchId, string mod, params string[] modified)
        {
            var e = new GraphPatchEdge { PatchId = patchId, SourceMod = mod, OperationType = "PatchOperationReplace" };
            e.ModifiedNodeIds.AddRange(modified);
            e.MatchedNodeIds.AddRange(modified);
            return e;
        }

        private static GraphInheritanceEdge Inh(string child, string parent)
            => new GraphInheritanceEdge { ChildNodeId = child, ParentName = parent, ParentNodeId = parent };

        private static List<string> Order(params string[] mods) => mods.ToList();

        [Test]
        public void NoChange_EmptyDirtySet()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("modA", "modB", "modC"),
                CurrentLoadOrder = Order("modA", "modB", "modC")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            Assert.That(r.Nodes, Is.Empty);
        }

        [Test]
        public void ChangedDef_SeedsOnlyThatDef()
        {
            var change = new GraphChange
            {
                ChangedNodeIds = { "ThingDef/Steel" },
                PriorLoadOrder = Order("modA", "modB", "modC"),
                CurrentLoadOrder = Order("modA", "modB", "modC")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            // Steel has no inheritance children and no order change, so just Steel.
            Assert.That(r.Nodes, Is.EquivalentTo(new[] { "ThingDef/Steel" }));
        }

        [Test]
        public void ChangedParent_PropagatesInheritanceClosure()
        {
            var change = new GraphChange
            {
                ChangedNodeIds = { "ThingDef@BuildingBase" },
                PriorLoadOrder = Order("modA", "modB", "modC"),
                CurrentLoadOrder = Order("modA", "modB", "modC")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            // BuildingBase -> Wall, Door; Wall -> SteelWall (transitive).
            Assert.That(r.Nodes, Is.EquivalentTo(new[]
            {
                "ThingDef@BuildingBase", "ThingDef/Wall", "ThingDef/Door", "ThingDef/SteelWall"
            }));
        }

        [Test]
        public void AddedDef_SeedsItAndCountsSeedAddedDefs()
        {
            // P2 — a newly-added concrete def (no baseline node) is seeded directly. The driver
            // (DirtySetDiagnostic) restricts membership to genuinely-new, changed-file ids; the
            // pure computer just folds whatever it is handed into the dirty set, exactly as it
            // does for ChangedNodeIds. "ThingDef/Brand" has no inheritance children here, so the
            // result is just itself — and it is counted under SeedAddedDefs, not SeedChangedDefs.
            var change = new GraphChange
            {
                AddedNodeIds = { "ThingDef/Brand" },
                PriorLoadOrder = Order("modA", "modB", "modC"),
                CurrentLoadOrder = Order("modA", "modB", "modC")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            Assert.That(r.Nodes, Is.EquivalentTo(new[] { "ThingDef/Brand" }));
            Assert.That(r.SeedAddedDefs, Is.EqualTo(1));
            Assert.That(r.SeedChangedDefs, Is.EqualTo(0));
        }

        [Test]
        public void AddedDef_PropagatesInheritanceClosure()
        {
            // An added id that the baseline graph already has inheritance edges out of must fan
            // out to its descendants just like any other seed — the closure runs over ALL seeds,
            // added ones included. Seeding @BuildingBase via AddedNodeIds dirties Wall, Door, and
            // (transitively) SteelWall, with the seed itself counted under SeedAddedDefs. This is
            // the same fixpoint the changed-parent test asserts, reached through the new channel.
            var change = new GraphChange
            {
                AddedNodeIds = { "ThingDef@BuildingBase" },
                PriorLoadOrder = Order("modA", "modB", "modC"),
                CurrentLoadOrder = Order("modA", "modB", "modC")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            Assert.That(r.Nodes, Is.EquivalentTo(new[]
            {
                "ThingDef@BuildingBase", "ThingDef/Wall", "ThingDef/Door", "ThingDef/SteelWall"
            }));
            Assert.That(r.SeedAddedDefs, Is.EqualTo(1));        // only the base is a seed
            Assert.That(r.InheritanceAdded, Is.EqualTo(3));     // Wall, Door, SteelWall via closure
        }

        [Test]
        public void AddedDef_AlreadyDirtyViaChangedDef_NotDoubleCounted()
        {
            // If an id is both a changed-def seed and an added seed, the first Add wins the count.
            // Seed 1 (changed defs) runs before Seed 5 (added defs), so it is counted as a changed
            // def and SeedAddedDefs stays 0 — the dirty set is unaffected (idempotent), and the
            // metrics don't double-attribute the node. Guards the seed-ordering contract.
            var change = new GraphChange
            {
                ChangedNodeIds = { "ThingDef/Steel" },
                AddedNodeIds = { "ThingDef/Steel" },
                PriorLoadOrder = Order("modA", "modB", "modC"),
                CurrentLoadOrder = Order("modA", "modB", "modC")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            Assert.That(r.Nodes, Is.EquivalentTo(new[] { "ThingDef/Steel" }));
            Assert.That(r.SeedChangedDefs, Is.EqualTo(1));
            Assert.That(r.SeedAddedDefs, Is.EqualTo(0));
        }

        [Test]
        public void ChangedModPatches_DirtyTheirModifiedNodesOnly()
        {
            var change = new GraphChange
            {
                ChangedMods = { "modB" },
                PriorLoadOrder = Order("modA", "modB", "modC"),
                CurrentLoadOrder = Order("modA", "modB", "modC")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            // modB#0 modifies only Steel; modA/modC patches are NOT seeded.
            Assert.That(r.Nodes, Is.EquivalentTo(new[] { "ThingDef/Steel" }));
        }

        [Test]
        public void Reorder_Overlap_DirtiesSharedNode_IndependentStayClean()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("modA", "modB", "modC"),
                CurrentLoadOrder = Order("modC", "modB", "modA")   // modA <-> modC swapped
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            // Shared is patched by BOTH modA and modC, so the swap changes its patch order -> dirty.
            // X (only modA) and Y (only modC) keep a one-element sequence -> clean (no over-dirty).
            Assert.That(r.Nodes, Contains.Item("ThingDef/Shared"));
            Assert.That(r.Nodes, Does.Not.Contain("ThingDef/X"));
            Assert.That(r.Nodes, Does.Not.Contain("ThingDef/Y"));
            Assert.That(r.Nodes, Does.Not.Contain("ThingDef/Steel")); // only modB touches it
        }

        [Test]
        public void Remove_DirtiesNodesTouchedByTheRemovedModsPatch()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("modA", "modB", "modC"),
                CurrentLoadOrder = Order("modA", "modB")           // modC removed
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            // modC#0 modified Shared + Y, so both lose a patch from their sequence -> dirty.
            Assert.That(r.Nodes, Contains.Item("ThingDef/Shared"));
            Assert.That(r.Nodes, Contains.Item("ThingDef/Y"));
            // X (only modA) is untouched by the removal.
            Assert.That(r.Nodes, Does.Not.Contain("ThingDef/X"));
        }

        [Test]
        public void DependencyGraphData_Parse_ReadsSchema()
        {
            const string json =
                "{\"version\":1," +
                "\"nodes\":[{\"id\":\"ThingDef/Steel\",\"defType\":\"ThingDef\",\"defName\":\"Steel\"," +
                  "\"sourceMod\":\"modA\",\"sourceFile\":\"a.xml\"}]," +
                "\"patchEdges\":[{\"patchId\":\"modB#0\",\"sourceMod\":\"modB\"," +
                  "\"operationType\":\"PatchOperationReplace\",\"xpath\":\"x\"," +
                  "\"matchedNodeIds\":[\"ThingDef/Steel\"],\"modifiedNodeIds\":[\"ThingDef/Steel\"]}]," +
                "\"inheritanceEdges\":[{\"childNodeId\":\"ThingDef/Wall\",\"parentName\":\"BuildingBase\"," +
                  "\"parentNodeId\":\"ThingDef@BuildingBase\"}]," +
                "\"metrics\":{\"nodeCount\":1}}";

            var g = DependencyGraphData.Parse(json);
            Assert.That(g.Version, Is.EqualTo(1));
            Assert.That(g.Nodes, Has.Count.EqualTo(1));
            Assert.That(g.Nodes[0].SourceFile, Is.EqualTo("a.xml"));
            Assert.That(g.PatchEdges, Has.Count.EqualTo(1));
            Assert.That(g.PatchEdges[0].ModifiedNodeIds, Is.EquivalentTo(new[] { "ThingDef/Steel" }));
            Assert.That(g.InheritanceEdges[0].ParentNodeId, Is.EqualTo("ThingDef@BuildingBase"));
        }

        [Test]
        public void DependencyGraphData_Parse_NullParentNodeId_BecomesNull()
        {
            const string json =
                "{\"version\":1,\"nodes\":[],\"patchEdges\":[]," +
                "\"inheritanceEdges\":[{\"childNodeId\":\"ThingDef/Foo\",\"parentName\":\"Missing\"," +
                  "\"parentNodeId\":null}],\"metrics\":{}}";
            var g = DependencyGraphData.Parse(json);
            Assert.That(g.InheritanceEdges[0].ParentNodeId, Is.Null);
        }
    }
}
