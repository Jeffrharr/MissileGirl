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

        // A graph carrying only a MayRequire index (no patch/inheritance edges), so Seed 6 is
        // exercised in isolation: "vmemese" gates a ThingStyleDef (its own root) and a FactionDef
        // (patch-injected content); "ludeon.rimworld.biotech" gates an unrelated def.
        private static DependencyGraphData BuildMayRequireGraph()
        {
            var g = new DependencyGraphData { Version = 1 };
            g.MayRequireIndex["vanillaexpanded.vmemese"] = new List<string>
            {
                "ThingStyleDef/TST_Hedonist_KneelSheet", "FactionDef/Crows_VelosEnclave"
            };
            g.MayRequireIndex["ludeon.rimworld.biotech"] = new List<string> { "ThingDef/Gene_X" };
            return g;
        }

        [Test]
        public void MayRequireFlip_RemovedMod_DirtiesGatedDefs()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats", "vanillaexpanded.vmemese"),
                CurrentLoadOrder = Order("toastyman.moreritualseats")   // vmemese removed
            };
            var r = DirtySetComputer.Compute(BuildMayRequireGraph(), change);
            Assert.That(r.Nodes, Contains.Item("ThingStyleDef/TST_Hedonist_KneelSheet"));
            Assert.That(r.Nodes, Contains.Item("FactionDef/Crows_VelosEnclave"));
            Assert.That(r.SeedMayRequire, Is.EqualTo(2));
            // A def gated on a DIFFERENT mod (biotech), present in neither load, must stay clean.
            Assert.That(r.Nodes, Does.Not.Contain("ThingDef/Gene_X"));
        }

        // Issue #40 proxy: PatchOperationFindMod ("oppey.eyegenes2" wraps a 34-op Sequence
        // in a FindMod testing FacialAnimation's presence) feeds ITS captured branch node
        // ids into this SAME mayRequire index (see ProvenanceRecorder.IndexFindMod). The
        // actual reflection capture is RimWorld-coupled and can only be verified live
        // (--expect-* live fixture, follow-up); this test locks in the "index reuse"
        // contract Seed 6 already provides once fed -- exercised here at real-world scale
        // (33 GeneDefs, matching the live-run archived gate miss) rather than a toy count.
        private static DependencyGraphData BuildFindModGraph()
        {
            var g = new DependencyGraphData { Version = 1 };
            var geneIds = new List<string>();
            for (int i = 0; i < 33; i++)
                geneIds.Add($"GeneDef/Eyes_Color{i}");
            g.MayRequireIndex["nals.facialanimation"] = geneIds;
            return g;
        }

        [Test]
        public void MayRequireFlip_FindModGatedSequence_DirtiesAllBranchDefs()
        {
            // oppey.eyegenes2 (the mod OWNING the FindMod op) never changes -- only the
            // gating mod (FacialAnimation) leaves the load, exactly like the live gate miss.
            var change = new GraphChange
            {
                PriorLoadOrder = Order("oppey.eyegenes2", "nals.facialanimation"),
                CurrentLoadOrder = Order("oppey.eyegenes2")   // FacialAnimation removed
            };
            var r = DirtySetComputer.Compute(BuildFindModGraph(), change);
            Assert.That(r.SeedMayRequire, Is.EqualTo(33));
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Color0"));
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Color32"));
        }

        [Test]
        public void MayRequireFlip_AddedMod_DirtiesGatedDefs()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats"),
                CurrentLoadOrder = Order("toastyman.moreritualseats", "vanillaexpanded.vmemese")
            };
            var r = DirtySetComputer.Compute(BuildMayRequireGraph(), change);
            Assert.That(r.Nodes, Contains.Item("ThingStyleDef/TST_Hedonist_KneelSheet"));
            Assert.That(r.Nodes, Contains.Item("FactionDef/Crows_VelosEnclave"));
        }

        [Test]
        public void MayRequire_ModPresentInBoth_NoFlip()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats", "vanillaexpanded.vmemese"),
                CurrentLoadOrder = Order("vanillaexpanded.vmemese", "toastyman.moreritualseats")
            };
            var r = DirtySetComputer.Compute(BuildMayRequireGraph(), change);
            // vmemese is in BOTH loads (only reordered) -> no inclusion flip -> no MayRequire seed.
            Assert.That(r.SeedMayRequire, Is.EqualTo(0));
            Assert.That(r.Nodes, Does.Not.Contain("ThingStyleDef/TST_Hedonist_KneelSheet"));
        }

        [Test]
        public void MayRequire_PackageIdMatch_IsCaseInsensitive()
        {
            var g = new DependencyGraphData { Version = 1 };
            // Index keyed with the FactionDef-patch casing; load order uses the ThingStyleDef
            // casing. RimWorld treats these as the same mod, so the flip must still fire.
            g.MayRequireIndex["VanillaExpanded.VMemesE"] =
                new List<string> { "FactionDef/Crows_VelosEnclave" };
            var change = new GraphChange
            {
                PriorLoadOrder = Order("vanillaexpanded.vmemese"),
                CurrentLoadOrder = Order()
            };
            var r = DirtySetComputer.Compute(g, change);
            Assert.That(r.Nodes, Contains.Item("FactionDef/Crows_VelosEnclave"));
        }

        [Test]
        public void MayRequire_EmptyIndex_NoOpAndNoOverDirty()
        {
            // The base matrix graph has no MayRequire index; a pure mod-add must not be seeded
            // by Seed 6 (P2/other seeds own that), proving Seed 6 stays inert when unused.
            var change = new GraphChange
            {
                PriorLoadOrder = Order("modA", "modB"),
                CurrentLoadOrder = Order("modA", "modB", "modNew")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            Assert.That(r.SeedMayRequire, Is.EqualTo(0));
        }

        [Test]
        public void DependencyGraphData_Parse_ReadsMayRequireIndex_CaseInsensitive()
        {
            const string json =
                "{\"version\":1,\"nodes\":[],\"patchEdges\":[],\"inheritanceEdges\":[]," +
                "\"mayRequire\":{\"VanillaExpanded.VMemesE\":[\"FactionDef/Crows_VelosEnclave\"," +
                  "\"ThingStyleDef/TST_Hedonist_KneelSheet\"]},\"metrics\":{}}";
            var g = DependencyGraphData.Parse(json);
            Assert.That(g.MayRequireIndex, Has.Count.EqualTo(1));
            // Lookup with different casing must resolve (OrdinalIgnoreCase).
            Assert.That(g.MayRequireIndex["vanillaexpanded.vmemese"],
                Is.EquivalentTo(new[]
                {
                    "FactionDef/Crows_VelosEnclave", "ThingStyleDef/TST_Hedonist_KneelSheet"
                }));
        }

        [Test]
        public void DependencyGraphData_Parse_PreP4Graph_HasEmptyMayRequireIndex()
        {
            // A graph written before P4 has no "mayRequire" field; parsing must yield an empty
            // index (not throw) so old caches load and Seed 6 simply no-ops.
            const string json =
                "{\"version\":1,\"nodes\":[],\"patchEdges\":[],\"inheritanceEdges\":[],\"metrics\":{}}";
            var g = DependencyGraphData.Parse(json);
            Assert.That(g.MayRequireIndex, Is.Empty);
        }

        // Issue #43: a def replaced outright by a later mod re-declaring the same defName
        // (no PatchOperation involved -- Verse.DefDatabase<T>.Add: last-loaded registration
        // wins). "oppey.eyegenes2" owns (last-registered) two vanilla GeneDefs it never
        // patches; an unrelated mod owns an unrelated def.
        private static DependencyGraphData BuildDefOverrideGraph()
        {
            var g = new DependencyGraphData { Version = 1 };
            g.DefOverrides["oppey.eyegenes2"] = new List<string>
            {
                "GeneDef/Eyes_Red", "GeneDef/Eyes_Gray"
            };
            g.DefOverrides["ludeon.rimworld.biotech"] = new List<string> { "GeneDef/Gene_X" };
            return g;
        }

        [Test]
        public void DefOverride_RemovedOwningMod_DirtiesOverriddenDefs()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats", "oppey.eyegenes2"),
                CurrentLoadOrder = Order("toastyman.moreritualseats")   // eyegenes2 removed
            };
            var r = DirtySetComputer.Compute(BuildDefOverrideGraph(), change);
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Red"));
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Gray"));
            Assert.That(r.SeedDefOverride, Is.EqualTo(2));
            // A def owned by a DIFFERENT mod, present in neither load, must stay clean.
            Assert.That(r.Nodes, Does.Not.Contain("GeneDef/Gene_X"));
        }

        [Test]
        public void DefOverride_AddedOwningMod_DirtiesOverriddenDefs()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats"),
                CurrentLoadOrder = Order("toastyman.moreritualseats", "oppey.eyegenes2")
            };
            var r = DirtySetComputer.Compute(BuildDefOverrideGraph(), change);
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Red"));
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Gray"));
        }

        [Test]
        public void DefOverride_ThreeWayChain_RemovingStaleIntermediateOwnerStillDirties()
        {
            // Three-way override chain (vanilla -> modA -> modB): modB is the CURRENT owner, but
            // capture never prunes modA's now-stale defOverrides entry for the same node (see
            // ProvenanceGraphTests.AddNode_ThreeWayChain_KeepsStaleIntermediateOwnerInDefOverrides).
            // Removing modA -- even though modB already owns the content either way, so nothing
            // actually changes -- must still fire Seed 7 via the stale entry. This is a safe
            // over-approximation (an unnecessary but harmless dirty), not a correctness bug.
            var g = new DependencyGraphData { Version = 1 };
            g.DefOverrides["modA.eyegenes"] = new List<string> { "GeneDef/Eyes_Red" };
            g.DefOverrides["modB.eyegenes2"] = new List<string> { "GeneDef/Eyes_Red" };

            var change = new GraphChange
            {
                PriorLoadOrder = Order("modA.eyegenes", "modB.eyegenes2"),
                CurrentLoadOrder = Order("modB.eyegenes2")   // only modA removed; modB stays
            };
            var r = DirtySetComputer.Compute(g, change);
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Red"));
            Assert.That(r.SeedDefOverride, Is.EqualTo(1));
        }

        // KNOWN GAP (issue #50) — an EXISTING mod's file edit that changes which mod owns an
        // already-owned def is currently uncaught. Baseline chain modA -> modC (modC owns
        // GeneDef/Eyes_Red). modB was already present in both loads (not newly added/removed)
        // but its Defs file is edited this load to newly declare Eyes_Red, and modB now loads
        // after modC -- a real ownership/content change. None of today's seeds see it:
        //   - Seed 1 needs the BASELINE node's SourceFile among changedAssets, but baseline
        //     records modC's file, not modB's -- modB's edit never maps to this id.
        //   - Seed 5 (added defs) skips ids already present in baseline.
        //   - Seed 7 needs the owning packageId to flip LOAD PRESENCE; modB's presence didn't
        //     change.
        //   - Seed 7b (DefOverrideRematch, #45) is gated on newlyAddedMods; modB isn't newly
        //     added to the modlist, only its file changed.
        // This test asserts TODAY'S (gap) behavior -- flip the assertion to Contains.Item once
        // #50 lands a fix (a rematch seed keyed off change.ChangedMods rather than
        // newlyAddedMods).
        [Test]
        public void KnownGap_ExistingModFileEditChangesOwnership_NotCaughtByAnySeed()
        {
            var g = new DependencyGraphData { Version = 1 };
            g.Nodes.Add(new GraphNode
            {
                Id = "GeneDef/Eyes_Red",
                SourceMod = "modC.eyegenes",
                SourceFile = "/Mods/modC/Defs/GeneDefs.xml"
            });

            var change = new GraphChange
            {
                // modB was present in BOTH loads -- only its file content changed, which
                // BuildChange cannot express via ChangedNodeIds/AddedNodeIds for an id already
                // owned (in baseline) by a different mod's file. Left empty here to mirror
                // exactly what the real diagnostic would compute for this scenario.
                PriorLoadOrder = Order("modA.eyegenes", "modB.eyegenes", "modC.eyegenes"),
                CurrentLoadOrder = Order("modA.eyegenes", "modC.eyegenes", "modB.eyegenes")
            };

            var r = DirtySetComputer.Compute(g, change);

            Assert.That(r.Nodes, Does.Not.Contain("GeneDef/Eyes_Red"),
                "Gap reproduced: modB becoming the new real owner of Eyes_Red is invisible to " +
                "every current seed. See issue #50.");
        }

        [Test]
        public void DefOverride_OwningModPresentInBoth_NoFlip()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats", "oppey.eyegenes2"),
                CurrentLoadOrder = Order("oppey.eyegenes2", "toastyman.moreritualseats")
            };
            var r = DirtySetComputer.Compute(BuildDefOverrideGraph(), change);
            // Owning mod is in BOTH loads (only reordered) -> no flip -> no seed.
            Assert.That(r.SeedDefOverride, Is.EqualTo(0));
            Assert.That(r.Nodes, Does.Not.Contain("GeneDef/Eyes_Red"));
        }

        [Test]
        public void DefOverride_EmptyIndex_NoOpAndNoOverDirty()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("modA", "modB"),
                CurrentLoadOrder = Order("modA", "modB", "modNew")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            Assert.That(r.SeedDefOverride, Is.EqualTo(0));
        }

        [Test]
        public void DependencyGraphData_Parse_PreIssue43Graph_HasEmptyDefOverrides()
        {
            // A graph written before #43 has no "defOverrides" field; parsing must yield an
            // empty map (not throw) so old caches load and Seed 7 simply no-ops.
            const string json =
                "{\"version\":1,\"nodes\":[],\"patchEdges\":[],\"inheritanceEdges\":[],\"metrics\":{}}";
            var g = DependencyGraphData.Parse(json);
            Assert.That(g.DefOverrides, Is.Empty);
        }

        [Test]
        public void Closure_ReachesGrandchild_ThroughIntermediateAbstractBase()
        {
            // Chain: top abstract base -> intermediate abstract base (the defect-1 shape) ->
            // concrete leaf, all resolved edges. Dirtying the TOP base must fan out through the
            // intermediate abstract node to the leaf.
            var g = new DependencyGraphData { Version = 1 };
            foreach (var id in new[] { "TerrainDef@NaturalTerrainBase",
                                       "TerrainDef@MF_VoidTerrainBase", "TerrainDef/MF_SpaceVoid" })
                g.Nodes.Add(new GraphNode { Id = id, DefType = "TerrainDef" });
            g.InheritanceEdges.Add(Inh("TerrainDef@MF_VoidTerrainBase", "TerrainDef@NaturalTerrainBase"));
            g.InheritanceEdges.Add(Inh("TerrainDef/MF_SpaceVoid", "TerrainDef@MF_VoidTerrainBase"));

            var change = new GraphChange { ChangedNodeIds = { "TerrainDef@NaturalTerrainBase" } };
            var r = DirtySetComputer.Compute(g, change);
            Assert.That(r.Nodes, Is.EquivalentTo(new[]
            {
                "TerrainDef@NaturalTerrainBase", "TerrainDef@MF_VoidTerrainBase", "TerrainDef/MF_SpaceVoid"
            }));
            Assert.That(r.SeedUnresolvedFanout, Is.EqualTo(0)); // all edges resolved: net does not fire
        }

        [Test]
        public void ChangeC_UnresolvedEdge_FansOutByName_ResolvedEdgesUnaffected()
        {
            // The safety net: an UNRESOLVED edge (parentNodeId null) whose parentName matches a
            // dirtied node's Name must still dirty its children. Here the base node exists and
            // is dirtied, but the inheritance edge to it was never resolved (e.g. the recorder
            // missed wiring it). The bare-name match on "MF_VoidTerrainBase" must reach the leaf.
            var g = new DependencyGraphData { Version = 1 };
            foreach (var id in new[] { "TerrainDef@MF_VoidTerrainBase", "TerrainDef/MF_SpaceVoid",
                                       "ThingDef/Wall", "ThingDef@BuildingBase" })
                g.Nodes.Add(new GraphNode { Id = id, DefType = id.StartsWith("Terrain") ? "TerrainDef" : "ThingDef" });

            // Unresolved edge: child points at "MF_VoidTerrainBase" but parentNodeId is null.
            g.InheritanceEdges.Add(new GraphInheritanceEdge
            {
                ChildNodeId = "TerrainDef/MF_SpaceVoid",
                ParentName = "MF_VoidTerrainBase",
                ParentNodeId = null
            });
            // A RESOLVED edge in the same graph that must NOT be disturbed.
            g.InheritanceEdges.Add(Inh("ThingDef/Wall", "ThingDef@BuildingBase"));

            // Dirty the abstract base by its node id; its Name is "MF_VoidTerrainBase".
            var change = new GraphChange { ChangedNodeIds = { "TerrainDef@MF_VoidTerrainBase" } };
            var r = DirtySetComputer.Compute(g, change);

            Assert.That(r.Nodes, Contains.Item("TerrainDef/MF_SpaceVoid")); // reached via the net
            Assert.That(r.SeedUnresolvedFanout, Is.EqualTo(1));
            // The unrelated resolved edge's nodes stay clean (the base was not dirtied).
            Assert.That(r.Nodes, Does.Not.Contain("ThingDef/Wall"));
            Assert.That(r.Nodes, Does.Not.Contain("ThingDef@BuildingBase"));
        }

        [Test]
        public void ChangeC_DoesNotFire_WhenEdgeResolved()
        {
            // Same shape as above but the edge IS resolved; the resolved-edge closure handles
            // it and the safety net must stay dormant (no double counting, no reliance on it).
            var g = new DependencyGraphData { Version = 1 };
            foreach (var id in new[] { "TerrainDef@MF_VoidTerrainBase", "TerrainDef/MF_SpaceVoid" })
                g.Nodes.Add(new GraphNode { Id = id, DefType = "TerrainDef" });
            g.InheritanceEdges.Add(Inh("TerrainDef/MF_SpaceVoid", "TerrainDef@MF_VoidTerrainBase"));

            var change = new GraphChange { ChangedNodeIds = { "TerrainDef@MF_VoidTerrainBase" } };
            var r = DirtySetComputer.Compute(g, change);
            Assert.That(r.Nodes, Contains.Item("TerrainDef/MF_SpaceVoid"));
            Assert.That(r.SeedUnresolvedFanout, Is.EqualTo(0)); // resolved closure did the work
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
