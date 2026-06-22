// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// WildcardRematchTests.cs (Piece D — Milestone 2a: superset-safe dirty set)
//
// Contains: the offline correctness gate for the wildcard-flip re-test — the one direction
// that makes the dirty set a true superset (a changed mod's predicate newly matching an
// otherwise-unchanged def), plus the guards that keep it from over- or under-dirtying
// (old matches not re-reported, unchanged mods ignored, identity patches a no-op, child
// elements attributed to their def, document-path fallbacks excluded), and an end-to-end
// check that the flips feed DirtySetComputer and propagate through inheritance.
//
// Why: this is M2a's load-bearing logic. RimWorld matches by XPath, not identity, so a subset
// here is silently wrong at recompute time. Driven by synthetic XmlDocuments, no game launch.

using System.Collections.Generic;
using System.Xml;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class WildcardRematchTests
    {
        // A raw def body as a top-level <Defs> child, as it would arrive from Context.XmlAssets.
        private static XmlNode Def(string defType, string innerXml)
        {
            var doc = new XmlDocument();
            doc.LoadXml($"<{defType}>{innerXml}</{defType}>");
            return doc.DocumentElement;
        }

        // An abstract / Name-based def (no <defName>), which keys as "{Type}@{Name}".
        private static XmlNode AbstractDef(string defType, string name, string innerXml = "")
        {
            var doc = new XmlDocument();
            doc.LoadXml($"<{defType} Name=\"{name}\" Abstract=\"True\">{innerXml}</{defType}>");
            return doc.DocumentElement;
        }

        private static GraphPatchEdge Edge(string patchId, string mod, string xpath,
            params string[] baselineMatched)
        {
            var e = new GraphPatchEdge { PatchId = patchId, SourceMod = mod, Xpath = xpath };
            e.MatchedNodeIds.AddRange(baselineMatched);
            e.ModifiedNodeIds.AddRange(baselineMatched);
            return e;
        }

        private static DependencyGraphData GraphWith(params GraphPatchEdge[] edges)
        {
            var g = new DependencyGraphData { Version = 1 };
            g.PatchEdges.AddRange(edges);
            return g;
        }

        private static Dictionary<string, string> Cur(string patchId, string xpath)
            => new Dictionary<string, string> { { patchId, xpath } };

        // The core hazard: a changed mod's predicate WIDENED. The baseline edge (narrow xpath)
        // matched nothing, but the CURRENT predicate matches a def whose own body did not
        // change — so nothing else seeds it. NewlyMatched must surface it from the current
        // xpath, not the stale baseline one.
        [Test]
        public void NewlyMatched_WidenedPredicateMatchesNewDef_IsSeeded()
        {
            var candidates = new[]
            {
                Def("ThingDef", "<defName>Gloves</defName><apparel><tags><li>Hands</li></tags></apparel>"),
                Def("ThingDef", "<defName>Boots</defName><apparel><tags><li>Feet</li></tags></apparel>")
            };
            var doc = WildcardRematch.BuildCandidateDocument(candidates);

            // Baseline edge had a NARROW xpath that matched nothing; the current predicate is
            // wider. NewlyMatched must use the current xpath (passed in), not edge.Xpath.
            var graph = GraphWith(Edge("modX#0", "modX", "Defs/ThingDef[apparel/tags/li=\"NEVER\"]"));
            var current = Cur("modX#0", "Defs/ThingDef[apparel/tags/li=\"Hands\"]");

            var added = WildcardRematch.NewlyMatched(graph, current, doc);
            Assert.That(added, Is.EquivalentTo(new[] { "ThingDef/Gloves" }));
        }

        // Defs already in the baseline match set are NOT additions (Seed 2 owns the old set);
        // only genuinely new members are returned.
        [Test]
        public void NewlyMatched_BaselineMatchesNotReReported()
        {
            var candidates = new[]
            {
                Def("ThingDef", "<defName>Gloves</defName><apparel><tags><li>Hands</li></tags></apparel>")
            };
            var doc = WildcardRematch.BuildCandidateDocument(candidates);

            // Baseline already matched Gloves and the predicate is unchanged -> nothing added.
            var graph = GraphWith(Edge("modX#0", "modX",
                "Defs/ThingDef[apparel/tags/li=\"Hands\"]", "ThingDef/Gloves"));
            var current = Cur("modX#0", "Defs/ThingDef[apparel/tags/li=\"Hands\"]");

            var added = WildcardRematch.NewlyMatched(graph, current, doc);
            Assert.That(added, Is.Empty);
        }

        // A current patch id with no baseline edge (a brand-new patch the changed mod added)
        // has an empty baseline, so every def it matches is an addition.
        [Test]
        public void NewlyMatched_BrandNewPatch_AllMatchesAdded()
        {
            var candidates = new[]
            {
                Def("ThingDef", "<defName>Gloves</defName><apparel><tags><li>Hands</li></tags></apparel>")
            };
            var doc = WildcardRematch.BuildCandidateDocument(candidates);
            var graph = GraphWith(); // no edges at all
            var current = Cur("modX#7", "Defs/ThingDef[apparel/tags/li=\"Hands\"]");

            var added = WildcardRematch.NewlyMatched(graph, current, doc);
            Assert.That(added, Is.EquivalentTo(new[] { "ThingDef/Gloves" }));
        }

        // Re-testing an identity (defName-targeted) patch is harmless: it resolves to the same
        // single def, which is already its baseline match -> no phantom flip. This is why the
        // driver can re-test ALL of a changed mod's predicates without a classifier.
        [Test]
        public void NewlyMatched_IdentityPatch_NoPhantomFlip()
        {
            var candidates = new[]
            {
                Def("ThingDef", "<defName>Steel</defName>"),
                Def("ThingDef", "<defName>Plasteel</defName>")
            };
            var doc = WildcardRematch.BuildCandidateDocument(candidates);
            var graph = GraphWith(Edge("modX#0", "modX",
                "Defs/ThingDef[defName=\"Steel\"]", "ThingDef/Steel"));
            var current = Cur("modX#0", "Defs/ThingDef[defName=\"Steel\"]");

            var added = WildcardRematch.NewlyMatched(graph, current, doc);
            Assert.That(added, Is.Empty);
        }

        // A predicate that widens to match an abstract base must attribute to its "@Name" id,
        // so the downstream inheritance closure can fan the change out to descendants.
        [Test]
        public void NewlyMatched_MatchesAbstractBase_KeysByName()
        {
            var candidates = new[]
            {
                AbstractDef("ThingDef", "BuildingBase", "<statBases><Mass>1</Mass></statBases>")
            };
            var doc = WildcardRematch.BuildCandidateDocument(candidates);
            var graph = GraphWith(Edge("modX#0", "modX", "Defs/ThingDef[statBases/Mass=\"NEVER\"]"));
            var current = Cur("modX#0", "Defs/ThingDef[statBases/Mass]");

            var added = WildcardRematch.NewlyMatched(graph, current, doc);
            Assert.That(added, Is.EquivalentTo(new[] { "ThingDef@BuildingBase" }));
        }

        // A patch targeting a child ELEMENT inside a def must attribute to the def id, not the
        // child, so the def (whose resolved body changes) is the unit dirtied.
        [Test]
        public void MatchedDefIds_ChildElement_AttributesToDef()
        {
            var candidates = new[] { Def("ThingDef", "<defName>Steel</defName><statBases><Mass>1</Mass></statBases>") };
            var ids = WildcardRematch.MatchedDefIds(candidates, "Defs/ThingDef/statBases");
            Assert.That(ids, Is.EquivalentTo(new[] { "ThingDef/Steel" }));
        }

        // A match on a node outside any def (here the <Defs> root itself) keys to a document
        // path, which is not a stable identity and must be excluded rather than reported as a
        // phantom "Defs" flip.
        [Test]
        public void MatchedDefIds_DocumentPathFallback_Excluded()
        {
            var candidates = new[] { Def("ThingDef", "<defName>Steel</defName>") };
            var ids = WildcardRematch.MatchedDefIds(candidates, "/Defs");
            Assert.That(ids, Is.Empty);
        }

        [Test]
        public void MatchedDefIds_MalformedXpath_MatchesNothing()
        {
            var candidates = new[] { Def("ThingDef", "<defName>Steel</defName>") };
            var ids = WildcardRematch.MatchedDefIds(candidates, "Defs/ThingDef[");
            Assert.That(ids, Is.Empty);
        }

        // End-to-end: the flips feed DirtySetComputer as Seed 4 and then propagate through the
        // inheritance closure (a newly-matched abstract base must dirty its descendants).
        [Test]
        public void Compute_WildcardFlipSeeds_CountedAndClosedOverInheritance()
        {
            var graph = new DependencyGraphData { Version = 1 };
            foreach (var id in new[] { "ThingDef@BuildingBase", "ThingDef/Wall", "ThingDef/Door" })
                graph.Nodes.Add(new GraphNode { Id = id, DefType = "ThingDef" });
            graph.InheritanceEdges.Add(new GraphInheritanceEdge
                { ChildNodeId = "ThingDef/Wall", ParentName = "BuildingBase", ParentNodeId = "ThingDef@BuildingBase" });
            graph.InheritanceEdges.Add(new GraphInheritanceEdge
                { ChildNodeId = "ThingDef/Door", ParentName = "BuildingBase", ParentNodeId = "ThingDef@BuildingBase" });

            var change = new GraphChange
            {
                PriorLoadOrder = new List<string> { "modX" },
                CurrentLoadOrder = new List<string> { "modX" }
            };

            // A changed mod's patch newly matches the abstract base.
            var r = DirtySetComputer.Compute(graph, change, new[] { "ThingDef@BuildingBase" });

            Assert.That(r.SeedWildcardFlip, Is.EqualTo(1));
            Assert.That(r.Nodes, Is.EquivalentTo(new[]
            {
                "ThingDef@BuildingBase", "ThingDef/Wall", "ThingDef/Door"
            }));
        }

        // Null/empty inputs are safe and additive-free (the common no-patch-change load).
        [Test]
        public void NewlyMatched_EmptyInputs_Empty()
        {
            var doc = WildcardRematch.BuildCandidateDocument(new[] { Def("ThingDef", "<defName>Steel</defName>") });
            var cur = Cur("modX#0", "Defs/ThingDef");
            Assert.That(WildcardRematch.NewlyMatched(GraphWith(), new Dictionary<string, string>(), doc), Is.Empty);
            Assert.That(WildcardRematch.NewlyMatched(null, cur, doc), Is.Empty);
            Assert.That(WildcardRematch.NewlyMatched(GraphWith(), cur, null), Is.Empty);
        }
    }
}
