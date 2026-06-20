// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using Gagarin.IncrementalCache.Json;

namespace Gagarin.IncrementalCache.Classification
{
    // Reads a DependencyGraph.json, classifies its patchEdges, and produces the
    // PatchClassification.json document consumed by Piece C.
    public sealed class ClassificationReport
    {
        public List<PatchClassificationResult> Patches { get; } = new List<PatchClassificationResult>();

        // nodeId -> patchIds that identity-target it. This is the cheap O(1) lookup the
        // incremental cache uses: "this def changed -> here are the patches keyed to it".
        public Dictionary<string, List<string>> IdentityIndex { get; } = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        public int IdentityCount { get; private set; }
        public int WildcardCount { get; private set; }
        public Dictionary<string, ModBreakdown> PerMod { get; } = new Dictionary<string, ModBreakdown>(StringComparer.Ordinal);

        // Headline metric: fraction of patches that are cheap to re-evaluate incrementally.
        public double IdentityRatio
        {
            get
            {
                int total = IdentityCount + WildcardCount;
                return total == 0 ? 0.0 : (double)IdentityCount / total;
            }
        }

        public static ClassificationReport FromGraph(JsonValue graphRoot)
        {
            if (!(graphRoot is JsonObject root))
                throw new FormatException("DependencyGraph root must be a JSON object.");

            var report = new ClassificationReport();

            if (!root.TryGet("patchEdges", out JsonValue edgesValue))
                throw new FormatException("DependencyGraph is missing 'patchEdges'.");
            if (!(edgesValue is JsonArray edges))
                throw new FormatException("'patchEdges' must be an array.");

            foreach (JsonValue edgeValue in edges.Items)
            {
                PatchEdge edge = ReadEdge(edgeValue);
                report.Add(edge, PatchClassifier.Classify(edge));
            }

            return report;
        }

        private void Add(PatchEdge edge, PatchClassificationResult result)
        {
            Patches.Add(result);

            ModBreakdown breakdown = GetOrCreateBreakdown(result.SourceMod);
            if (result.Classification == Classification.Identity)
            {
                IdentityCount++;
                breakdown.Identity++;
                IndexIdentity(edge, result);
            }
            else
            {
                WildcardCount++;
                breakdown.Wildcard++;
            }
        }

        // An identity patch is indexed under every node it provably touches. We prefer the
        // resolved matchedNodeIds from Piece A (authoritative), and fall back to synthesising a
        // best-effort id from the parsed defName when running on a graph without resolved ids.
        private void IndexIdentity(PatchEdge edge, PatchClassificationResult result)
        {
            var nodeIds = new List<string>();
            if (edge.MatchedNodeIds != null && edge.MatchedNodeIds.Count > 0)
            {
                nodeIds.AddRange(edge.MatchedNodeIds);
            }
            else
            {
                // No resolved ids: synthesise "<defName>" keys so the index is still usable.
                // Piece A's ids look like "ThingDef/Steel"; we can't know the defType here, so
                // we key on the bare defName. Piece C can reconcile once real ids are present.
                foreach (string defName in result.TargetDefNames)
                    nodeIds.Add(defName);
            }

            foreach (string nodeId in nodeIds)
            {
                if (string.IsNullOrEmpty(nodeId))
                    continue;
                if (!IdentityIndex.TryGetValue(nodeId, out List<string> patchIds))
                {
                    patchIds = new List<string>();
                    IdentityIndex[nodeId] = patchIds;
                }
                if (!patchIds.Contains(result.PatchId))
                    patchIds.Add(result.PatchId);
            }
        }

        private ModBreakdown GetOrCreateBreakdown(string sourceMod)
        {
            string key = sourceMod ?? "(unknown)";
            if (!PerMod.TryGetValue(key, out ModBreakdown breakdown))
            {
                breakdown = new ModBreakdown();
                PerMod[key] = breakdown;
            }
            return breakdown;
        }

        private static PatchEdge ReadEdge(JsonValue edgeValue)
        {
            if (!(edgeValue is JsonObject obj))
                throw new FormatException("Each patchEdge must be a JSON object.");

            var edge = new PatchEdge
            {
                PatchId = GetString(obj, "patchId"),
                SourceMod = GetString(obj, "sourceMod"),
                OperationType = GetString(obj, "operationType"),
                Xpath = GetString(obj, "xpath"),
                MatchedNodeIds = GetStringList(obj, "matchedNodeIds"),
                ModifiedNodeIds = GetStringList(obj, "modifiedNodeIds")
            };
            return edge;
        }

        private static string GetString(JsonObject obj, string key)
        {
            if (obj.TryGet(key, out JsonValue v) && v is JsonString s)
                return s.Value;
            return null;
        }

        private static List<string> GetStringList(JsonObject obj, string key)
        {
            var list = new List<string>();
            if (obj.TryGet(key, out JsonValue v) && v is JsonArray arr)
            {
                foreach (JsonValue item in arr.Items)
                    if (item is JsonString s)
                        list.Add(s.Value);
            }
            return list;
        }

        // Serialise to the exact PatchClassification.json schema Piece C expects.
        public JsonObject ToJson()
        {
            var root = new JsonObject();
            root.Add("version", new JsonNumber(1));

            var patchesArray = new JsonArray();
            foreach (PatchClassificationResult p in Patches)
            {
                var entry = new JsonObject();
                entry.Add("patchId", new JsonString(p.PatchId ?? string.Empty));
                entry.Add("sourceMod", new JsonString(p.SourceMod ?? string.Empty));
                entry.Add("classification", new JsonString(p.ClassificationString));

                var names = new JsonArray();
                foreach (string n in p.TargetDefNames)
                    names.Add(new JsonString(n));
                entry.Add("targetDefNames", names);

                entry.Add("predicate", p.Predicate == null ? (JsonValue)JsonNull.Instance : new JsonString(p.Predicate));
                patchesArray.Add(entry);
            }
            root.Add("patches", patchesArray);

            var index = new JsonObject();
            foreach (KeyValuePair<string, List<string>> pair in IdentityIndex)
            {
                var ids = new JsonArray();
                foreach (string id in pair.Value)
                    ids.Add(new JsonString(id));
                index.Add(pair.Key, ids);
            }
            root.Add("identityIndex", index);

            var summary = new JsonObject();
            summary.Add("identityCount", new JsonNumber(IdentityCount));
            summary.Add("wildcardCount", new JsonNumber(WildcardCount));

            var perMod = new JsonObject();
            foreach (KeyValuePair<string, ModBreakdown> pair in PerMod)
            {
                var modObj = new JsonObject();
                modObj.Add("identity", new JsonNumber(pair.Value.Identity));
                modObj.Add("wildcard", new JsonNumber(pair.Value.Wildcard));
                perMod.Add(pair.Key, modObj);
            }
            summary.Add("perMod", perMod);
            root.Add("summary", summary);

            return root;
        }
    }
}
