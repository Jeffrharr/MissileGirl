// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// ProvenanceRecorder.cs (Piece A — provenance capture)
//
// Contains: the static ProvenanceRecorder — the RimWorld-facing instrumentation
// layer that indexes patches, registers def nodes, records patch matches, and
// writes DependencyGraph.json to the cache folder.
//
// Used for: driving ProvenanceGraph from inside a single cold load (called by
// the DirectXmlLoader/LoadedModManager/PatchOperation patches), and measuring
// the capture overhead so we know the instrumentation's cost.
//
// Why: it is pure instrumentation, gated behind GagarinPrefs.CaptureProvenance
// (dev-only, default OFF), that never alters cache validity or load behaviour.
// It exists to produce the dependency graph the incremental cache will later use
// to recompute only the defs affected by a change instead of rebuilding all.

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

        // Per-phase breakdown of the total overhead, logged (not serialized) so we
        // can attribute the capture cost without churning the shared JSON schema.
        private static readonly Stopwatch registerSw = new Stopwatch();
        private static readonly Stopwatch recordSw = new Stopwatch();
        private static readonly Stopwatch serializeSw = new Stopwatch();

        public static bool Active => GagarinPrefs.CaptureProvenance && !Context.IsUsingCache;

        public static void Reset()
        {
            graph.Reset();
            patchIds.Clear();
            overhead.Reset();
            registerSw.Reset();
            recordSw.Reset();
            serializeSw.Reset();
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
            registerSw.Start();
            try
            {
                XmlElement element = node as XmlElement;
                string parentName = element?.GetAttribute("ParentName");
                // A concrete def can also be an inheritance template (Name attribute);
                // pass it so children referencing it by ParentName resolve here.
                string nameAttr = element?.GetAttribute("Name");
                graph.AddNode(
                    def.GetType().Name,
                    def.defName,
                    nameAttr,
                    asset?.mod?.PackageId,
                    asset?.FullFilePath,
                    parentName);
            }
            finally
            {
                registerSw.Stop();
                overhead.Stop();
            }
        }

        // Registers an abstract / Name-based parent def (e.g. <ThingDef Name="BuildingBase"
        // Abstract="True">). These never become Def objects, so RegisterNode never sees
        // them; without this, the ~most ParentName references resolve to nothing and
        // inheritance fan-out breaks. Driven by a postfix on XmlInheritance.TryRegister,
        // which fires for every node carrying a Name or ParentName. Concrete defs (those
        // with a defName) are skipped here — RegisterNode already handles them, including
        // their Name attribute.
        public static void RegisterAbstract(XmlNode node, ModContentPack mod)
        {
            if (!Active)
                return;

            XmlElement element = node as XmlElement;
            if (element == null)
                return;
            string nameAttr = element.GetAttribute("Name");
            if (string.IsNullOrEmpty(nameAttr))
                return; // no Name => not an inheritance base we need to register here
            if (!string.IsNullOrEmpty(element["defName"]?.InnerText))
                return; // concrete def: RegisterNode owns it

            overhead.Start();
            registerSw.Start();
            try
            {
                string parentName = element.GetAttribute("ParentName");
                graph.AddNode(element.Name, null, nameAttr, mod?.PackageId, null, parentName);
            }
            finally
            {
                registerSw.Stop();
                overhead.Stop();
            }
        }

        // Keys a matched node to its stable id at selection time — called from the XML
        // selection hooks while the node is still attached to the document. Returns null
        // when capture is inactive. Keying here, rather than in the Apply postfix, is what
        // keeps Replace/Remove matches attributed to their def: those operations detach
        // the matched node before the postfix runs.
        public static string KeyMatched(XmlNode node)
        {
            if (!Active || node == null)
                return null;

            overhead.Start();
            recordSw.Start();
            try
            {
                return graph.KeyForNode(node);
            }
            finally
            {
                recordSw.Stop();
                overhead.Stop();
            }
        }

        // Records matched/modified node ids for a single PatchOperation.Apply. The ids
        // were computed at selection time (see KeyMatched); modifiedNodeIds are the
        // matched ids when the operation succeeded, else null. Only operations carrying
        // an xpath produce an edge.
        public static void RecordPatch(PatchOperation patch, string xpath,
            IEnumerable<string> matchedNodeIds, IEnumerable<string> modifiedNodeIds)
        {
            if (!Active || patch == null)
                return;

            overhead.Start();
            recordSw.Start();
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
                    matchedNodeIds, modifiedNodeIds);
            }
            finally
            {
                recordSw.Stop();
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
            serializeSw.Start();
            try
            {
                // Serializing is part of the capture cost, so it happens inside the
                // overhead window; we stop the clock before writing to disk so the
                // metric reflects in-memory work only.
                int activeModCount = Context.RunningMods?.Count ?? 0;
                // registerMs/recordMs are complete by now (load has finished); serializeMs
                // is logged separately since it is still being measured here.
                string json = graph.Serialize(overhead.ElapsedMilliseconds,
                    registerSw.ElapsedMilliseconds, recordSw.ElapsedMilliseconds, activeModCount);
                serializeSw.Stop();
                overhead.Stop();

                if (!Directory.Exists(GagarinEnvironmentInfo.CacheFolderPath))
                    Directory.CreateDirectory(GagarinEnvironmentInfo.CacheFolderPath);
                string path = Path.Combine(GagarinEnvironmentInfo.CacheFolderPath, DependencyGraphFileName);
                File.WriteAllText(path, json);

                int edges = graph.InheritanceEdgeCount;
                int resolved = graph.InheritanceResolvedCount;
                float resolvedPct = edges > 0 ? 100f * resolved / edges : 100f;
                Log.Warning($"GAGARIN: <color=white>Provenance captured</color> " +
                    $"mods={activeModCount} nodes={graph.NodeCount} (abstract={graph.AbstractNodeCount}) " +
                    $"patchEdges={graph.PatchEdgeCount} " +
                    $"inheritanceEdges={edges} (resolved={resolved}, {resolvedPct:F1}%) " +
                    $"docPathFallbacks={graph.DocumentPathFallbackCount} " +
                    $"bytes={Encoding.UTF8.GetByteCount(json)} overheadMs={overhead.ElapsedMilliseconds} " +
                    $"[registerMs={registerSw.ElapsedMilliseconds} " +
                    $"recordMs={recordSw.ElapsedMilliseconds} " +
                    $"serializeMs={serializeSw.ElapsedMilliseconds}]");
            }
            catch (Exception er)
            {
                if (serializeSw.IsRunning)
                    serializeSw.Stop();
                if (overhead.IsRunning)
                    overhead.Stop();
                Logger.Debug("GAGARIN: Failed to write provenance graph", exception: er);
            }
        }
    }
}
