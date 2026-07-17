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

        // Seed 8: a def defined by exactly ONE mod (no override -> no defOverrides entry, so
        // Seed 7 can't reach it) must still be dirtied for removal when that mod leaves the
        // load order. Mirrors the live gap: V.Rooboid.Faun's RBM_UnguligradeLegs GeneDef/FurDef
        // survived a rebuild after Faun was removed, because no seed reached a single-owner def.
        private static DependencyGraphData BuildSoleOwnerGraph()
        {
            var g = new DependencyGraphData { Version = 1 };
            g.Nodes.Add(new GraphNode { Id = "GeneDef/RBM_UnguligradeLegs", DefName = "RBM_UnguligradeLegs", SourceMod = "v.rooboid.faun" });
            g.Nodes.Add(new GraphNode { Id = "FurDef/RBM_UnguligradeLegs", DefName = "RBM_UnguligradeLegs", SourceMod = "v.rooboid.faun" });
            g.Nodes.Add(new GraphNode { Id = "ThingDef/Steel", DefName = "Steel", SourceMod = "ludeon.rimworld" });
            return g;
        }

        [Test]
        public void OwnerModRemoved_SoleOwnerDef_IsDirtied()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld", "v.rooboid.faun"),
                CurrentLoadOrder = Order("ludeon.rimworld")   // Faun removed
            };
            var r = DirtySetComputer.Compute(BuildSoleOwnerGraph(), change);
            Assert.That(r.Nodes, Contains.Item("GeneDef/RBM_UnguligradeLegs"));
            Assert.That(r.Nodes, Contains.Item("FurDef/RBM_UnguligradeLegs"));
            Assert.That(r.SeedOwnerModRemoved, Is.EqualTo(2));
            // Steel's owning mod (ludeon.rimworld) is still loaded -> stays clean.
            Assert.That(r.Nodes, Does.Not.Contain("ThingDef/Steel"));
        }

        [Test]
        public void OwnerModRemoved_ModPresentInBoth_NoFlip()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld", "v.rooboid.faun"),
                CurrentLoadOrder = Order("v.rooboid.faun", "ludeon.rimworld")   // reordered only
            };
            var r = DirtySetComputer.Compute(BuildSoleOwnerGraph(), change);
            Assert.That(r.SeedOwnerModRemoved, Is.EqualTo(0));
            Assert.That(r.Nodes, Does.Not.Contain("GeneDef/RBM_UnguligradeLegs"));
        }

        [Test]
        public void OwnerModRemoved_ModAdded_DoesNotSeed()
        {
            // Seed 8 is asymmetric by design (removal only) -- a newly-added mod's defs are
            // fresh nodes handled by Seed 5 (AddedNodeIds), not by this seed.
            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld"),
                CurrentLoadOrder = Order("ludeon.rimworld", "v.rooboid.faun")
            };
            var r = DirtySetComputer.Compute(BuildSoleOwnerGraph(), change);
            Assert.That(r.SeedOwnerModRemoved, Is.EqualTo(0));
        }

        // Seed 8 fallback: a node whose SourceMod/SourceFile came back null because
        // RegisterNode's `loadingAsset` was null -- the real-world shape whenever a
        // PatchOperationAdd splices a brand-new top-level def into the combined doc (never
        // one of CombineIntoUnifiedXML's per-file assetlookup entries, so ParseAndProcessXML
        // passes DefFromNodeNew a null asset). No SourceMod means the plain node.SourceMod
        // XOR above can never fire for it, so it must fall back to whichever mod's patch edge
        // touched it.
        // Seed 8 fallback: a node whose SourceMod/SourceFile came back null because
        // RegisterNode's `loadingAsset` was null -- the real-world shape whenever a
        // PatchOperationAdd splices a brand-new top-level def into the combined doc (never
        // one of CombineIntoUnifiedXML's per-file assetlookup entries, so ParseAndProcessXML
        // passes DefFromNodeNew a null asset). Such a node's own patch edge (if captured at
        // all) records only the xpath TARGET the Add selected, never the new node's own id,
        // so this can ONLY be reached via the dedicated PatchInjectedOwners index
        // (ProvenanceRecorder.RecordAddedChildren), never via ModifiedNodeIds.
        [Test]
        public void OwnerModRemoved_NullSourceMod_FallsBackToPatchInjectedOwner()
        {
            var g = new DependencyGraphData { Version = 1 };
            g.Nodes.Add(new GraphNode { Id = "GeneDef/RBM_UnguligradeLegs", DefName = "RBM_UnguligradeLegs" });
            g.PatchInjectedOwners["GeneDef/RBM_UnguligradeLegs"] = "v.rooboid.faun";

            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld", "v.rooboid.faun"),
                CurrentLoadOrder = Order("ludeon.rimworld")   // Faun removed
            };
            var r = DirtySetComputer.Compute(g, change);
            Assert.That(r.Nodes, Contains.Item("GeneDef/RBM_UnguligradeLegs"));
            Assert.That(r.SeedOwnerModRemoved, Is.EqualTo(1));
        }

        [Test]
        public void OwnerModRemoved_NullSourceMod_PatchInjectedOwnerStillPresent_NoFlip()
        {
            var g = new DependencyGraphData { Version = 1 };
            g.Nodes.Add(new GraphNode { Id = "GeneDef/RBM_UnguligradeLegs", DefName = "RBM_UnguligradeLegs" });
            g.PatchInjectedOwners["GeneDef/RBM_UnguligradeLegs"] = "v.rooboid.faun";

            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld", "v.rooboid.faun"),
                CurrentLoadOrder = Order("ludeon.rimworld", "v.rooboid.faun", "some.other.mod")
            };
            var r = DirtySetComputer.Compute(g, change);
            Assert.That(r.SeedOwnerModRemoved, Is.EqualTo(0));
            Assert.That(r.Nodes, Does.Not.Contain("GeneDef/RBM_UnguligradeLegs"));
        }

        [Test]
        public void OwnerModRemoved_RealSourceMod_TakesPrecedenceOverPatchInjectedOwner()
        {
            // A node with a real, non-empty SourceMod must never consult the fallback index,
            // even if (implausibly) an entry exists for its id.
            var g = new DependencyGraphData { Version = 1 };
            g.Nodes.Add(new GraphNode { Id = "GeneDef/RBM_UnguligradeLegs", DefName = "RBM_UnguligradeLegs", SourceMod = "real.owner" });
            g.PatchInjectedOwners["GeneDef/RBM_UnguligradeLegs"] = "v.rooboid.faun";

            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld", "v.rooboid.faun", "real.owner"),
                CurrentLoadOrder = Order("ludeon.rimworld", "real.owner")   // only Faun removed
            };
            var r = DirtySetComputer.Compute(g, change);
            Assert.That(r.SeedOwnerModRemoved, Is.EqualTo(0));
            Assert.That(r.Nodes, Does.Not.Contain("GeneDef/RBM_UnguligradeLegs"));
        }

        // Seed 9 (issue #86): a def's .NET Type can be supplied by a DIFFERENT mod's C# assembly
        // than the mod whose XML declares the instance (e.g. FacialAnimation.EyeballColorDef,
        // a type from nals.facialanimation, instantiated in oppey.eyegenes2's own unpatched
        // XML). When the type-providing mod leaves the load, the Type disappears, so no mod's
        // XML can construct that element anymore -- even a consuming mod that never changed.
        // Mirrors Seed 6 (MayRequire) exactly, just keyed on TypeProviderIndex.
        private static DependencyGraphData BuildTypeProviderGraph()
        {
            var g = new DependencyGraphData { Version = 1 };
            g.TypeProviderIndex["nals.facialanimation"] = new List<string>
            {
                "FacialAnimation.EyeballColorDef/EC_Blue", "FacialAnimation.EyeballColorDef/EC_Green"
            };
            g.TypeProviderIndex["ludeon.rimworld.biotech"] = new List<string> { "ThingDef/Gene_X" };
            return g;
        }

        [Test]
        public void TypeProviderFlip_RemovedMod_DirtiesProvidedDefs()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld", "ludeon.rimworld.biotech", "nals.facialanimation", "oppey.eyegenes2"),
                CurrentLoadOrder = Order("ludeon.rimworld", "ludeon.rimworld.biotech", "oppey.eyegenes2")   // provider removed
            };
            var r = DirtySetComputer.Compute(BuildTypeProviderGraph(), change);
            Assert.That(r.Nodes, Contains.Item("FacialAnimation.EyeballColorDef/EC_Blue"));
            Assert.That(r.Nodes, Contains.Item("FacialAnimation.EyeballColorDef/EC_Green"));
            Assert.That(r.SeedTypeProvider, Is.EqualTo(2));
            // biotech's genes are provided by a mod that's still loaded -> stays clean.
            Assert.That(r.Nodes, Does.Not.Contain("ThingDef/Gene_X"));
        }

        [Test]
        public void TypeProvider_AddedMod_DirtiesProvidedDefs()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld", "ludeon.rimworld.biotech", "oppey.eyegenes2"),
                CurrentLoadOrder = Order("ludeon.rimworld", "ludeon.rimworld.biotech", "nals.facialanimation", "oppey.eyegenes2")
            };
            var r = DirtySetComputer.Compute(BuildTypeProviderGraph(), change);
            Assert.That(r.Nodes, Contains.Item("FacialAnimation.EyeballColorDef/EC_Blue"));
            Assert.That(r.Nodes, Contains.Item("FacialAnimation.EyeballColorDef/EC_Green"));
            Assert.That(r.SeedTypeProvider, Is.EqualTo(2));
        }

        [Test]
        public void TypeProvider_ModPresentInBoth_NoFlip()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld", "nals.facialanimation"),
                CurrentLoadOrder = Order("nals.facialanimation", "ludeon.rimworld")   // reordered only
            };
            var r = DirtySetComputer.Compute(BuildTypeProviderGraph(), change);
            Assert.That(r.SeedTypeProvider, Is.EqualTo(0));
            Assert.That(r.Nodes, Does.Not.Contain("FacialAnimation.EyeballColorDef/EC_Blue"));
        }

        [Test]
        public void TypeProvider_PackageIdMatch_IsCaseInsensitive()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld", "NALS.FacialAnimation"),
                CurrentLoadOrder = Order("ludeon.rimworld")   // removed, differently-cased
            };
            var r = DirtySetComputer.Compute(BuildTypeProviderGraph(), change);
            Assert.That(r.Nodes, Contains.Item("FacialAnimation.EyeballColorDef/EC_Blue"));
            Assert.That(r.SeedTypeProvider, Is.EqualTo(2));
        }

        [Test]
        public void TypeProvider_EmptyIndex_NoOpAndNoOverDirty()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("modA", "modB"),
                CurrentLoadOrder = Order("modA", "modB", "modNew")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change);
            Assert.That(r.SeedTypeProvider, Is.EqualTo(0));
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

        [Test]
        public void DependencyGraphData_Parse_ReadsTypeProviderIndex_CaseInsensitive()
        {
            const string json =
                "{\"version\":1,\"nodes\":[],\"patchEdges\":[],\"inheritanceEdges\":[]," +
                "\"typeProviders\":{\"Nals.FacialAnimation\":[\"FacialAnimation.EyeballColorDef/EC_Blue\"," +
                  "\"FacialAnimation.EyeballColorDef/EC_Green\"]},\"metrics\":{}}";
            var g = DependencyGraphData.Parse(json);
            Assert.That(g.TypeProviderIndex, Has.Count.EqualTo(1));
            // Lookup with different casing must resolve (OrdinalIgnoreCase).
            Assert.That(g.TypeProviderIndex["nals.facialanimation"],
                Is.EquivalentTo(new[]
                {
                    "FacialAnimation.EyeballColorDef/EC_Blue", "FacialAnimation.EyeballColorDef/EC_Green"
                }));
        }

        [Test]
        public void DependencyGraphData_Parse_PreIssue86Graph_HasEmptyTypeProviderIndex()
        {
            // A graph written before issue #86 has no "typeProviders" field; parsing must yield an
            // empty index (not throw) so old caches load and Seed 9 simply no-ops.
            const string json =
                "{\"version\":1,\"nodes\":[],\"patchEdges\":[],\"inheritanceEdges\":[],\"metrics\":{}}";
            var g = DependencyGraphData.Parse(json);
            Assert.That(g.TypeProviderIndex, Is.Empty);
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

        // defOverrides is keyed only by the WINNING mod (ProvenanceGraph.AddNode sets
        // node.sourceMod to the last-write-wins owner and indexes defOverrides under that same
        // packageId) -- so a def that lost an override never gets a defOverrides entry at all,
        // and a def that won one carries BOTH a real node.SourceMod (reachable by Seed 8) and a
        // defOverrides entry (reachable by Seed 7) for the identical id. Removing the winner
        // must dirty it (via either or both seeds); removing a mod that only ever lost the
        // override must leave it clean, since the winner's content never depended on the loser.
        private static DependencyGraphData BuildOverrideWinnerGraph()
        {
            var g = new DependencyGraphData { Version = 1 };
            g.Nodes.Add(new GraphNode { Id = "GeneDef/Eyes_Red", DefName = "Eyes_Red", SourceMod = "oppey.eyegenes2" });
            g.DefOverrides["oppey.eyegenes2"] = new List<string> { "GeneDef/Eyes_Red" };
            return g;
        }

        [Test]
        public void DefOverride_WinningMod_HasBothSourceModAndOverrideEntry_RemovedMod_DirtiesDef()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("ludeon.rimworld.biotech", "oppey.eyegenes2"),
                CurrentLoadOrder = Order("ludeon.rimworld.biotech")   // the winner is removed
            };
            var r = DirtySetComputer.Compute(BuildOverrideWinnerGraph(), change);
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Red"));
            // Both seeds recognize this id as relevant, but each seed's counter only
            // increments on dirty.Add returning true (first insertion) -- Seed 7 runs before
            // Seed 8 in seed order, so Seed 7 claims the credit and Seed 8's `continue` never
            // reaches its own dirty.Add for an id Seed 7 already added. The result set is
            // identical either way (a HashSet, order-independent); only the per-seed
            // diagnostic counters are order-sensitive, which is why this is pinned explicitly.
            Assert.That(r.SeedDefOverride, Is.EqualTo(1));
            Assert.That(r.SeedOwnerModRemoved, Is.EqualTo(0));
        }

        [Test]
        public void DefOverride_LosingMod_Removed_WinnerStays_NoFlip()
        {
            // A mod that never won the override has no defOverrides entry and is not the
            // node's SourceMod, so its own removal has zero bearing on this def's content --
            // neither seed should fire.
            var change = new GraphChange
            {
                PriorLoadOrder = Order("some.losing.mod", "oppey.eyegenes2"),
                CurrentLoadOrder = Order("oppey.eyegenes2")   // only the loser is removed
            };
            var r = DirtySetComputer.Compute(BuildOverrideWinnerGraph(), change);
            Assert.That(r.Nodes, Does.Not.Contain("GeneDef/Eyes_Red"));
            Assert.That(r.SeedDefOverride, Is.EqualTo(0));
            Assert.That(r.SeedOwnerModRemoved, Is.EqualTo(0));
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

        // Issue #43 add-direction: DefOverrideRematch's own output folded into Compute via the
        // new defOverrideRematchSeeds parameter (mirrors how wildcardFlipSeeds already folds in).
        [Test]
        public void DefOverrideRematch_SeededId_DirtiesNode_AndCounts()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats"),
                CurrentLoadOrder = Order("toastyman.moreritualseats", "oppey.eyegenes2")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change,
                defOverrideRematchSeeds: new[] { "GeneDef/Eyes_Red" });
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Red"));
            Assert.That(r.SeedDefOverrideRematch, Is.EqualTo(1));
        }

        [Test]
        public void DefOverrideRematch_NullSeeds_NoOp()
        {
            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats"),
                CurrentLoadOrder = Order("toastyman.moreritualseats")
            };
            var r = DirtySetComputer.Compute(BuildGraph(), change, defOverrideRematchSeeds: null);
            Assert.That(r.SeedDefOverrideRematch, Is.EqualTo(0));
        }

        // Issue #50: modB is present in BOTH prior and current load order (no presence flip,
        // so Seed 7 can't see it) but its own Defs file changed this load and it now owns a
        // def baseline attributes to a different mod. GraphChange.ChangedDefFileMods carries
        // that signal; DefOverrideRematch.NewlyDetectedOverrides (the same function #43 uses)
        // is fed the changed-mod set instead of a newly-added-mod set to compute the seed,
        // exactly as DirtySetDiagnostic.ComputeDefOverrideFlips does end to end.
        [Test]
        public void DefOverrideRematch_ChangedDefFileMod_DirtiesNode_AndCounts()
        {
            var graph = new DependencyGraphData { Version = 1 };
            graph.Nodes.Add(new GraphNode { Id = "GeneDef/Eyes_Red", SourceMod = "toastyman.moreritualseats" });

            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats", "oppey.eyegenes2"),
                CurrentLoadOrder = Order("toastyman.moreritualseats", "oppey.eyegenes2"), // unchanged
                ChangedDefFileMods = { "oppey.eyegenes2" } // modB's own Defs file changed this load
            };

            var currentIdOwner = new Dictionary<string, string> { ["GeneDef/Eyes_Red"] = "oppey.eyegenes2" };
            var seeds = DefOverrideRematch.NewlyDetectedOverrides(graph, currentIdOwner, change.ChangedDefFileMods);

            var r = DirtySetComputer.Compute(graph, change, defOverrideRematchSeeds: seeds);
            Assert.That(r.Nodes, Contains.Item("GeneDef/Eyes_Red"));
            Assert.That(r.SeedDefOverrideRematch, Is.EqualTo(1));
        }

        [Test]
        public void DefOverrideRematch_UnchangedDefFileMod_NoFlip()
        {
            var graph = new DependencyGraphData { Version = 1 };
            graph.Nodes.Add(new GraphNode { Id = "GeneDef/Eyes_Red", SourceMod = "toastyman.moreritualseats" });

            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats", "oppey.eyegenes2"),
                CurrentLoadOrder = Order("toastyman.moreritualseats", "oppey.eyegenes2"), // unchanged
                ChangedDefFileMods = { "some.unrelated.mod" } // neither owning mod changed
            };

            var currentIdOwner = new Dictionary<string, string> { ["GeneDef/Eyes_Red"] = "toastyman.moreritualseats" };
            var seeds = DefOverrideRematch.NewlyDetectedOverrides(graph, currentIdOwner, change.ChangedDefFileMods);

            var r = DirtySetComputer.Compute(graph, change, defOverrideRematchSeeds: seeds);
            Assert.That(r.Nodes, Does.Not.Contain("GeneDef/Eyes_Red"));
            Assert.That(r.SeedDefOverrideRematch, Is.EqualTo(0));
        }

        [Test]
        public void DefOverrideRematch_FoldsBeforeInheritanceClosure()
        {
            // Seeding the ABSTRACT parent via the rematch must still fan out to its concrete
            // children through the normal inheritance-closure step below.
            var graph = BuildGraph();
            string abstractParentId = graph.Nodes.First(n => n.Id.Contains("@")).Id;
            string concreteChildId = graph.InheritanceEdges
                .First(e => e.ParentNodeId == abstractParentId).ChildNodeId;

            var change = new GraphChange
            {
                PriorLoadOrder = Order("toastyman.moreritualseats"),
                CurrentLoadOrder = Order("toastyman.moreritualseats", "oppey.eyegenes2")
            };
            var r = DirtySetComputer.Compute(graph, change,
                defOverrideRematchSeeds: new[] { abstractParentId });
            Assert.That(r.Nodes, Contains.Item(abstractParentId));
            Assert.That(r.Nodes, Contains.Item(concreteChildId));
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
