// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// Contracts.cs (Piece C — replay harness)
//
// Contains: in-memory mirrors of the Piece A and Piece B JSON contracts —
// DependencyGraph (nodes, patch edges, inheritance edges) and PatchClassification
// (classified patches + identity index), each with FromJson/ToJson round-trips.
//
// Used for: feeding the harness real-shaped graph/classification data and proving
// it depends only on the documented schemas, not on Piece A/B code.
//
// Why: Pieces A and B may not be merged yet, so the harness reconstructs their
// outputs from a fixture; modelling only the documented fields keeps the harness
// honest about the contract surface it actually consumes.

using System.Collections.Generic;

namespace Gagarin.IncrementalReplay
{
    // In-memory mirror of the Piece A (DependencyGraph) and Piece B
    // (PatchClassification) JSON contracts. The harness consumes these as opaque
    // inputs; it does NOT depend on Piece A/B code, only on their documented schemas.

    internal sealed class GraphNode
    {
        public string Id;
        public string DefType;
        public string DefName;
        public string SourceMod;
    }

    internal sealed class PatchEdge
    {
        public string PatchId;
        public string SourceMod;
        public string OperationType;
        public string Xpath;
        public List<string> MatchedNodeIds = new List<string>();
        public List<string> ModifiedNodeIds = new List<string>();
    }

    internal sealed class InheritanceEdge
    {
        public string ChildNodeId;
        public string ParentName;
        public string ParentNodeId;
    }

    internal sealed class DependencyGraph
    {
        public List<GraphNode> Nodes = new List<GraphNode>();
        public List<PatchEdge> PatchEdges = new List<PatchEdge>();
        public List<InheritanceEdge> InheritanceEdges = new List<InheritanceEdge>();

        public static DependencyGraph FromJson(string json)
        {
            var root = (JsonObject)JsonParser.Parse(json);
            var g = new DependencyGraph();

            var nodes = root.Array("nodes");
            for (int i = 0; i < nodes.Items.Count; i++)
            {
                var o = nodes.ObjAt(i);
                g.Nodes.Add(new GraphNode
                {
                    Id = o.Str("id"), DefType = o.Str("defType"),
                    DefName = o.Str("defName"), SourceMod = o.Str("sourceMod")
                });
            }

            var pe = root.Array("patchEdges");
            for (int i = 0; i < pe.Items.Count; i++)
            {
                var o = pe.ObjAt(i);
                var edge = new PatchEdge
                {
                    PatchId = o.Str("patchId"), SourceMod = o.Str("sourceMod"),
                    OperationType = o.Str("operationType"), Xpath = o.Str("xpath")
                };
                foreach (var v in o.Array("matchedNodeIds").Items)
                    edge.MatchedNodeIds.Add(((JsonString)v).Value);
                foreach (var v in o.Array("modifiedNodeIds").Items)
                    edge.ModifiedNodeIds.Add(((JsonString)v).Value);
                g.PatchEdges.Add(edge);
            }

            var ie = root.Array("inheritanceEdges");
            for (int i = 0; i < ie.Items.Count; i++)
            {
                var o = ie.ObjAt(i);
                g.InheritanceEdges.Add(new InheritanceEdge
                {
                    ChildNodeId = o.Str("childNodeId"), ParentName = o.Str("parentName"),
                    ParentNodeId = o.Str("parentNodeId")
                });
            }
            return g;
        }

        public string ToJson()
        {
            var root = JsonValue.Obj().Set("version", JsonValue.Of(1));
            var nodes = JsonValue.Arr();
            foreach (var n in Nodes)
                nodes.Add(JsonValue.Obj()
                    .Set("id", JsonValue.Of(n.Id)).Set("defType", JsonValue.Of(n.DefType))
                    .Set("defName", JsonValue.Of(n.DefName)).Set("sourceMod", JsonValue.Of(n.SourceMod)));
            root.Set("nodes", nodes);

            var pe = JsonValue.Arr();
            foreach (var e in PatchEdges)
            {
                var matched = JsonValue.Arr();
                foreach (var m in e.MatchedNodeIds) matched.Add(JsonValue.Of(m));
                var modified = JsonValue.Arr();
                foreach (var m in e.ModifiedNodeIds) modified.Add(JsonValue.Of(m));
                pe.Add(JsonValue.Obj()
                    .Set("patchId", JsonValue.Of(e.PatchId)).Set("sourceMod", JsonValue.Of(e.SourceMod))
                    .Set("operationType", JsonValue.Of(e.OperationType)).Set("xpath", JsonValue.Of(e.Xpath))
                    .Set("matchedNodeIds", matched).Set("modifiedNodeIds", modified));
            }
            root.Set("patchEdges", pe);

            var ie = JsonValue.Arr();
            foreach (var e in InheritanceEdges)
                ie.Add(JsonValue.Obj()
                    .Set("childNodeId", JsonValue.Of(e.ChildNodeId))
                    .Set("parentName", JsonValue.Of(e.ParentName))
                    .Set("parentNodeId", JsonValue.Of(e.ParentNodeId)));
            root.Set("inheritanceEdges", ie);
            root.Set("metrics", JsonValue.Obj());
            return root.ToString();
        }
    }

    internal sealed class PatchInfo
    {
        public string PatchId;
        public string SourceMod;
        public string Classification; // "identity" | "wildcard"
        public List<string> TargetDefNames = new List<string>();
        public string Predicate;      // for wildcard: the predicate element name
    }

    internal sealed class PatchClassification
    {
        public List<PatchInfo> Patches = new List<PatchInfo>();
        // identityIndex: nodeId -> [patchId,...]
        public Dictionary<string, List<string>> IdentityIndex
            = new Dictionary<string, List<string>>();

        public static PatchClassification FromJson(string json)
        {
            var root = (JsonObject)JsonParser.Parse(json);
            var c = new PatchClassification();

            var ps = root.Array("patches");
            for (int i = 0; i < ps.Items.Count; i++)
            {
                var o = ps.ObjAt(i);
                var info = new PatchInfo
                {
                    PatchId = o.Str("patchId"), SourceMod = o.Str("sourceMod"),
                    Classification = o.Str("classification"), Predicate = o.Str("predicate")
                };
                foreach (var v in o.Array("targetDefNames").Items)
                    info.TargetDefNames.Add(((JsonString)v).Value);
                c.Patches.Add(info);
            }

            var idx = root.Get("identityIndex") as JsonObject;
            if (idx != null)
                foreach (var m in idx.Members)
                {
                    var list = new List<string>();
                    foreach (var v in ((JsonArray)m.Value).Items)
                        list.Add(((JsonString)v).Value);
                    c.IdentityIndex[m.Key] = list;
                }
            return c;
        }

        public string ToJson()
        {
            var root = JsonValue.Obj().Set("version", JsonValue.Of(1));
            var ps = JsonValue.Arr();
            int identityCount = 0, wildcardCount = 0;
            foreach (var p in Patches)
            {
                if (p.Classification == "identity") identityCount++; else wildcardCount++;
                var names = JsonValue.Arr();
                foreach (var n in p.TargetDefNames) names.Add(JsonValue.Of(n));
                ps.Add(JsonValue.Obj()
                    .Set("patchId", JsonValue.Of(p.PatchId)).Set("sourceMod", JsonValue.Of(p.SourceMod))
                    .Set("classification", JsonValue.Of(p.Classification))
                    .Set("targetDefNames", names)
                    .Set("predicate", p.Predicate == null ? JsonValue.Null : JsonValue.Of(p.Predicate)));
            }
            root.Set("patches", ps);

            var idx = JsonValue.Obj();
            foreach (var kv in IdentityIndex)
            {
                var arr = JsonValue.Arr();
                foreach (var pid in kv.Value) arr.Add(JsonValue.Of(pid));
                idx.Set(kv.Key, arr);
            }
            root.Set("identityIndex", idx);
            root.Set("summary", JsonValue.Obj()
                .Set("identityCount", JsonValue.Of(identityCount))
                .Set("wildcardCount", JsonValue.Of(wildcardCount))
                .Set("perMod", JsonValue.Obj()));
            return root.ToString();
        }
    }
}
