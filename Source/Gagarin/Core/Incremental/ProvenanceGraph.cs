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

using System;
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

        // MayRequire index (P4): packageId -> set of def node ids whose resolved content
        // carries a MayRequire / MayRequireAnyOf gated on that packageId. Captured by
        // scanning the patched document (see ProvenanceRecorder.IndexMayRequire). It is
        // what lets a dirty-set seed fire when a mod is added to / removed from the load:
        // such a def lives in an UNCHANGED mod with an UNCHANGED file, so no structural
        // seed reaches it, yet its resolved value flips with the gated mod's presence.
        // Keyed case-insensitively because RimWorld treats packageIds that way and the
        // same mod is referenced with different casing across authors
        // (e.g. "vanillaexpanded.vmemese" vs "VanillaExpanded.VMemesE").
        private readonly Dictionary<string, HashSet<string>> mayRequire =
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        // ParentName resolution is keyed by a composite "{defType}:{name}" rather than a
        // bare name. RimWorld's inheritance is defType-scoped: a TerrainDef with
        // ParentName="HotSpring" inherits the TerrainDef named HotSpring, NOT a ThoughtDef
        // that happens to share the name. The old bare-name maps had no defType, so a
        // same-name node in a different defType could win the lookup, mis-wiring ~8% of
        // edges across defTypes. We resolve same-defType first via these composite maps and
        // only fall back to a bare-name/any-defType lookup below when no same-type match
        // exists (which preserves the rare legitimate cross-defType template reference).

        // "{defType}:{defName}" -> node id, so inheritance edges can resolve a same-defType
        // ParentName to a concrete parent node id during serialization (the parent def may
        // be registered after the child).
        private readonly Dictionary<string, string> defNameToNodeId =
            new Dictionary<string, string>();

        // "{defType}:{Name}" -> node id. Most ParentName references target abstract bases
        // (a Name attribute and no defName), which resolve here rather than via defName.
        private readonly Dictionary<string, string> nameAttrToNodeId =
            new Dictionary<string, string>();

        // Bare-name fallback maps (no defType), used only when no same-defType match exists.
        // First-writer-wins is acceptable: these only decide cross-defType template
        // references, which are rare and non-deterministic by nature; the common,
        // correctness-critical case is the same-defType resolution above.
        private readonly Dictionary<string, string> defNameToNodeIdAny =
            new Dictionary<string, string>();
        private readonly Dictionary<string, string> nameAttrToNodeIdAny =
            new Dictionary<string, string>();

        // Pending (childNodeId, childDefType, parentName) triples awaiting parent
        // resolution. childDefType lets ResolveInheritance try a same-defType match first.
        private readonly List<PendingEdge> pendingInheritance = new List<PendingEdge>();

        private struct PendingEdge
        {
            public string childNodeId;
            public string childDefType;
            public string parentName;
        }

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
        // Distinct packageIds referenced by any MayRequire / MayRequireAnyOf this load.
        public int MayRequirePackageCount => mayRequire.Count;
        // Total (packageId, defNodeId) pairs indexed — the seed's fan-out upper bound.
        public int MayRequireEdgeCount
        {
            get
            {
                int count = 0;
                foreach (HashSet<string> ids in mayRequire.Values)
                    count += ids.Count;
                return count;
            }
        }
        // How many times KeyForNode fell back to DocumentPath (NearestDefElement
        // returned null). High values indicate nodes outside a <Defs> root or an
        // unexpected document structure worth investigating.
        public int DocumentPathFallbackCount { get; private set; }

        // Inheritance edges whose ParentName resolved to a parent node id. Compared to
        // InheritanceEdgeCount this gives the resolution rate — a key correctness signal.
        // Valid after Serialize/ResolveInheritance has run.
        public int InheritanceResolvedCount
        {
            get
            {
                int count = 0;
                foreach (InheritanceEdge edge in inheritanceEdges)
                    if (!string.IsNullOrEmpty(edge.parentNodeId))
                        count++;
                return count;
            }
        }

        // Nodes with no defName: abstract / Name-based templates.
        public int AbstractNodeCount
        {
            get
            {
                int count = 0;
                foreach (NodeRecord node in nodes.Values)
                    if (string.IsNullOrEmpty(node.defName))
                        count++;
                return count;
            }
        }

        public void Reset()
        {
            nodes.Clear();
            patchEdges.Clear();
            inheritanceEdges.Clear();
            defNameToNodeId.Clear();
            nameAttrToNodeId.Clear();
            defNameToNodeIdAny.Clear();
            nameAttrToNodeIdAny.Clear();
            pendingInheritance.Clear();
            mayRequire.Clear();
            keyCache.Clear();
            DocumentPathFallbackCount = 0;
        }

        // Adds a def node. parentName (if any) is queued for resolution against
        // the parent's node id once all nodes are known.
        public void AddNode(string defType, string defName, string nameAttr,
            string sourceMod, string sourceFile, string parentName)
        {
            // Concrete defs key by defName; abstract / Name-based templates (which have
            // no defName at patch time) key by their Name attribute, matching how
            // matched nodes are keyed in patch edges. A node with neither has nothing
            // stable to key on and is skipped.
            string id = !string.IsNullOrEmpty(defName)
                ? $"{defType}/{defName}"
                : (!string.IsNullOrEmpty(nameAttr) ? $"{defType}@{nameAttr}" : null);
            if (id == null)
                return;

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

            // Populate the resolution maps keyed by defType so a same-defType ParentName
            // lookup wins over an unrelated same-name node in a different defType. The
            // bare-name maps are kept purely as a fallback for legitimate cross-defType
            // template references (first writer wins).
            if (!string.IsNullOrEmpty(defName))
            {
                defNameToNodeId[$"{defType}:{defName}"] = id;
                if (!defNameToNodeIdAny.ContainsKey(defName))
                    defNameToNodeIdAny[defName] = id;
            }
            // A node may carry both a defName and a Name (a concrete inheritance
            // template); mapping the Name to the same id lets children that reference it
            // by ParentName resolve to the concrete node.
            if (!string.IsNullOrEmpty(nameAttr))
            {
                nameAttrToNodeId[$"{defType}:{nameAttr}"] = id;
                if (!nameAttrToNodeIdAny.ContainsKey(nameAttr))
                    nameAttrToNodeIdAny[nameAttr] = id;
            }

            if (!string.IsNullOrEmpty(parentName))
                pendingInheritance.Add(new PendingEdge
                {
                    childNodeId = id,
                    childDefType = defType,
                    parentName = parentName
                });
        }

        // Indexes one MayRequire / MayRequireAnyOf dependency: def node nodeId carries
        // (directly, or via a patch that injected the gated content) a requirement on
        // packageId. Empty/null inputs are ignored. A MayRequireAnyOf listing several
        // packages calls this once per package — conservatively, ANY of them entering or
        // leaving the load can flip the def's inclusion, so we want the seed to fire on
        // each; over-approximating here only over-dirties (superset-safe), never misses.
        public void AddMayRequire(string packageId, string nodeId)
        {
            if (string.IsNullOrEmpty(packageId) || string.IsNullOrEmpty(nodeId))
                return;
            if (!mayRequire.TryGetValue(packageId, out HashSet<string> ids))
            {
                ids = new HashSet<string>();
                mayRequire[packageId] = ids;
            }
            ids.Add(nodeId);
        }

        // Records matched/modified node ids for a single PatchOperation. The ids are
        // computed by the caller at selection time (while the nodes are still attached
        // to the document) via KeyForNode, NOT here: Replace/Remove operations detach
        // their matched nodes before our postfix runs, so keying them afterwards would
        // lose the def ancestry and collapse distinct defs onto a bare element name.
        public void AddPatchEdge(string patchId, string sourceMod, string operationType,
            string xpath, IEnumerable<string> matchedNodeIds, IEnumerable<string> modifiedNodeIds)
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

            AddIds(edge.matchedNodeIds, matchedNodeIds);
            AddIds(edge.modifiedNodeIds, modifiedNodeIds);
        }

        private static void AddIds(HashSet<string> set, IEnumerable<string> ids)
        {
            if (ids == null)
                return;
            foreach (string id in ids)
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
            foreach (PendingEdge pending in pendingInheritance)
            {
                string parentNodeId = ResolveParent(pending.childDefType, pending.parentName);
                inheritanceEdges.Add(new InheritanceEdge
                {
                    childNodeId = pending.childNodeId,
                    parentName = pending.parentName,
                    parentNodeId = parentNodeId
                });
            }
        }

        // Resolves a child's ParentName to a parent node id. RimWorld inheritance is
        // defType-scoped, so we resolve SAME-DEFTYPE FIRST: try the same-defType concrete
        // (defName) map, then the same-defType abstract (Name) map. Only when neither has a
        // same-type match do we fall back to a bare-name/any-defType lookup (concrete first,
        // then Name), which preserves the rare legitimate cross-defType template reference
        // without letting an unrelated same-name node hijack a same-type parent. Returns null
        // when nothing matches (the edge is then recorded unresolved).
        private string ResolveParent(string childDefType, string parentName)
        {
            if (!string.IsNullOrEmpty(childDefType))
            {
                if (defNameToNodeId.TryGetValue($"{childDefType}:{parentName}", out string sameType))
                    return sameType;
                if (nameAttrToNodeId.TryGetValue($"{childDefType}:{parentName}", out sameType))
                    return sameType;
            }

            if (defNameToNodeIdAny.TryGetValue(parentName, out string anyType))
                return anyType;
            if (nameAttrToNodeIdAny.TryGetValue(parentName, out anyType))
                return anyType;

            return null;
        }

        // Hand-rolled JSON: the schema is small, fixed, and shared with other
        // prototype pieces, so we avoid pulling in a serializer dependency. The extra
        // metric arguments are optional and additive — consumers ignore unknown metrics
        // fields — and exist so a capture self-reports the numbers a reviewer would want
        // (resolution rate, per-phase timing, mod count) rather than relying on a script.
        public string Serialize(long captureOverheadMs, long registerMs = 0,
            long recordMs = 0, int activeModCount = 0)
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

            // MayRequire index (P4): { "<packageId>": ["<nodeId>", ...], ... }. An object
            // rather than an array so the consumer (DirtySetComputer Seed 6) can look up a
            // packageId directly when a mod enters/leaves the load.
            sb.Append("\"mayRequire\":{");
            first = true;
            foreach (KeyValuePair<string, HashSet<string>> kv in mayRequire)
            {
                if (!first) sb.Append(',');
                first = false;
                AppendQ(sb, kv.Key);
                sb.Append(':');
                AppendArr(sb, kv.Value);
            }
            sb.Append("},");

            // riskyMods (issue #26): the mods that own an op with INVISIBLE-OP RISK — a producer the
            // recompute allowlist cannot vouch for. Two attributable kinds, both keyed to the owning
            // mod and read straight off the edges:
            //   - a DYNAMICALLY-GENERATED op (patchId "...generated[N]", attributed by the apply-stack);
            //   - an op that touches an UNKEYABLE node (a matched/modified id that is a positional
            //     document path, i.e. a /Defs-root match — the documentPathFallback case), so its
            //     effect cannot be tied to a stable def.
            // This is the per-mod signal the deterministic changed-mod-scoped serve rule consumes:
            // a CHANGED mod listed here has an unbounded blast radius -> full-rebuild fallback. (Ops we
            // cannot attribute to any real mod stay counted in unindexedEdgeCount below, not here.)
            var riskyMods = new SortedSet<string>(StringComparer.Ordinal);
            foreach (PatchEdge edge in patchEdges.Values)
            {
                string mod = edge.sourceMod;
                if (string.IsNullOrEmpty(mod) || mod == "unindexed")
                    continue;
                if (riskyMods.Contains(mod))
                    continue;
                if (IsGeneratedPatchId(edge.patchId) || TouchesDocPath(edge))
                    riskyMods.Add(mod);
            }
            sb.Append("\"riskyMods\":");
            AppendArr(sb, riskyMods);
            sb.Append(',');

            // serializedBytes is the UTF-8 length of the complete document,
            // including the serializedBytes field itself. The value is therefore
            // self-referential: writing it changes the length. We measure the
            // fixed parts around the value and solve for a count whose own digit
            // width is consistent (converges in one or two steps since the
            // total's digit count is stable).
            // Capture-completeness signal (issue #25): ops the index walk could not attribute are
            // bucketed under an "unindexed#" patchId by RecordPatch (dynamically-generated ops, or a
            // missed child). The recompute allowlist treats any such producing op as a capture-gap
            // fallback, so surfacing the count lets us TRACK completeness toward zero — a precondition
            // for ever serving the incremental cache (the allowlist is blind to ops absent entirely,
            // and conservative on ops it cannot attribute).
            int unindexedEdgeCount = 0;
            foreach (string id in patchEdges.Keys)
                if (id != null && id.StartsWith("unindexed#", StringComparison.Ordinal))
                    unindexedEdgeCount++;

            string before = sb.ToString() + "\"metrics\":{" +
                $"\"nodeCount\":{nodes.Count}," +
                $"\"abstractNodeCount\":{AbstractNodeCount}," +
                $"\"patchEdgeCount\":{patchEdges.Count}," +
                $"\"unindexedEdgeCount\":{unindexedEdgeCount}," +
                $"\"inheritanceEdgeCount\":{inheritanceEdges.Count}," +
                $"\"inheritanceResolvedCount\":{InheritanceResolvedCount}," +
                $"\"mayRequirePackageCount\":{MayRequirePackageCount}," +
                $"\"mayRequireEdgeCount\":{MayRequireEdgeCount}," +
                $"\"documentPathFallbacks\":{DocumentPathFallbackCount}," +
                $"\"activeModCount\":{activeModCount}," +
                $"\"registerMs\":{registerMs}," +
                $"\"recordMs\":{recordMs}," +
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

        private static void AppendArr(StringBuilder sb, IEnumerable<string> ids)
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

        // True when a patchId names a DYNAMICALLY-GENERATED op (apply-stack id "...generated[N]").
        // Look only after '#' so a packageId containing the literal cannot false-positive.
        private static bool IsGeneratedPatchId(string patchId)
        {
            if (string.IsNullOrEmpty(patchId))
                return false;
            int hash = patchId.IndexOf('#');
            string suffix = hash >= 0 ? patchId.Substring(hash + 1) : patchId;
            return suffix.IndexOf(".generated[", StringComparison.Ordinal) >= 0;
        }

        // True when an edge matched/modified an UNKEYABLE node — a node id that is a positional
        // document path rather than a stable def id (the documentPathFallback case).
        private static bool TouchesDocPath(PatchEdge edge)
        {
            foreach (string id in edge.modifiedNodeIds)
                if (IsDocPathKey(id)) return true;
            foreach (string id in edge.matchedNodeIds)
                if (IsDocPathKey(id)) return true;
            return false;
        }

        // Mirrors WildcardRematch.IsDocumentPath: KeyForNode falls back to a positional document path
        // (always starting with the <Defs> root wrapper) for a node outside any def, e.g. a /Defs-root
        // add; a real def id starts with the def TYPE ("ThingDef/Steel"). Such an id is an unstable,
        // unattributable-to-a-def producer.
        private static bool IsDocPathKey(string id)
            => id != null && (id == "Defs" || id.StartsWith("Defs/", StringComparison.Ordinal));

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
