// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace Gagarin
{
    // The dependency graph data model plus its pure logic: node keying,
    // inheritance resolution and JSON serialization. Deliberately free of any
    // RimWorld dependency so it can be unit-tested offline against synthetic
    // XmlDocument inputs. ProvenanceRecorder is the RimWorld-facing layer that
    // feeds this during a cold load.
    public class ProvenanceGraph
    {
        public const int SchemaVersion = 1;

        // A def node in the combined document. Id is "{defType}/{defName}"; for
        // non-def nodes we fall back to a document path (see KeyForNode).
        private class NodeRecord
        {
            public string id;
            public string defType;
            public string defName;
            public string sourceMod;
            public string sourceFile;
        }

        // Per top-level PatchOperation. matched/modified accumulate across the
        // Apply invocations observed for that operation.
        private class PatchEdge
        {
            public string patchId;
            public string sourceMod;
            public string operationType;
            public string xpath;
            public readonly HashSet<string> matchedNodeIds = new HashSet<string>();
            public readonly HashSet<string> modifiedNodeIds = new HashSet<string>();
        }

        private class InheritanceEdge
        {
            public string childNodeId;
            public string parentName;
            public string parentNodeId;
        }

        // Keyed by node id so repeated registrations collapse to one node.
        private readonly Dictionary<string, NodeRecord> nodes =
            new Dictionary<string, NodeRecord>();

        // Keyed by patchId so multiple Apply calls for one operation accumulate
        // into a single edge.
        private readonly Dictionary<string, PatchEdge> patchEdges =
            new Dictionary<string, PatchEdge>();

        private readonly List<InheritanceEdge> inheritanceEdges =
            new List<InheritanceEdge>();

        // defName -> node id, so inheritance edges can resolve a ParentName to a
        // concrete parent node id during serialization (the parent def may be
        // registered after the child).
        private readonly Dictionary<string, string> defNameToNodeId =
            new Dictionary<string, string>();

        // Pending (childNodeId, parentName) pairs awaiting parent resolution.
        private readonly List<KeyValuePair<string, string>> pendingInheritance =
            new List<KeyValuePair<string, string>>();

        public int NodeCount => nodes.Count;
        public int PatchEdgeCount => patchEdges.Count;
        public int InheritanceEdgeCount => inheritanceEdges.Count;

        public void Reset()
        {
            nodes.Clear();
            patchEdges.Clear();
            inheritanceEdges.Clear();
            defNameToNodeId.Clear();
            pendingInheritance.Clear();
        }

        // Adds a def node. parentName (if any) is queued for resolution against
        // the parent's node id once all nodes are known.
        public void AddNode(string defType, string defName, string sourceMod,
            string sourceFile, string parentName)
        {
            string id = $"{defType}/{defName}";
            if (!nodes.ContainsKey(id))
            {
                nodes[id] = new NodeRecord
                {
                    id = id,
                    defType = defType,
                    defName = defName,
                    sourceMod = sourceMod,
                    sourceFile = sourceFile
                };
            }

            if (!string.IsNullOrEmpty(defName))
                defNameToNodeId[defName] = id;

            if (!string.IsNullOrEmpty(parentName))
                pendingInheritance.Add(new KeyValuePair<string, string>(id, parentName));
        }

        // Records matched/modified node ids for a single PatchOperation.
        public void AddPatchEdge(string patchId, string sourceMod, string operationType,
            string xpath, IEnumerable<XmlNode> matchedNodes, IEnumerable<XmlNode> modifiedNodes)
        {
            if (!patchEdges.TryGetValue(patchId, out PatchEdge edge))
            {
                edge = new PatchEdge
                {
                    patchId = patchId,
                    sourceMod = sourceMod,
                    operationType = operationType,
                    xpath = xpath
                };
                patchEdges[patchId] = edge;
            }
            else if (string.IsNullOrEmpty(edge.xpath))
            {
                edge.xpath = xpath;
            }

            if (matchedNodes != null)
                foreach (XmlNode node in matchedNodes)
                    AddNodeId(edge.matchedNodeIds, node);

            if (modifiedNodes != null)
                foreach (XmlNode node in modifiedNodes)
                    AddNodeId(edge.modifiedNodeIds, node);
        }

        private static void AddNodeId(HashSet<string> set, XmlNode node)
        {
            string id = KeyForNode(node);
            if (!string.IsNullOrEmpty(id))
                set.Add(id);
        }

        // Resolves the stable node id for an arbitrary XML node. Walks up to the
        // nearest top-level def element (a direct child of <Defs>) and keys it as
        // "{defType}/{defName}"; falls back to a positional document path.
        public static string KeyForNode(XmlNode node)
        {
            XmlElement defElement = NearestDefElement(node);
            if (defElement != null)
            {
                string defName = defElement["defName"]?.InnerText;
                if (!string.IsNullOrEmpty(defName))
                    return $"{defElement.Name}/{defName}";
            }
            return DocumentPath(node);
        }

        // The nearest ancestor (inclusive) that is a direct child of the document
        // root <Defs>, i.e. a top-level def element.
        private static XmlElement NearestDefElement(XmlNode node)
        {
            XmlNode current = node;
            while (current != null)
            {
                if (current is XmlElement element &&
                    current.ParentNode is XmlElement parent &&
                    parent.Name == "Defs" &&
                    parent.ParentNode is XmlDocument)
                {
                    return element;
                }
                current = current.ParentNode;
            }
            return null;
        }

        // Stable positional path for non-def nodes, e.g. "Defs/ThingDef[3]/comps".
        private static string DocumentPath(XmlNode node)
        {
            if (node == null)
                return null;

            List<string> parts = new List<string>();
            XmlNode current = node;
            while (current != null && current.NodeType == XmlNodeType.Element)
            {
                int index = 1;
                XmlNode sibling = current.PreviousSibling;
                while (sibling != null)
                {
                    if (sibling.NodeType == XmlNodeType.Element && sibling.Name == current.Name)
                        index++;
                    sibling = sibling.PreviousSibling;
                }
                parts.Insert(0, index > 1 ? $"{current.Name}[{index}]" : current.Name);
                current = current.ParentNode;
            }
            return parts.Count > 0 ? string.Join("/", parts.ToArray()) : null;
        }

        private void ResolveInheritance()
        {
            inheritanceEdges.Clear();
            foreach (KeyValuePair<string, string> pending in pendingInheritance)
            {
                defNameToNodeId.TryGetValue(pending.Value, out string parentNodeId);
                inheritanceEdges.Add(new InheritanceEdge
                {
                    childNodeId = pending.Key,
                    parentName = pending.Value,
                    parentNodeId = parentNodeId
                });
            }
        }

        // Hand-rolled JSON: the schema is small, fixed, and shared with other
        // prototype pieces, so we avoid pulling in a serializer dependency.
        public string Serialize(long captureOverheadMs)
        {
            ResolveInheritance();

            StringBuilder sb = new StringBuilder();
            sb.Append('{');
            sb.Append($"\"version\":{SchemaVersion},");

            sb.Append("\"nodes\":[");
            bool first = true;
            foreach (NodeRecord node in nodes.Values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append($"\"id\":{Q(node.id)},");
                sb.Append($"\"defType\":{Q(node.defType)},");
                sb.Append($"\"defName\":{Q(node.defName)},");
                sb.Append($"\"sourceMod\":{Q(node.sourceMod)},");
                sb.Append($"\"sourceFile\":{Q(node.sourceFile)}");
                sb.Append('}');
            }
            sb.Append("],");

            sb.Append("\"patchEdges\":[");
            first = true;
            foreach (PatchEdge edge in patchEdges.Values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append($"\"patchId\":{Q(edge.patchId)},");
                sb.Append($"\"sourceMod\":{Q(edge.sourceMod)},");
                sb.Append($"\"operationType\":{Q(edge.operationType)},");
                sb.Append($"\"xpath\":{Q(edge.xpath)},");
                sb.Append($"\"matchedNodeIds\":{Arr(edge.matchedNodeIds)},");
                sb.Append($"\"modifiedNodeIds\":{Arr(edge.modifiedNodeIds)}");
                sb.Append('}');
            }
            sb.Append("],");

            sb.Append("\"inheritanceEdges\":[");
            first = true;
            foreach (InheritanceEdge edge in inheritanceEdges)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append('{');
                sb.Append($"\"childNodeId\":{Q(edge.childNodeId)},");
                sb.Append($"\"parentName\":{Q(edge.parentName)},");
                sb.Append($"\"parentNodeId\":{Q(edge.parentNodeId)}");
                sb.Append('}');
            }
            sb.Append("],");

            // serializedBytes is the UTF-8 length of the complete document,
            // including the serializedBytes field itself. The value is therefore
            // self-referential: writing it changes the length. We measure the
            // fixed parts around the value and solve for a count whose own digit
            // width is consistent (converges in one or two steps since the
            // total's digit count is stable).
            string before = sb.ToString() + "\"metrics\":{" +
                $"\"nodeCount\":{nodes.Count}," +
                $"\"patchEdgeCount\":{patchEdges.Count}," +
                $"\"inheritanceEdgeCount\":{inheritanceEdges.Count}," +
                "\"serializedBytes\":";
            string after = $",\"captureOverheadMs\":{captureOverheadMs}}}}}";

            long fixedBytes = Encoding.UTF8.GetByteCount(before) + Encoding.UTF8.GetByteCount(after);
            long total = fixedBytes;
            for (int i = 0; i < 4; i++)
            {
                long next = fixedBytes + total.ToString().Length;
                if (next == total)
                    break;
                total = next;
            }
            return before + total + after;
        }

        private static string Arr(HashSet<string> ids)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append('[');
            bool first = true;
            foreach (string id in ids)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Q(id));
            }
            sb.Append(']');
            return sb.ToString();
        }

        // JSON string literal with escaping; null serializes as JSON null.
        private static string Q(string value)
        {
            if (value == null)
                return "null";

            StringBuilder sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
