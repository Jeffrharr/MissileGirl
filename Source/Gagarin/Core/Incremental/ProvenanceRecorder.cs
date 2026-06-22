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
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
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

        // Maps a PatchOperation instance to its deterministic patchId. Top-level
        // operations get "{sourceMod}#{index}" (matching how ApplyPatches
        // enumerates them); nested operations (the children of containers such as
        // PatchOperationSequence / PatchOperationConditional / PatchOperationFindMod
        // that RimWorld applies recursively) get a hierarchical suffix appended to
        // their owning top-level id, e.g. "mod#3.operations[2]" or "mod#3.match".
        // The suffix never contains '#', so RecordPatch can still split on the
        // first '#' to recover sourceMod for every operation.
        private static readonly Dictionary<PatchOperation, string> patchIds =
            new Dictionary<PatchOperation, string>();

        // Caches the deterministically-ordered PatchOperation-typed reflection
        // surface of each operation type, so we don't re-sort fields per instance.
        private static readonly Dictionary<Type, FieldInfo[]> patchFieldCache =
            new Dictionary<Type, FieldInfo[]>();

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
            patchFieldCache.Clear();
            overhead.Reset();
            registerSw.Reset();
            recordSw.Reset();
            serializeSw.Reset();
        }

        // Assigns deterministic patchIds to every PatchOperation in active-mod
        // load order. Each mod's top-level operations get "{sourceMod}#{index}"
        // (matching how ApplyPatches enumerates them); their nested children get a
        // hierarchical id derived from the top-level one. RimWorld applies the
        // children of containers (PatchOperationSequence, PatchOperationConditional,
        // PatchOperationFindMod, and any custom container) recursively, so without
        // indexing them every nested op falls back to a single shared
        // "unindexed#{type}" bucket in RecordPatch — collapsing distinct ops and
        // losing their real source mod.
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
                        if (patch != null)
                        {
                            // Walk the whole subtree rooted at this top-level op,
                            // assigning a stable hierarchical id to it and every
                            // nested operation reachable through its fields.
                            PatchIdWalker.AssignIds(
                                patch, $"{sourceMod}#{index}", GetChildPatches, patchIds);
                        }
                        index++;
                    }
                }
            }
            finally
            {
                overhead.Stop();
            }
        }

        // The RimWorld-specific half of the recursion: reflects over a
        // PatchOperation's fields and yields its (label, child) pairs for the pure
        // PatchIdWalker. A field whose value is a PatchOperation yields one pair
        // labelled ".{fieldName}" (e.g. ".match"); a field whose value is an
        // IEnumerable of PatchOperation yields one pair per element labelled
        // ".{fieldName}[{i}]" (e.g. ".operations[2]"). Labels never contain '#', so
        // RecordPatch's split on the first '#' still recovers sourceMod.
        //
        // This is validated in-game rather than by unit test (it needs real
        // PatchOperation subclasses); the recursion/cycle/labelling logic it feeds
        // is covered by PatchIdWalker's offline tests.
        // internal so the dirty-set diagnostic (M2a) can reuse the exact same child
        // enumeration to re-derive a changed mod's CURRENT patch ids and pair them to the
        // baseline graph edges by id.
        internal static IEnumerable<(string label, PatchOperation child)> GetChildPatches(
            PatchOperation op)
        {
            foreach (FieldInfo field in GetPatchFields(op.GetType()))
            {
                object value = field.GetValue(op);
                if (value == null)
                    continue;

                if (value is PatchOperation child)
                {
                    yield return ($".{field.Name}", child);
                }
                else if (value is IEnumerable enumerable)
                {
                    // Index by position so reordering a list changes ids predictably
                    // and two sibling ops in the same list never collide.
                    int i = 0;
                    foreach (object item in enumerable)
                    {
                        if (item is PatchOperation itemOp)
                            yield return ($".{field.Name}[{i}]", itemOp);
                        i++;
                    }
                }
            }
        }

        // Returns the fields of a PatchOperation type that can hold nested
        // operations (a PatchOperation or an enumerable of them), in a
        // deterministic order. GetFields' order is not guaranteed across runs, so
        // we sort by (declaring depth, MetadataToken) to keep ids stable. Results
        // are cached per type. Value/string/primitive fields are excluded up front
        // so the per-instance hot path only touches relevant fields.
        private static FieldInfo[] GetPatchFields(Type type)
        {
            if (patchFieldCache.TryGetValue(type, out FieldInfo[] cached))
                return cached;

            var fields = new List<FieldInfo>();
            // Walk the type hierarchy so private fields declared on base
            // PatchOperation classes are included.
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            {
                fields.AddRange(t.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
                    | BindingFlags.DeclaredOnly));
            }

            FieldInfo[] result = fields
                .Where(CanHoldPatchOperation)
                // Deterministic, stable order: base-declared fields first (shallower
                // declaring depth from object), then by metadata token within a
                // type. This makes the generated ids identical across runs.
                .OrderBy(DeclaringDepth)
                .ThenBy(f => f.MetadataToken)
                .ToArray();

            patchFieldCache[type] = result;
            return result;
        }

        // True if a field could reference nested operations: its type is a
        // PatchOperation, or a non-string IEnumerable that might contain them.
        // Value types and strings can never hold operations and are skipped.
        private static bool CanHoldPatchOperation(FieldInfo field)
        {
            Type ft = field.FieldType;
            if (typeof(PatchOperation).IsAssignableFrom(ft))
                return true;
            if (ft == typeof(string) || ft.IsValueType)
                return false;
            return typeof(IEnumerable).IsAssignableFrom(ft);
        }

        // Distance of a field's declaring type from object, used only to order
        // base-class fields ahead of derived-class fields deterministically.
        private static int DeclaringDepth(FieldInfo field)
        {
            int depth = 0;
            for (Type t = field.DeclaringType; t != null; t = t.BaseType)
                depth++;
            return depth;
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
                // Key by the XML ELEMENT name, not def.GetType().Name. These diverge for
                // two real cases: a fully-namespaced custom def element
                // (<VFEProps.PropDef> -> element name "VFEProps.PropDef" vs type name
                // "PropDef") and a Class-attributed def (<ThingDef Class="Mod.SubDef">
                // -> element "ThingDef" vs type "SubDef"). Every consumer of the graph
                // -- KeyForNode, RegisterAbstract, DefRecompute, the splice and the gate
                // -- keys nodes by the element name, so using the simple type name here
                // filed those defs under a key nobody looks up: they were captured but
                // never matchable, so no seed could dirty them (the bulk of the P1
                // "un-captured def types" gate misses). Fall back to the type name only
                // if node somehow isn't an element (a def always has an element here).
                graph.AddNode(
                    element?.Name ?? def.GetType().Name,
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
        // which fires for every node carrying a Name or ParentName.
        //
        // Ownership split with RegisterNode (the defName guard): a node that declares a
        // <defName> normally becomes a real Def, so RegisterNode owns it (including any Name
        // attribute it also carries as a concrete template). BUT an *abstract* def can ALSO
        // declare a <defName> (e.g. <TerrainDef Name="MF_VoidTerrainBase" Abstract="True">
        // <defName>MF_VoidTerrainBase</defName>). Such a node never produces a Def — so
        // RegisterNode never sees it either — and the old "has defName => skip" rule dropped
        // it entirely, severing every chain that passed through it (concrete grandchild →
        // abstract-with-defName base → real base). We therefore skip on defName ONLY when the
        // element is NOT Abstract="True"; an abstract node is registered here regardless of
        // its defName so its identity stays the abstract "{DefType}@{Name}" shape. The strict
        // Abstract="True" guard keeps a genuine concrete template (defName + Name, not
        // abstract) owned by RegisterNode, so the two paths never double-register one node.
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

            // RimWorld marks abstract bases with the Abstract="True" attribute on the def
            // element (not an <Abstract> child element); treat "True"/"true" as abstract.
            string abstractAttr = element.GetAttribute("Abstract");
            bool isAbstract = string.Equals(abstractAttr, "True", System.StringComparison.OrdinalIgnoreCase);

            // A concrete (non-abstract) def with a defName is owned by RegisterNode. An
            // abstract def is owned here even if it also declares a defName, because it never
            // becomes a Def and RegisterNode would never see it.
            if (!isAbstract && !string.IsNullOrEmpty(element["defName"]?.InnerText))
                return;

            overhead.Start();
            registerSw.Start();
            try
            {
                string parentName = element.GetAttribute("ParentName");
                // defName is passed as null so the node id stays the abstract "{DefType}@{Name}"
                // shape even when an abstract base also declares a <defName>; mixing in the
                // defName would key it as a concrete "{DefType}/{defName}" node, which both
                // changes its identity (DefRecompute.IsConcrete keys off '/') and would let a
                // ParentName reference resolve to the wrong shape.
                graph.AddNode(element.Name, null, nameAttr, mod?.PackageId, null, parentName);
            }
            finally
            {
                registerSw.Stop();
                overhead.Stop();
            }
        }

        // Scans the fully-patched document for MayRequire / MayRequireAnyOf attributes and
        // indexes each gated node under its owning def, keyed by every packageId it names
        // (P4). Called once from the ApplyPatches postfix, where the combined+patched doc is
        // complete but def objects have not yet been parsed — so the attributes are still
        // present (RimWorld strips MayRequire-failed nodes later, at DefFromNode time). This
        // captures the dependencies of BOTH inline def content (<ThingStyleDef MayRequire=...>)
        // and patch-injected content (<li MayRequire=...> an Add operation spliced in), because
        // after patching they are indistinguishable nodes in one tree. The index is what makes
        // a mod add/remove dirty the affected defs even though their own files never changed.
        public static void IndexMayRequire(XmlDocument patchedDoc)
        {
            if (!Active || patchedDoc?.DocumentElement == null)
                return;

            overhead.Start();
            recordSw.Start();
            try
            {
                ScanForMayRequire(patchedDoc.DocumentElement);
            }
            finally
            {
                recordSw.Stop();
                overhead.Stop();
            }
        }

        // The two attribute names RimWorld recognises for conditional inclusion. MayRequire
        // takes a single packageId; MayRequireAnyOf takes a comma-separated list. We treat
        // both as a list and index the owning def under each named package — over-dirtying
        // is superset-safe, under-dirtying is the silent-staleness bug we are closing.
        private static readonly char[] PackageIdSeparators = { ',', ';' };

        private static void ScanForMayRequire(XmlNode node)
        {
            if (node is XmlElement element)
            {
                string mayRequire = element.GetAttribute("MayRequire");
                string mayRequireAnyOf = element.GetAttribute("MayRequireAnyOf");
                if (!string.IsNullOrEmpty(mayRequire) || !string.IsNullOrEmpty(mayRequireAnyOf))
                {
                    // Resolve the owning def only once we know the node is gated. KeyForNode
                    // walks up to the nearest <Defs> child and keys it the same way every
                    // other consumer does, so the indexed id matches the dirty-set/gate ids.
                    string nodeId = graph.KeyForNode(element);
                    if (nodeId != null)
                    {
                        AddPackages(mayRequire, nodeId);
                        AddPackages(mayRequireAnyOf, nodeId);
                    }
                }
            }

            for (XmlNode child = node.FirstChild; child != null; child = child.NextSibling)
                ScanForMayRequire(child);
        }

        private static void AddPackages(string attrValue, string nodeId)
        {
            if (string.IsNullOrEmpty(attrValue))
                return;
            foreach (string raw in attrValue.Split(PackageIdSeparators))
            {
                string pkg = raw.Trim();
                if (pkg.Length > 0)
                    graph.AddMayRequire(pkg, nodeId);
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
                    $"mayRequire={graph.MayRequireEdgeCount} (pkgs={graph.MayRequirePackageCount}) " +
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
