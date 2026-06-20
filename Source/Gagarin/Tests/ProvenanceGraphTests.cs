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
        public void Serialize_ProducesValidSchema_WithCorrectCounts()
        {
            XmlDocument doc = BuildDefsDoc("ThingDef:Steel", "ThingDef:Plasteel");
            XmlElement steel = (XmlElement)doc.DocumentElement.ChildNodes[0];

            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("ThingDef", "Steel", null, "ludeon.rimworld", "Core/Steel.xml", null);
            graph.AddNode("ThingDef", "Plasteel", null, "ludeon.rimworld", "Core/Plasteel.xml", null);
            graph.AddPatchEdge("cete.combatextended#42", "cete.combatextended",
                "PatchOperationReplace", "Defs/ThingDef[defName=\"Steel\"]/statBases",
                new[] { (XmlNode)steel }, new[] { (XmlNode)steel });

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
        public void PatchEdge_AccumulatesAcrossMultipleApplies()
        {
            XmlDocument doc = BuildDefsDoc("ThingDef:Steel", "ThingDef:Plasteel");
            XmlNode steel = doc.DocumentElement.ChildNodes[0];
            XmlNode plasteel = doc.DocumentElement.ChildNodes[1];

            ProvenanceGraph graph = new ProvenanceGraph();
            // Same patchId observed twice (e.g. a wildcard that matched two defs
            // across separate Apply observations) collapses to one edge.
            graph.AddPatchEdge("mod#0", "mod", "PatchOperationAdd", "//comps",
                new[] { steel }, new[] { steel });
            graph.AddPatchEdge("mod#0", "mod", "PatchOperationAdd", "//comps",
                new[] { plasteel }, new[] { plasteel });

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
    }
}
