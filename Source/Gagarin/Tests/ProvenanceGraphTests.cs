// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

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

            Assert.That(ProvenanceGraph.KeyForNode(def), Is.EqualTo("ThingDef/Steel"));
        }

        [Test]
        public void KeyForNode_ChildOfDef_KeysToOwningDef()
        {
            XmlDocument doc = BuildDefsDoc("ThingDef:Steel");
            XmlElement def = (XmlElement)doc.DocumentElement.FirstChild;
            XmlElement statBases = doc.CreateElement("statBases");
            def.AppendChild(statBases);

            // A node nested inside a def resolves to that def's id.
            Assert.That(ProvenanceGraph.KeyForNode(statBases), Is.EqualTo("ThingDef/Steel"));
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

            Assert.That(ProvenanceGraph.KeyForNode(second), Is.EqualTo("Root/Child[2]"));
        }

        [Test]
        public void Serialize_ProducesValidSchema_WithCorrectCounts()
        {
            XmlDocument doc = BuildDefsDoc("ThingDef:Steel", "ThingDef:Plasteel");
            XmlElement steel = (XmlElement)doc.DocumentElement.ChildNodes[0];

            ProvenanceGraph graph = new ProvenanceGraph();
            graph.AddNode("ThingDef", "Steel", "ludeon.rimworld", "Core/Steel.xml", null);
            graph.AddNode("ThingDef", "Plasteel", "ludeon.rimworld", "Core/Plasteel.xml", null);
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
            graph.AddNode("ThingDef", "Steel", "ludeon.rimworld", "Core/Steel.xml", null);

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
            graph.AddNode("ThingDef", "Foo", "mod.a", "A/Foo.xml", "BaseApparel");
            graph.AddNode("ThingDef", "BaseApparel", "ludeon.rimworld", "Core/Base.xml", null);

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
            graph.AddNode("ThingDef", "Foo", "mod.a", "A/Foo.xml", "MissingBase");

            JsonElement edge = JsonDocument.Parse(graph.Serialize(0))
                .RootElement.GetProperty("inheritanceEdges")[0];

            Assert.That(edge.GetProperty("parentNodeId").ValueKind, Is.EqualTo(JsonValueKind.Null));
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
