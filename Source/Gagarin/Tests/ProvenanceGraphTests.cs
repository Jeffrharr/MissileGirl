// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// ProvenanceGraphTests.cs (Piece A — provenance capture)
//
// Contains: NUnit fixture exercising ProvenanceGraph's pure logic — node keying
// (KeyForNode), inheritance resolution, patch-edge accumulation, and the
// self-referential serializedBytes metric — against synthetic XmlDocuments.
//
// Used for: offline verification of the load-bearing keying and serialization
// code without launching RimWorld; runs under a plain net8.0 NUnit host.
//
// Why: ProvenanceGraph is deliberately RimWorld-free precisely so it can be
// tested here. The RimWorld plumbing in ProvenanceRecorder stays unverified
// until a real cold load, so these tests guard the parts we can prove offline.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml;
using Gagarin;

namespace Gagarin.Tests
{
    // Exercises the pure node-keying and serialization logic of ProvenanceGraph
    // against synthetic XmlDocument inputs. These do not require RimWorld and run
    // under a plain net8.0 test host.
    [TestFixture]
    public class ProvenanceGraphTests
    {
        // Builds a minimal combined <Defs> document with the given top-level def
        // elements, each "Type:Name" creating <Type><defName>Name</defName>...</Type>.
        private static XmlDocument BuildDefsDoc(params string[] typeAndName)
        {
            XmlDocument doc = new XmlDocument();
            XmlElement defs = doc.CreateElement("Defs");
            doc.AppendChild(defs);
            foreach (string spec in typeAndName)
            {
                string[] parts = spec.Split(':');
                XmlElement def = doc.CreateElement(parts[0]);
                XmlElement defName = doc.CreateElement("defName");
                defName.InnerText = parts[1];
                def.AppendChild(defName);
                defs.AppendChild(def);
            }
            return doc;
        }

        [Test]
        public void KeyForNode_TopLevelDef_UsesTypeAndDefName()
        {
            XmlDocument doc = BuildDefsDoc("ThingDef:Steel");
            XmlElement def = (XmlElement)doc.DocumentElement.FirstChild;

            Assert.That(new ProvenanceGraph().KeyForNode(def), Is.EqualTo("ThingDef/Steel"));
        }

        [Test]
        public void KeyForNode_ChildOfDef_KeysToOwningDef()
        {
            XmlDocument doc = BuildDefsDoc("ThingDef:Steel");
            XmlElement def = (XmlElement)doc.DocumentElement.FirstChild;
            XmlElement statBases = doc.CreateElement("statBases");
            def.AppendChild(statBases);

            // A node nested inside a def resolves to that def's id.
            Assert.That(new ProvenanceGraph().KeyForNode(statBases), Is.EqualTo("ThingDef/Steel"));
        }

        [Test]
        public void KeyForNode_NonDefNode_FallsBackToDocumentPath()
        {
            // A node not under <Defs>/<Def> has no def id; we expect a positional
            // document path including a sibling index.
            XmlDocument doc = new XmlDocument();
            XmlElement root = doc.CreateElement("Root");
            doc.AppendChild(root);
            root.AppendChild(doc.CreateElement("Child"));
            XmlElement second = doc.CreateElement("Child");
            root.AppendChild(second);

            Assert.That(new ProvenanceGraph().KeyForNode(second), Is.EqualTo("Root/Child[2]"));
        }

        [Test]
        public void KeyForNode_AbstractDef_UsesNameAttribute()
        {
            // Abstract / Name-based parent defs carry no <defName> at patch time
            // (inheritance is resolved later). They — and any node inside them — must
            // key by the Name attribute so the identity is stable and ties to the
            // inheritance graph, rather than falling back to a positional path.
            XmlDocument doc = new XmlDocument();
            XmlElement defs = doc.CreateElement("Defs");
            doc.AppendChild(defs);
            XmlElement abstractDef = doc.CreateElement("ThingDef");
            abstractDef.SetAttribute("Name", "BuildingBase");
            abstractDef.SetAttribute("Abstract", "True");
            defs.AppendChild(abstractDef);
            XmlElement comps = doc.CreateElement("comps");
            abstractDef.AppendChild(comps);

            ProvenanceGraph graph = new ProvenanceGraph();
            Assert.That(graph.KeyForNode(abstractDef), Is.EqualTo("ThingDef@BuildingBase"));
            // A node inside the abstract def keys to the same abstract identity.
            Assert.That(graph.KeyForNode(comps), Is.EqualTo("ThingDef@BuildingBase"));
            // And it must NOT have used the positional fallback.
            Assert.That(graph.DocumentPathFallbackCount, Is.EqualTo(0));
        }

        [Test]
        public void KeyForNode_AbstractDefWithDefName_UsesNameAttribute_NotDefName()
        {
            // Issue #52: an abstract def can carry BOTH a Name attribute (used for
            // inheritance) and a <defName> child. That <defName> may collide with an
            // unrelated concrete sibling's own defName (e.g. Vanilla Core's
            // MercenarySlasherBase / Mercenary_Slasher). KeyForNode must key such a
            // node by Name — same as RegisterAbstract does when registering it — so a
            // patch matching the abstract node never lands on the concrete node's id.
            XmlDocument doc = BuildDefsDoc("ThingDef:Mercenary_Slasher");
            XmlElement concreteDef = (XmlElement)doc.DocumentElement.FirstChild;

            XmlElement abstractDef = doc.CreateElement("ThingDef");
            abstractDef.SetAttribute("Name", "MercenarySlasherBase");
            abstractDef.SetAttribute("Abstract", "True");
            XmlElement defNameEl = doc.CreateElement("defName");
            defNameEl.InnerText = "Mercenary_Slasher";
            abstractDef.AppendChild(defNameEl);
            doc.DocumentElement.AppendChild(abstractDef);

            ProvenanceGraph graph = new ProvenanceGraph();
            Assert.That(graph.KeyForNode(abstractDef), Is.EqualTo("ThingDef@MercenarySlasherBase"));
            Assert.That(graph.KeyForNode(concreteDef), Is.EqualTo("ThingDef/Mercenary_Slasher"));
        }

        [Test]
        public void Serialize_ProducesValidSchema_WithCorrectCounts()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("ThingDef", "Steel", null, "ludeon.rimworld", "Core/Steel.xml", null);
            graph.AddNode("ThingDef", "Plasteel", null, "ludeon.rimworld", "Core/Plasteel.xml", null);
            // Node ids are pre-computed at selection time, so AddPatchEdge takes ids.
            graph.AddPatchEdge("cete.combatextended#42", "cete.combatextended",
                "PatchOperationReplace", "Defs/ThingDef[defName=\"Steel\"]/statBases",
                new[] { "ThingDef/Steel" }, new[] { "ThingDef/Steel" });

            string json = graph.Serialize(7);
            using JsonDocument parsed = JsonDocument.Parse(json);
            JsonElement root = parsed.RootElement;

            Assert.That(root.GetProperty("version").GetInt32(), Is.EqualTo(1));
            Assert.That(root.GetProperty("nodes").GetArrayLength(), Is.EqualTo(2));
            Assert.That(root.GetProperty("patchEdges").GetArrayLength(), Is.EqualTo(1));

            JsonElement metrics = root.GetProperty("metrics");
            Assert.That(metrics.GetProperty("nodeCount").GetInt32(), Is.EqualTo(2));
            Assert.That(metrics.GetProperty("patchEdgeCount").GetInt32(), Is.EqualTo(1));
            Assert.That(metrics.GetProperty("captureOverheadMs").GetInt32(), Is.EqualTo(7));

            JsonElement edge = root.GetProperty("patchEdges")[0];
            Assert.That(edge.GetProperty("patchId").GetString(), Is.EqualTo("cete.combatextended#42"));
            Assert.That(edge.GetProperty("operationType").GetString(), Is.EqualTo("PatchOperationReplace"));
            Assert.That(edge.GetProperty("xpath").GetString(),
                Is.EqualTo("Defs/ThingDef[defName=\"Steel\"]/statBases"));
            Assert.That(edge.GetProperty("matchedNodeIds").EnumerateArray()
                .Select(e => e.GetString()), Does.Contain("ThingDef/Steel"));
        }

        [Test]
        public void Serialize_SerializedBytes_MatchesUtf8Length()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("ThingDef", "Steel", null, "ludeon.rimworld", "Core/Steel.xml", null);

            string json = graph.Serialize(0);
            long reported = JsonDocument.Parse(json).RootElement
                .GetProperty("metrics").GetProperty("serializedBytes").GetInt64();

            // The reported byte count must equal the actual UTF-8 length of the
            // emitted document (the value is self-referential).
            Assert.That(reported, Is.EqualTo(Encoding.UTF8.GetByteCount(json)));
        }

        [Test]
        public void Serialize_Metrics_ReportResolutionAndCounts()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("ThingDef", null, "BuildingBase", "m", "B.xml", null);    // abstract
            graph.AddNode("ThingDef", "Wall", null, "m", "W.xml", "BuildingBase");  // resolves
            graph.AddNode("ThingDef", "Orphan", null, "m", "O.xml", "MissingBase"); // unresolved

            JsonElement m = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("metrics");
            Assert.That(m.GetProperty("abstractNodeCount").GetInt32(), Is.EqualTo(1));
            Assert.That(m.GetProperty("inheritanceEdgeCount").GetInt32(), Is.EqualTo(2));
            Assert.That(m.GetProperty("inheritanceResolvedCount").GetInt32(), Is.EqualTo(1));
            // The additive timing / mod-count metrics are always present.
            Assert.That(m.TryGetProperty("registerMs", out _), Is.True);
            Assert.That(m.TryGetProperty("recordMs", out _), Is.True);
            Assert.That(m.TryGetProperty("activeModCount", out _), Is.True);
        }

        [Test]
        public void InheritanceEdge_ResolvesParentNodeId_WhenParentKnown()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // Child registered before parent: resolution must still find it.
            graph.AddNode("ThingDef", "Foo", null, "mod.a", "A/Foo.xml", "BaseApparel");
            graph.AddNode("ThingDef", "BaseApparel", null, "ludeon.rimworld", "Core/Base.xml", null);

            JsonElement edges = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("inheritanceEdges");

            Assert.That(edges.GetArrayLength(), Is.EqualTo(1));
            JsonElement edge = edges[0];
            Assert.That(edge.GetProperty("childNodeId").GetString(), Is.EqualTo("ThingDef/Foo"));
            Assert.That(edge.GetProperty("parentName").GetString(), Is.EqualTo("BaseApparel"));
            Assert.That(edge.GetProperty("parentNodeId").GetString(), Is.EqualTo("ThingDef/BaseApparel"));
        }

        [Test]
        public void InheritanceEdge_UnknownParent_SerializesNullParentNodeId()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("ThingDef", "Foo", null, "mod.a", "A/Foo.xml", "MissingBase");

            JsonElement edge = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("inheritanceEdges")[0];

            Assert.That(edge.GetProperty("parentNodeId").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }

        [Test]
        public void InheritanceEdge_ResolvesAbstractParent_ByNameAttribute()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // Abstract base: a Name attribute and no defName. The concrete child
            // references it by ParentName. Most ParentName targets are abstract bases
            // like this, so resolving them is what makes inheritance fan-out work.
            graph.AddNode("ThingDef", null, "BuildingBase", "ludeon.rimworld", "Core/Buildings.xml", null);
            graph.AddNode("ThingDef", "Wall", null, "ludeon.rimworld", "Core/Wall.xml", "BuildingBase");

            JsonElement edge = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("inheritanceEdges").EnumerateArray()
                .Single(e => e.GetProperty("childNodeId").GetString() == "ThingDef/Wall");
            Assert.That(edge.GetProperty("parentName").GetString(), Is.EqualTo("BuildingBase"));
            Assert.That(edge.GetProperty("parentNodeId").GetString(), Is.EqualTo("ThingDef@BuildingBase"));
        }

        [Test]
        public void InheritanceEdge_AbstractParent_ResolvesRegardlessOfOrder()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // Child registered before the abstract parent: resolution is deferred to
            // serialization, so order must not matter.
            graph.AddNode("ThingDef", "Wall", null, "m", "Wall.xml", "BuildingBase");
            graph.AddNode("ThingDef", null, "BuildingBase", "m", "Base.xml", null);

            JsonElement edge = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("inheritanceEdges")[0];
            Assert.That(edge.GetProperty("parentNodeId").GetString(), Is.EqualTo("ThingDef@BuildingBase"));
        }

        [Test]
        public void InheritanceEdge_MultiLevelAbstract_Resolves()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // Wall -> BuildingBase (abstract) -> ThingBase (abstract). Abstract bases can
            // themselves inherit, so each link must resolve.
            graph.AddNode("ThingDef", null, "ThingBase", "m", "T.xml", null);
            graph.AddNode("ThingDef", null, "BuildingBase", "m", "B.xml", "ThingBase");
            graph.AddNode("ThingDef", "Wall", null, "m", "W.xml", "BuildingBase");

            var byChild = JsonDocument.Parse(graph.Serialize(0)).RootElement
                .GetProperty("inheritanceEdges").EnumerateArray()
                .ToDictionary(e => e.GetProperty("childNodeId").GetString(),
                              e => e.GetProperty("parentNodeId").GetString());
            Assert.That(byChild["ThingDef/Wall"], Is.EqualTo("ThingDef@BuildingBase"));
            Assert.That(byChild["ThingDef@BuildingBase"], Is.EqualTo("ThingDef@ThingBase"));
        }

        [Test]
        public void AbstractNode_RegisteredInNodes_KeyedByName()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("ThingDef", null, "BuildingBase", "ludeon.rimworld", "Core/B.xml", null);

            JsonElement node = JsonDocument.Parse(graph.Serialize(0)).RootElement
                .GetProperty("nodes").EnumerateArray()
                .Single(n => n.GetProperty("id").GetString() == "ThingDef@BuildingBase");
            Assert.That(node.GetProperty("defType").GetString(), Is.EqualTo("ThingDef"));
            Assert.That(node.GetProperty("defName").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }

        [Test]
        public void AddNode_LaterModSameDefName_OverwritesSourceModAndIndexesOverride()
        {
            // Issue #43: a later mod's Defs file re-declares the same defName as an earlier
            // one (e.g. a vanilla GeneDef), with no PatchOperation involved -- exactly how
            // Verse.DefDatabase<T>.Add resolves duplicates (last-loaded registration wins).
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("GeneDef", "Eyes_Red", null, "ludeon.rimworld.biotech",
                "Data/Biotech/Defs/GeneDefs/GeneDefs_Cosmetic.xml", null);
            graph.AddNode("GeneDef", "Eyes_Red", null, "oppey.eyegenes2",
                "1.6/Defs/GeneDefs/GeneDefs_Cosmetic.xml", null);

            JsonElement root = JsonDocument.Parse(graph.Serialize(0)).RootElement;
            JsonElement node = root.GetProperty("nodes").EnumerateArray()
                .Single(n => n.GetProperty("id").GetString() == "GeneDef/Eyes_Red");
            // Last-write-wins: the node now reports the OVERRIDING mod, mirroring what the
            // real DefDatabase actually serves.
            Assert.That(node.GetProperty("sourceMod").GetString(), Is.EqualTo("oppey.eyegenes2"));

            JsonElement overrides = root.GetProperty("defOverrides");
            JsonElement owned = overrides.GetProperty("oppey.eyegenes2");
            Assert.That(owned.EnumerateArray().Select(e => e.GetString()),
                Is.EquivalentTo(new[] { "GeneDef/Eyes_Red" }));
        }

        [Test]
        public void AddNode_ThreeWayChain_KeepsStaleIntermediateOwnerInDefOverrides()
        {
            // A three-way override chain (vanilla -> A -> B): AddNode's override branch only
            // ever ADDS the new owner to defOverrides, it never removes the previous owner's
            // entry. So after B supersedes A, defOverrides must still list A's now-stale entry
            // for the same node -- that staleness is what lets Seed 7 correctly dirty the node
            // if A alone is later removed from the load (B still owns it either way, so the
            // dirty is a safe over-approximation rather than a miss).
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("GeneDef", "Eyes_Red", null, "ludeon.rimworld.biotech",
                "Data/Biotech/Defs/GeneDefs/GeneDefs_Cosmetic.xml", null);
            graph.AddNode("GeneDef", "Eyes_Red", null, "modA.eyegenes",
                "1.6/Defs/GeneDefs/GeneDefs_Cosmetic.xml", null);
            graph.AddNode("GeneDef", "Eyes_Red", null, "modB.eyegenes2",
                "1.6/Defs/GeneDefs/GeneDefs_Cosmetic.xml", null);

            JsonElement root = JsonDocument.Parse(graph.Serialize(0)).RootElement;
            JsonElement node = root.GetProperty("nodes").EnumerateArray()
                .Single(n => n.GetProperty("id").GetString() == "GeneDef/Eyes_Red");
            // Last-write-wins: node reports the FINAL owner, B.
            Assert.That(node.GetProperty("sourceMod").GetString(), Is.EqualTo("modB.eyegenes2"));

            JsonElement overrides = root.GetProperty("defOverrides");
            // Both A's stale entry and B's current entry must be present.
            Assert.That(overrides.GetProperty("modA.eyegenes").EnumerateArray()
                .Select(e => e.GetString()), Is.EquivalentTo(new[] { "GeneDef/Eyes_Red" }));
            Assert.That(overrides.GetProperty("modB.eyegenes2").EnumerateArray()
                .Select(e => e.GetString()), Is.EquivalentTo(new[] { "GeneDef/Eyes_Red" }));
        }

        [Test]
        public void AddNode_SameModReregisters_DoesNotIndexAsOverride()
        {
            // Re-registering under the SAME sourceMod (e.g. the same file processed twice)
            // is not a real override and must not pollute defOverrides.
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("GeneDef", "Eyes_Red", null, "ludeon.rimworld.biotech", "A.xml", null);
            graph.AddNode("GeneDef", "Eyes_Red", null, "ludeon.rimworld.biotech", "A.xml", null);

            JsonElement overrides = JsonDocument.Parse(graph.Serialize(0)).RootElement
                .GetProperty("defOverrides");
            Assert.That(overrides.EnumerateObject(), Is.Empty);
        }

        [Test]
        public void AddNode_SecondRegistrationWithNullSourceMod_DoesNotClobberAttribution()
        {
            // A def can get RegisterNode called twice with no resolvable LoadableXmlAsset on
            // the second call (sourceMod == null) -- observed live for V.Rooboid.Faun's
            // RBM_UnguligradeLegs GeneDef/FurDef, which otherwise capture correctly for every
            // OTHER def in the same file. Before the fix, the unguarded override branch
            // treated the null second call as a "later mod re-declares" case and overwrote a
            // perfectly valid sourceMod/sourceFile with null -- silently making the node
            // invisible to every sourceMod-keyed seed (Seed 7 and any future removed-owner
            // seed) even though a single, real, still-loaded mod owns it.
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("GeneDef", "RBM_UnguligradeLegs", null, "v.rooboid.faun",
                "Defs/GeneDefs/RBSF_GeneDefs.xml", null);
            graph.AddNode("GeneDef", "RBM_UnguligradeLegs", null, null, null, null);

            JsonElement node = JsonDocument.Parse(graph.Serialize(0)).RootElement
                .GetProperty("nodes").EnumerateArray()
                .Single(n => n.GetProperty("id").GetString() == "GeneDef/RBM_UnguligradeLegs");
            Assert.That(node.GetProperty("sourceMod").GetString(), Is.EqualTo("v.rooboid.faun"));
            Assert.That(node.GetProperty("sourceFile").GetString(),
                Is.EqualTo("Defs/GeneDefs/RBSF_GeneDefs.xml"));

            JsonElement overrides = JsonDocument.Parse(graph.Serialize(0)).RootElement
                .GetProperty("defOverrides");
            Assert.That(overrides.EnumerateObject(), Is.Empty);
        }

        [Test]
        public void InheritanceEdge_ConcreteParentWithNameAttr_ResolvesToConcreteId()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // A concrete def can also be an inheritance template (both defName and Name).
            // A child referencing it by Name must resolve to the concrete node id, not a
            // separate abstract one.
            graph.AddNode("ThingDef", "Steel", "ResourceBase", "ludeon.rimworld", "Core/Steel.xml", null);
            graph.AddNode("ThingDef", "Plasteel", null, "ludeon.rimworld", "Core/Plasteel.xml", "ResourceBase");

            JsonElement edge = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("inheritanceEdges").EnumerateArray()
                .Single(e => e.GetProperty("childNodeId").GetString() == "ThingDef/Plasteel");
            Assert.That(edge.GetProperty("parentNodeId").GetString(), Is.EqualTo("ThingDef/Steel"));
        }

        [Test]
        public void InheritanceEdge_SameNameDifferentDefType_ResolvesToOwnDefType()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // Two unrelated defs share the bare name "HotSpring": a ThoughtDef abstract base
            // and a concrete TerrainDef. A TerrainDef child with ParentName="HotSpring" must
            // resolve to the TerrainDef parent, never the ThoughtDef — RimWorld inheritance is
            // defType-scoped. The old bare-name resolver could hand it the ThoughtDef.
            graph.AddNode("ThoughtDef", null, "HotSpring", "m", "T.xml", null);          // abstract ThoughtDef
            graph.AddNode("TerrainDef", "HotSpring", null, "m", "Te.xml", null);         // concrete TerrainDef
            graph.AddNode("TerrainDef", "SpringFlood", null, "m", "S.xml", "HotSpring"); // child

            JsonElement edge = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("inheritanceEdges").EnumerateArray()
                .Single(e => e.GetProperty("childNodeId").GetString() == "TerrainDef/SpringFlood");
            Assert.That(edge.GetProperty("parentNodeId").GetString(), Is.EqualTo("TerrainDef/HotSpring"));
        }

        [Test]
        public void InheritanceEdge_CrossDefTypeTemplate_ResolvesWhenNoSameTypeMatch()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // A legitimate cross-defType template reference: the only node named "SharedBase"
            // is a ThingDef abstract base, and a TerrainDef child points at it by ParentName.
            // With no same-defType match, the bare-name fallback must still resolve it so we
            // don't regress the rare-but-valid cross-type case.
            graph.AddNode("ThingDef", null, "SharedBase", "m", "B.xml", null);
            graph.AddNode("TerrainDef", "Child", null, "m", "C.xml", "SharedBase");

            JsonElement edge = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("inheritanceEdges").EnumerateArray()
                .Single(e => e.GetProperty("childNodeId").GetString() == "TerrainDef/Child");
            Assert.That(edge.GetProperty("parentNodeId").GetString(), Is.EqualTo("ThingDef@SharedBase"));
        }

        [Test]
        public void InheritanceEdge_MultiLevelThroughAbstractWithDefName_ResolvesEndToEnd()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // The defect-1 chain: a concrete leaf inherits an abstract base that ALSO declares
            // a defName (registered here with defName=null so it keeps the "@{Name}" id), which
            // in turn inherits a real base. Every link must resolve so a change to the top base
            // fans out to the leaf.
            graph.AddNode("TerrainDef", null, "NaturalTerrainBase", "m", "N.xml", null);
            graph.AddNode("TerrainDef", null, "MF_VoidTerrainBase", "m", "V.xml", "NaturalTerrainBase");
            graph.AddNode("TerrainDef", "MF_SpaceVoid", null, "m", "S.xml", "MF_VoidTerrainBase");

            var byChild = JsonDocument.Parse(graph.Serialize(0)).RootElement
                .GetProperty("inheritanceEdges").EnumerateArray()
                .ToDictionary(e => e.GetProperty("childNodeId").GetString(),
                              e => e.GetProperty("parentNodeId").GetString());
            Assert.That(byChild["TerrainDef/MF_SpaceVoid"], Is.EqualTo("TerrainDef@MF_VoidTerrainBase"));
            Assert.That(byChild["TerrainDef@MF_VoidTerrainBase"], Is.EqualTo("TerrainDef@NaturalTerrainBase"));
        }

        [Test]
        public void AbstractWithDefName_RegisteredAsAtNameNode_BumpsAbstractCount()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // RegisterAbstract passes defName=null for an abstract-with-defName base so its
            // identity stays the "{DefType}@{Name}" abstract shape (and counts as abstract).
            graph.AddNode("TerrainDef", null, "MF_VoidTerrainBase", "m", "V.xml", null);

            JsonDocument parsed = JsonDocument.Parse(graph.Serialize(0));
            JsonElement node = parsed.RootElement.GetProperty("nodes").EnumerateArray()
                .Single(n => n.GetProperty("id").GetString() == "TerrainDef@MF_VoidTerrainBase");
            Assert.That(node.GetProperty("defName").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(parsed.RootElement.GetProperty("metrics")
                .GetProperty("abstractNodeCount").GetInt32(), Is.EqualTo(1));
        }

        [Test]
        public void PatchEdge_AccumulatesAcrossMultipleApplies()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            // Same patchId observed twice (e.g. a wildcard that matched two defs
            // across separate Apply observations) collapses to one edge.
            graph.AddPatchEdge("mod#0", "mod", "PatchOperationAdd", "//comps",
                new[] { "ThingDef/Steel" }, new[] { "ThingDef/Steel" });
            graph.AddPatchEdge("mod#0", "mod", "PatchOperationAdd", "//comps",
                new[] { "ThingDef/Plasteel" }, new[] { "ThingDef/Plasteel" });

            JsonElement edges = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("patchEdges");
            Assert.That(edges.GetArrayLength(), Is.EqualTo(1));
            List<string> matched = edges[0].GetProperty("matchedNodeIds")
                .EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(matched, Is.EquivalentTo(new[] { "ThingDef/Steel", "ThingDef/Plasteel" }));
        }

        [Test]
        public void Serialize_EscapesSpecialCharacters_InXpath()
        {
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddPatchEdge("mod#0", "mod", "PatchOperationReplace",
                "Defs/ThingDef[defName=\"A\\B\"]", null, null);

            // Must be parseable (i.e. quotes/backslashes escaped) and round-trip.
            JsonElement edge = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("patchEdges")[0];
            Assert.That(edge.GetProperty("xpath").GetString(),
                Is.EqualTo("Defs/ThingDef[defName=\"A\\B\"]"));
        }

        [Test]
        public void MayRequire_SerializesPackageToNodeIds()
        {
            // P4 capture side: each gated def is indexed under every packageId that gates it,
            // de-duplicated, and emitted as a { packageId: [nodeId, ...] } object.
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddMayRequire("vanillaexpanded.vmemese", "ThingStyleDef/TST_Hedonist_KneelSheet");
            graph.AddMayRequire("vanillaexpanded.vmemese", "FactionDef/Crows_VelosEnclave");
            graph.AddMayRequire("vanillaexpanded.vmemese", "FactionDef/Crows_VelosEnclave"); // dup
            graph.AddMayRequire("ludeon.rimworld.biotech", "ThingDef/Gene_Example");

            JsonElement mayRequire = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("mayRequire");

            var vmemese = mayRequire.GetProperty("vanillaexpanded.vmemese")
                .EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(vmemese, Is.EquivalentTo(new[]
            {
                "ThingStyleDef/TST_Hedonist_KneelSheet", "FactionDef/Crows_VelosEnclave"
            }));
            Assert.That(mayRequire.GetProperty("ludeon.rimworld.biotech").GetArrayLength(),
                Is.EqualTo(1));
        }

        [Test]
        public void MayRequire_EmptyIndex_SerializesEmptyObject()
        {
            // A graph with no MayRequire content still emits the field (an empty object) so the
            // schema is stable and the parser's lookup is uniform.
            JsonElement mayRequire = JsonDocument.Parse(new ProvenanceGraph().Serialize(0))
                .RootElement.GetProperty("mayRequire");
            Assert.That(mayRequire.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(mayRequire.EnumerateObject().Any(), Is.False);
        }

        [Test]
        public void TypeProviders_SerializesPackageToNodeIds()
        {
            // Issue #86 capture side: each def whose .NET Type came from a mod's own assembly is
            // indexed under that mod's packageId, de-duplicated, and emitted as a
            // { packageId: [nodeId, ...] } object -- same shape as mayRequire above.
            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddTypeProvider("nals.facialanimation", "FacialAnimation.EyeballColorDef/EC_Blue");
            graph.AddTypeProvider("nals.facialanimation", "FacialAnimation.EyeballColorDef/EC_Green");
            graph.AddTypeProvider("nals.facialanimation", "FacialAnimation.EyeballColorDef/EC_Green"); // dup
            graph.AddTypeProvider("ludeon.rimworld.biotech", "ThingDef/Gene_Example");

            JsonElement typeProviders = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("typeProviders");

            var facialAnimation = typeProviders.GetProperty("nals.facialanimation")
                .EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.That(facialAnimation, Is.EquivalentTo(new[]
            {
                "FacialAnimation.EyeballColorDef/EC_Blue", "FacialAnimation.EyeballColorDef/EC_Green"
            }));
            Assert.That(typeProviders.GetProperty("ludeon.rimworld.biotech").GetArrayLength(),
                Is.EqualTo(1));
        }

        [Test]
        public void TypeProviders_EmptyIndex_SerializesEmptyObject()
        {
            // A graph with no type-provider content still emits the field (an empty object) so
            // the schema is stable and the parser's lookup is uniform.
            JsonElement typeProviders = JsonDocument.Parse(new ProvenanceGraph().Serialize(0))
                .RootElement.GetProperty("typeProviders");
            Assert.That(typeProviders.ValueKind, Is.EqualTo(JsonValueKind.Object));
            Assert.That(typeProviders.EnumerateObject().Any(), Is.False);
        }

        [Test]
        public void Serialize_RiskyMods_ListsGeneratedAndDocPathOwnersOnly()
        {
            var g = new ProvenanceGraph();
            // A normal safe-leaf op keyed to a def -> its mod is NOT risky.
            g.AddPatchEdge("modA#0", "modA", "PatchOperationReplace",
                "Defs/ThingDef[defName=\"X\"]/label", new[] { "ThingDef/X" }, new[] { "ThingDef/X" });
            // A dynamically-generated op (apply-stack id) -> modB is risky.
            g.AddPatchEdge("modB#1.generated[0]", "modB", "PatchOperationAdd",
                "Defs/ThingDef[defName=\"Y\"]", new[] { "ThingDef/Y" }, new[] { "ThingDef/Y" });
            // A /Defs-root add (documentPathFallback: node id is the "Defs" doc path) -> modC is risky.
            g.AddPatchEdge("modC#2", "modC", "PatchOperationAdd", "Defs",
                new[] { "Defs" }, new[] { "Defs" });

            List<string> risky = JsonDocument.Parse(g.Serialize(0))
                .RootElement.GetProperty("riskyMods").EnumerateArray()
                .Select(e => e.GetString()).ToList();

            Assert.That(risky, Is.EqualTo(new[] { "modB", "modC" })); // sorted, modA excluded
        }
    }
}
