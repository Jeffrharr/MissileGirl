// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;
using MissileGirl;
using Verse;

namespace Gagarin
{
    // Piece A of the incremental-cache prototype: capture the dependency graph
    // (nodes, patch edges, inheritance edges) during a single cold load so that
    // future work can recompute only the defs affected by a change. This is pure
    // instrumentation; it never alters cache validity or load behaviour and is
    // gated behind GagarinPrefs.CaptureProvenance (dev-only, default OFF). The
    // graph data model and serialization live in the dependency-free
    // ProvenanceGraph so they can be unit-tested offline.
    public static class ProvenanceRecorder
    {
        private const string DependencyGraphFileName = "DependencyGraph.json";

        private static readonly ProvenanceGraph graph = new ProvenanceGraph();

        // Maps a top-level PatchOperation instance to the deterministic patchId
        // "{sourceMod}#{index}" assigned when ApplyPatches enumerates them.
        private static readonly Dictionary<PatchOperation, string> patchIds =
            new Dictionary<PatchOperation, string>();

        private static readonly Stopwatch overhead = new Stopwatch();

        public static bool Active => GagarinPrefs.CaptureProvenance && !Context.IsUsingCache;

        public static void Reset()
        {
            graph.Reset();
            patchIds.Clear();
            overhead.Reset();
        }

        // Assigns deterministic patchIds to every top-level PatchOperation in
        // active-mod load order, matching how ApplyPatches enumerates them.
        public static void IndexPatches(IEnumerable<ModContentPack> mods)
        {
            if (!Active)
                return;

            overhead.Start();
            try
            {
                patchIds.Clear();
                foreach (ModContentPack mod in mods)
                {
                    if (mod?.Patches == null)
                        continue;
                    string sourceMod = mod.PackageId ?? mod.Name ?? "unknown";
                    int index = 0;
                    foreach (PatchOperation patch in mod.Patches)
                    {
                        if (patch != null && !patchIds.ContainsKey(patch))
                            patchIds[patch] = $"{sourceMod}#{index}";
                        index++;
                    }
                }
            }
            finally
            {
                overhead.Stop();
            }
        }

        // Records one def node. node carries the raw XML element (used for the
        // ParentName attribute); asset gives the owning mod and source file.
        public static void RegisterNode(Def def, XmlNode node, LoadableXmlAsset asset)
        {
            if (!Active || def == null)
                return;

            overhead.Start();
            try
            {
                string parentName = (node as XmlElement)?.GetAttribute("ParentName");
                graph.AddNode(
                    def.GetType().Name,
                    def.defName,
                    asset?.mod?.PackageId,
                    asset?.FullFilePath,
                    parentName);
            }
            finally
            {
                overhead.Stop();
            }
        }

        // Records matched/modified nodes for a single PatchOperation.Apply.
        // matchedNodes is the XPath selection before mutation; modifiedNodes the
        // nodes touched. Only operations carrying an xpath produce an edge.
        public static void RecordPatch(PatchOperation patch, string xpath,
            IEnumerable<XmlNode> matchedNodes, IEnumerable<XmlNode> modifiedNodes)
        {
            if (!Active || patch == null)
                return;

            overhead.Start();
            try
            {
                if (!patchIds.TryGetValue(patch, out string patchId))
                {
                    // Child of a sequence, or a patch we never indexed: synthesise
                    // a stable id from the operation type so the edge is still
                    // attributable.
                    patchId = $"unindexed#{patch.GetType().Name}";
                }

                string sourceMod = patchId.Contains("#")
                    ? patchId.Substring(0, patchId.IndexOf('#'))
                    : null;

                graph.AddPatchEdge(patchId, sourceMod, patch.GetType().Name, xpath,
                    matchedNodes, modifiedNodes);
            }
            finally
            {
                overhead.Stop();
            }
        }

        // Serializes the accumulated graph to DependencyGraph.json in the cache
        // folder. No-op if capture is inactive.
        public static void Save()
        {
            if (!Active)
                return;

            overhead.Start();
            try
            {
                // Serializing is part of the capture cost, so it happens inside the
                // overhead window; we stop the clock before writing to disk so the
                // metric reflects in-memory work only.
                string json = graph.Serialize(overhead.ElapsedMilliseconds);
                overhead.Stop();

                if (!Directory.Exists(GagarinEnvironmentInfo.CacheFolderPath))
                    Directory.CreateDirectory(GagarinEnvironmentInfo.CacheFolderPath);
                string path = Path.Combine(GagarinEnvironmentInfo.CacheFolderPath, DependencyGraphFileName);
                File.WriteAllText(path, json);

                Log.Warning($"GAGARIN: <color=white>Provenance captured</color> " +
                    $"nodes={graph.NodeCount} patchEdges={graph.PatchEdgeCount} " +
                    $"inheritanceEdges={graph.InheritanceEdgeCount} " +
                    $"bytes={Encoding.UTF8.GetByteCount(json)} overheadMs={overhead.ElapsedMilliseconds}");
            }
            catch (Exception er)
            {
                if (overhead.IsRunning)
                    overhead.Stop();
                Logger.Debug("GAGARIN: Failed to write provenance graph", exception: er);
            }
        }
    }
}
