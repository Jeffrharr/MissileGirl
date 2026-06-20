// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// ProvenanceGraph.cs (Piece A — provenance capture)
//
// Contains: the ProvenanceGraph type — the dependency-graph data model (def
// nodes, patch edges, inheritance edges), the node-keying / inheritance-
// resolution logic, and a hand-rolled JSON serializer for DependencyGraph.json.
//
// Used for: holding the provenance recorded during a cold load so it can be
// persisted; ProvenanceRecorder is the RimWorld-facing layer that feeds it.
//
// Why: the incremental cache needs to know which patches and inheritance edges
// touched which defs in order to recompute only the affected ones. This type is
// deliberately free of any RimWorld dependency so the load-bearing keying and
// serialization logic can be unit-tested offline against synthetic XmlDocuments.

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

        // Memoizes KeyForNode by node reference. The same nodes are matched by many
        // patches during a load (popular base defs especially), and keying walks the
        // ancestor chain + scans for <defName>; caching turns the repeat work into a
        // dictionary lookup. Node identities are stable across a build, so this is
        // safe; the rare defName/Name rename via a patch is acceptable for a dev-only
        // provenance artifact.
        private readonly Dictionary<XmlNode, string> keyCache =
            new Dictionary<XmlNode, string>();

        public int NodeCount => nodes.Count;
        public int PatchEdgeCount => patchEdges.Count;
        public int InheritanceEdgeCount => inheritanceEdges.Count;
        // How many times KeyForNode fell back to DocumentPath (NearestDefElement
        // returned null). High values indicate nodes outside a <Defs> root or an
        // unexpected document structure worth investigating.
        public int DocumentPathFallbackCount { get; private set; }

        public void Reset()
        {
            nodes.Clear();
            patchEdges.Clear();
            inheritanceEdges.Clear();
            defNameToNodeId.Clear();
            pendingInheritance.Clear();
            keyCache.Clear();
            DocumentPathFallbackCount = 0;
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

        private void AddNodeId(HashSet<string> set, XmlNode node)
        {
            string id = KeyForNode(node);
            if (!string.IsNullOrEmpty(id))
                set.Add(id);
        }

        // Resolves the stable node id for an arbitrary XML node. Walks up to the
        // nearest top-level def element (a direct child of <Defs>) and keys it as
        // "{defType}/{defName}"; falls back to a positional document path.
        public string KeyForNode(XmlNode node)
        {
            if (node == null)
                return null;
            if (keyCache.TryGetValue(node, out string cached))
                return cached;

            string key;
            XmlElement defElement = NearestDefElement(node);
            if (defElement != null)
            {
                // Concrete defs key by <defName>. Abstract / Name-based parent defs
                // have no defName at patch time (inheritance is resolved later), so
                // key them — and any node inside them — by the def's Name attribute.
                // This is a stable identity (a positional document path is not, and
                // it loses the inheritance hook needed to fan a base-def change out to
                // its descendants during incremental recompute). The document path is
                // only a last resort for nodes genuinely outside any def.
                string defName = defElement["defName"]?.InnerText;
                if (!string.IsNullOrEmpty(defName))
                {
                    key = $"{defElement.Name}/{defName}";
                }
                else
                {
                    string nameAttr = defElement.GetAttribute("Name");
                    if (!string.IsNullOrEmpty(nameAttr))
                    {
                        key = $"{defElement.Name}@{nameAttr}";
                    }
                    else
                    {
                        DocumentPathFallbackCount++;
                        key = DocumentPath(node);
                    }
                }
            }
            else
            {
                DocumentPathFallbackCount++;
                key = DocumentPath(node);
            }

            keyCache[node] = key;
            return key;
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
                sb.Append("{\"id\":"); AppendQ(sb, node.id);
                sb.Append(",\"defType\":"); AppendQ(sb, node.defType);
                sb.Append(",\"defName\":"); AppendQ(sb, node.defName);
                sb.Append(",\"sourceMod\":"); AppendQ(sb, node.sourceMod);
                sb.Append(",\"sourceFile\":"); AppendQ(sb, node.sourceFile);
                sb.Append('}');
            }
            sb.Append("],");

            sb.Append("\"patchEdges\":[");
            first = true;
            foreach (PatchEdge edge in patchEdges.Values)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"patchId\":"); AppendQ(sb, edge.patchId);
                sb.Append(",\"sourceMod\":"); AppendQ(sb, edge.sourceMod);
                sb.Append(",\"operationType\":"); AppendQ(sb, edge.operationType);
                sb.Append(",\"xpath\":"); AppendQ(sb, edge.xpath);
                sb.Append(",\"matchedNodeIds\":"); AppendArr(sb, edge.matchedNodeIds);
                sb.Append(",\"modifiedNodeIds\":"); AppendArr(sb, edge.modifiedNodeIds);
                sb.Append('}');
            }
            sb.Append("],");

            sb.Append("\"inheritanceEdges\":[");
            first = true;
            foreach (InheritanceEdge edge in inheritanceEdges)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append("{\"childNodeId\":"); AppendQ(sb, edge.childNodeId);
                sb.Append(",\"parentName\":"); AppendQ(sb, edge.parentName);
                sb.Append(",\"parentNodeId\":"); AppendQ(sb, edge.parentNodeId);
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
                $"\"documentPathFallbacks\":{DocumentPathFallbackCount}," +
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

        private static void AppendArr(StringBuilder sb, HashSet<string> ids)
        {
            sb.Append('[');
            bool first = true;
            foreach (string id in ids)
            {
                if (!first) sb.Append(',');
                first = false;
                AppendQ(sb, id);
            }
            sb.Append(']');
        }

        // Appends a JSON string literal (with escaping) directly into sb.
        // Null serializes as JSON null.
        private static void AppendQ(StringBuilder sb, string value)
        {
            if (value == null)
            {
                sb.Append("null");
                return;
            }

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
        }
    }
}
