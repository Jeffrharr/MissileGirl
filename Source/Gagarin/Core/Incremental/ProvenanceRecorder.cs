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
using HarmonyLib;
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

        // Caches, per PatchOperation type, whether it declares a field literally named
        // "match" or "nomatch" (any accessibility, any declaring level in the type
        // hierarchy). Used by MaybeRecordUnresolvedGate (issue #40) to decide whether an
        // unrecognized type shares PatchOperationFindMod/Conditional's branch convention
        // without re-walking the type hierarchy on every Apply.
        private static readonly Dictionary<Type, bool> branchFieldCache =
            new Dictionary<Type, bool>();

        // Reflective access to PatchOperationFindMod's private fields (issue #40). mods
        // holds mod DISPLAY NAMES (not packageIds) tested via ModLister.HasActiveModWithName
        // -- confirmed by decompiling Verse.PatchOperationFindMod.ApplyWorker; match/nomatch
        // are the branch children RimWorld applies depending on that test's result.
        private static readonly AccessTools.FieldRef<PatchOperationFindMod, List<string>> FindModModsRef =
            AccessTools.FieldRefAccess<PatchOperationFindMod, List<string>>("mods");
        private static readonly AccessTools.FieldRef<PatchOperationFindMod, PatchOperation> FindModMatchRef =
            AccessTools.FieldRefAccess<PatchOperationFindMod, PatchOperation>("match");
        private static readonly AccessTools.FieldRef<PatchOperationFindMod, PatchOperation> FindModNomatchRef =
            AccessTools.FieldRefAccess<PatchOperationFindMod, PatchOperation>("nomatch");

        // The stack of currently-applying operations' ids (Apply hook pushes on entry, pops on exit).
        // RimWorld applies container children recursively, so the top of this stack is always the op
        // that is (transitively) applying the op now entering Apply. Used to attribute a DYNAMICALLY
        // GENERATED op — one a parent op's ApplyWorker news up and applies, so it was never in the
        // static index walk — to its enclosing op instead of the collapsed "unindexed#{type}" bucket.
        private static readonly Stack<string> applyStack = new Stack<string>();

        // Per-parent counter giving each parent's generated children stable, distinct suffixes
        // (parentId.generated[0], [1], ...). Application order within a load is deterministic, so the
        // ids are stable run-to-run for the same content.
        private static readonly Dictionary<string, int> generatedChildCounters =
            new Dictionary<string, int>();

        private static readonly Stopwatch overhead = new Stopwatch();

        // Per-phase breakdown of the total overhead, logged (not serialized) so we
        // can attribute the capture cost without churning the shared JSON schema.
        private static readonly Stopwatch registerSw = new Stopwatch();
        private static readonly Stopwatch recordSw = new Stopwatch();
        private static readonly Stopwatch serializeSw = new Stopwatch();

        // Number of KeyMatched calls (one per matched node). This is the hottest path in
        // the whole capture — the Select* hooks key every node every patch op matches, so
        // on a large modlist it fires billions of times (issue #31). We deliberately do NOT
        // wrap it in the per-call Stopwatch the other phases use: on mono-Linux Stopwatch
        // (QueryPerformanceCounter) costs enough per call that, multiplied by this volume,
        // the meter dominated the very recordMs it was measuring. A monotonic long counter
        // is effectively free and still surfaces the keying VOLUME — which is the real
        // driver of the super-linear cost — in the capture log for #31 re-measurement.
        private static long keyMatchedCount;

        public static bool Active => GagarinPrefs.CaptureProvenance && !Context.IsUsingCache;

        public static void Reset()
        {
            graph.Reset();
            patchIds.Clear();
            patchFieldCache.Clear();
            branchFieldCache.Clear();
            applyStack.Clear();
            generatedChildCounters.Clear();
            // pendingOperationGates is deliberately NOT cleared here -- see
            // ResetPendingOperationGates's header comment (issue #40 case 3): by the time
            // ApplyPatches_Patch.Prefix runs Reset(), ErrorCheckPatches() has already triggered
            // every mod's lazy LoadPatches() and populated pendingOperationGates for THIS load,
            // so clearing it here would discard entries before IndexOperationGate ever drains them.
            AssemblyOwnerLookup.Reset();
            overhead.Reset();
            registerSw.Reset();
            recordSw.Reset();
            serializeSw.Reset();
            keyMatchedCount = 0;
        }

        // Clears any MayRequire/MayRequireAnyOf gate RecordOperationGate stashed for a
        // constructed-but-never-Applied operation (an untaken FindMod/Conditional branch, or an op
        // whose Apply threw) in a PRIOR capture session, so it can never bleed into this one.
        //
        // Deliberately split out of Reset() (issue #40 case 3): Reset() runs from
        // ApplyPatches_Patch.Prefix, which is too LATE within a single cold load --
        // LoadedModManager.ErrorCheckPatches() (called earlier in the same LoadAllActiveMods, per
        // the decompiled method) is the first thing that touches ModContentPack.Patches for every
        // mod, lazily triggering ModContentPack.LoadPatches() -- which is exactly what fires
        // DirectXmlToObject_Patch's gate-capture hooks and populates pendingOperationGates for THIS
        // load. Clearing the dict at ApplyPatches-Prefix time discarded every entry ErrorCheckPatches
        // had just stashed moments earlier, before IndexOperationGate ever got a chance to drain them
        // -- silently zeroing every case-3 MayRequire-wrapper gate for every mod on every load.
        //
        // Called instead from TKeySystem_Parse_Patch's postfix (LoadedModManager_Patch.cs):
        // TKeySystem.Parse only runs when !hotReload (matching this capture's cold-load-only scope),
        // and it is the last thing LoadAllActiveMods does before ErrorCheckPatches -- so this always
        // runs exactly once per cold load, strictly before any mod's Patches can be lazily populated.
        public static void ResetPendingOperationGates()
        {
            if (GagarinPrefs.CaptureVerbose && pendingOperationGates.Count > 0)
                Logger.Debug($"GAGARIN: Discarding {pendingOperationGates.Count} " +
                    "never-applied MayRequire gate(s) from a prior capture session.");
            pendingOperationGates.Clear();
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
                string nodeId = graph.AddNode(
                    element?.Name ?? def.GetType().Name,
                    def.defName,
                    nameAttr,
                    asset?.mod?.PackageId,
                    asset?.FullFilePath,
                    parentName);

                // issue #86: if this def's .NET Type came from a mod's own C# assembly
                // (a custom Def type, not base-game), record that dependency. A later
                // removal of the providing mod makes the Type itself disappear, so RimWorld
                // can no longer construct this element in ANY mod's XML -- even one that
                // never changed and has no MayRequire. See AssemblyOwnerLookup and
                // ProvenanceGraph.AddTypeProvider. A node can have more than one candidate
                // provider (two mods sharing an identity-matching assembly via .NET's
                // LoadFrom binding) -- record all of them so TypeProviderGate can pass as
                // long as ANY one survives, not just whichever mod loaded last.
                List<string> providerPackageIds = AssemblyOwnerLookup.PackageIdsFor(def.GetType().Assembly);
                if (nodeId != null)
                {
                    foreach (string providerPackageId in providerPackageIds)
                        graph.AddTypeProvider(providerPackageId, nodeId);
                }
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
                // issue #81: resolve the node's real source file the same way RegisterNode does,
                // instead of unconditionally passing null. CombineIntoUnifiedXML_Patch stashes the
                // exact node->asset map RimWorld built while combining defs into Context.DefsXmlAssets
                // before ApplyPatches/ParseAndProcessXML run, so a genuinely raw-XML abstract base
                // (e.g. <ThingDef Name="BuildingBase" Abstract="True">) resolves a real SourceFile
                // here just like a concrete def does. A node absent from that map (e.g. one injected
                // by a patch, which never got its own assetlookup entry) still resolves to null,
                // preserving the "no SourceFile => patch-injected" signal CanRecompute's
                // unrecoverable-patch-injected check depends on.
                Context.DefsXmlAssets.TryGetValue(node, out LoadableXmlAsset asset);
                // defName is passed as null so the node id stays the abstract "{DefType}@{Name}"
                // shape even when an abstract base also declares a <defName>; mixing in the
                // defName would key it as a concrete "{DefType}/{defName}" node, which both
                // changes its identity (DefRecompute.IsConcrete keys off '/') and would let a
                // ParentName reference resolve to the wrong shape.
                graph.AddNode(element.Name, null, nameAttr, mod?.PackageId, asset?.FullFilePath, parentName);
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
            foreach (string pkg in SplitPackages(attrValue))
                graph.AddMayRequire(pkg, nodeId);
        }

        // Splits a MayRequire/MayRequireAnyOf attribute value into trimmed, non-empty
        // packageIds. The single parsing rule both AddPackages (doc-scan / P4) and
        // CollectPackages (per-operation gate / issue #40 case 3) build on, so a future
        // change to separator/trim/empty-filter rules only has one place to change.
        private static IEnumerable<string> SplitPackages(string attrValue)
        {
            if (string.IsNullOrEmpty(attrValue))
                yield break;
            foreach (string raw in attrValue.Split(PackageIdSeparators))
            {
                string pkg = raw.Trim();
                if (pkg.Length > 0)
                    yield return pkg;
            }
        }

        // Stashes the MayRequire/MayRequireAnyOf packageId(s) gating a just-constructed
        // PatchOperation, keyed by object identity (issue #40 case 3). Populated by
        // DirectXmlToObject_Patch's postfix on ObjectFromXml<PatchOperation>: RimWorld's
        // ListFromXml tests these attributes on the <li> BEFORE calling ObjectFromXml, so an
        // operation only reaches this call at all once its own gate has already passed --
        // meaning any node it goes on to match depends on that gating mod exactly as the P4
        // mayRequire index already models. Drained (and removed) by IndexOperationGate once
        // the operation's matched nodes are known.
        private static readonly Dictionary<PatchOperation, List<string>> pendingOperationGates =
            new Dictionary<PatchOperation, List<string>>();

        public static void RecordOperationGate(PatchOperation op, string mayRequire, string mayRequireAnyOf)
        {
            if (!Active || op == null)
                return;
            if (string.IsNullOrEmpty(mayRequire) && string.IsNullOrEmpty(mayRequireAnyOf))
                return;

            var packages = new List<string>();
            CollectPackages(mayRequire, packages);
            CollectPackages(mayRequireAnyOf, packages);
            if (packages.Count > 0)
            {
                pendingOperationGates[op] = packages;
                if (GagarinPrefs.CaptureVerbose)
                    Logger.Debug($"GAGARIN: Stashed MayRequire gate on {op.GetType().Name} " +
                        $"({string.Join(",", packages)}), pending={pendingOperationGates.Count}.");
            }
        }

        private static void CollectPackages(string attrValue, List<string> into)
        {
            into.AddRange(SplitPackages(attrValue));
        }

        // Drops a stashed gate for an operation whose Apply threw (PatchOperation_Patch's
        // Finalizer) without indexing anything -- the op failed, so nothing it would have
        // matched was actually applied. Without this, a throw leaves the pendingOperationGates
        // entry behind forever, since only IndexOperationGate (called from the Postfix, which
        // Harmony skips on a throw) ever removes it.
        public static void DiscardOperationGate(PatchOperation op)
        {
            if (op == null)
                return;
            if (pendingOperationGates.Remove(op) && GagarinPrefs.CaptureVerbose)
                Logger.Debug($"GAGARIN: Discarded MayRequire gate on {op.GetType().Name} -- Apply threw.");
        }

        // Drains a just-applied operation's own MayRequire gate (if RecordOperationGate
        // stashed one) into the shared mayRequire index, keyed by the nodes THIS operation
        // itself matched -- the <li Class="PatchOperationRemove" MayRequire="X"> case (issue
        // #40): the removed node's own patch edge already tracks the mod that ADDED the <li>,
        // but nothing previously recorded that the removal depends on X being active, so
        // removing X (with the target mod present) silently missed the dirty def. Called from
        // Apply's postfix for every operation; a cheap no-op when no gate was stashed.
        public static void IndexOperationGate(PatchOperation op, IEnumerable<string> matchedNodeIds)
        {
            if (!Active || op == null)
                return;
            if (!pendingOperationGates.TryGetValue(op, out List<string> packages))
                return;
            pendingOperationGates.Remove(op);

            // matchedNodeIds is only the op's OWN direct selection (its sink), which is
            // empty for container ops (PatchOperationSequence etc.) that never select
            // nodes themselves -- only their children do, each in a separately-pushed
            // sink layer. Walk the subtree too, so a gated container indexes everything
            // its children matched, not just its own (possibly empty) selection.
            var nodeIds = new HashSet<string>();
            if (matchedNodeIds != null)
                foreach (string nodeId in matchedNodeIds)
                    nodeIds.Add(nodeId);
            CollectMatchedNodeIds(op, nodeIds, new HashSet<PatchOperation>());

            if (nodeIds.Count == 0)
            {
                if (GagarinPrefs.CaptureVerbose)
                    Logger.Debug($"GAGARIN: MayRequire gate on {op.GetType().Name} " +
                        $"({string.Join(",", packages)}) matched no nodes (direct or nested) -- nothing indexed.");
                return;
            }

            foreach (string pkg in packages)
                foreach (string nodeId in nodeIds)
                    graph.AddMayRequire(pkg, nodeId);
        }

        // PatchOperationFindMod capture reader (issue #40). Called from the Apply hook's
        // postfix whenever the just-applied operation is a PatchOperationFindMod. Feeds
        // the SAME mayRequire index Seed 6 (MayRequire flips) already consumes, so no
        // DirtySetComputer change is needed: whichever branch (match/nomatch) actually ran
        // this load had its matched node ids already recorded by the ordinary Apply
        // postfix/RecordPatch path (FindMod's ApplyWorker calls that branch's Apply
        // synchronously before returning, so by the time THIS postfix runs the branch's own
        // capture is already complete) — we just need to look them up by the branch's
        // already-assigned patch id and union them into mayRequire under the resolved
        // packageId(s). See the file-header comment on FindModCapture for the full
        // root-cause rationale.
        //
        // RecomputeAllowlist gap (recompute-decline worklist): PatchOperationFindMod is not
        // PatchOperationPathed (no <xpath>, only <mods>), so the Postfix's ordinary
        // RecordPatch call never fires for it (see PatchOperation_Patch.cs's `is
        // PatchOperationPathed` early return) -- FindMod itself never got a PatchEdge, so its
        // branch children's BranchParentId lookup always missed and fell back as capture-gap,
        // even though FindMod's match/nomatch selection depends only on ModLister state (fixed
        // for the whole load), never on doc content -- unlike a real Conditional, it can never
        // re-select a different branch just because the sub-doc is smaller. RecordFindModEdge
        // below gives it a PatchEdge with an EMPTY matched-node set (there is no doc-content
        // read to record) so RecomputeAllowlist's conditionalReads treats it as a trivially
        // reproducible "conditional" (ReadSetInSubDoc treats an empty read set as always
        // in-subdoc) once IsConditional recognises the op kind.
        public static void RecordFindModEdge(PatchOperationFindMod findMod)
        {
            if (!Active || findMod == null)
                return;
            if (!patchIds.TryGetValue(findMod, out string patchId))
                return;
            string sourceMod = patchId.Contains("#") ? patchId.Substring(0, patchId.IndexOf('#')) : null;
            // FullName (namespace-qualified), not Name: RecomputeAllowlist's SafeLeafOps trusts
            // this string by identity, and a bare simple name could collide with an unrelated
            // mod's own class of the same name.
            graph.AddPatchEdge(patchId, sourceMod, findMod.GetType().FullName, null, null, null);
        }

        public static void IndexFindMod(PatchOperationFindMod findMod)
        {
            if (!Active || findMod == null)
                return;

            List<string> modNames = FindModModsRef(findMod);
            if (modNames == null || modNames.Count == 0)
                return;

            List<string> packageIds = FindModCapture.ResolvePackageIds(modNames, ModNameToPackageId);
            if (packageIds.Count == 0)
            {
                if (GagarinPrefs.CaptureVerbose)
                    Logger.Debug($"GAGARIN: FindMod names [{string.Join(",", modNames)}] " +
                        "resolved to zero packageIds -- nothing indexed.");
                return;
            }

            // Replay the exact test ApplyWorker just ran (Verse.PatchOperationFindMod:
            // ModLister.HasActiveModWithName(name), any match wins) to learn which branch
            // fired. We don't have ApplyWorker's local result handed to us, but the test is
            // deterministic and side-effect free, so re-running it here is exact and safe.
            bool anyActive = false;
            foreach (string name in modNames)
            {
                if (ModLister.HasActiveModWithName(name))
                {
                    anyActive = true;
                    break;
                }
            }

            PatchOperation branch = anyActive ? FindModMatchRef(findMod) : FindModNomatchRef(findMod);
            if (branch == null)
            {
                if (GagarinPrefs.CaptureVerbose)
                    Logger.Debug($"GAGARIN: FindMod [{string.Join(",", packageIds)}] took the " +
                        $"{(anyActive ? "match" : "nomatch")} branch, which is absent from the XML " +
                        "-- nothing indexed.");
                return; // e.g. nomatch omitted in XML -- no content depends on the gating mod on this branch
            }

            var nodeIds = new HashSet<string>();
            CollectMatchedNodeIds(branch, nodeIds, new HashSet<PatchOperation>());
            if (nodeIds.Count == 0)
            {
                if (GagarinPrefs.CaptureVerbose)
                    Logger.Debug($"GAGARIN: FindMod [{string.Join(",", packageIds)}] branch matched " +
                        "zero nodes (direct or nested) -- nothing indexed.");
                return;
            }

            foreach (string pkg in packageIds)
                foreach (string nodeId in nodeIds)
                    graph.AddMayRequire(pkg, nodeId);
        }

        // Resolves a mod's display Name (as ModLister.HasActiveModWithName compares it) to
        // its packageId via the currently running mods list, matching case-sensitively by
        // exact Name equality -- mirroring the decompiled ApplyWorker's own comparison so we
        // gate on precisely the same mod it does, no more and no less.
        private static string ModNameToPackageId(string displayName)
        {
            if (string.IsNullOrEmpty(displayName))
                return null;
            string packageId = null;
            int matches = 0;
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                if (mod == null || mod.Name != displayName)
                    continue;
                if (matches == 0)
                    packageId = mod.PackageId;
                matches++;
            }
            // Two active mods sharing a display Name is a real (if rare) modlist shape --
            // e.g. a Workshop copy and a manually-installed fork. ApplyWorker's own
            // ModLister.HasActiveModWithName test can't distinguish them either (any match
            // wins), so which one we attribute the gate to is inherently ambiguous; at least
            // make the ambiguity visible instead of silently picking the load-order-first one.
            if (matches > 1 && GagarinPrefs.CaptureVerbose)
                Logger.Debug($"GAGARIN: FindMod display name \"{displayName}\" matched " +
                    $"{matches} active mods; attributing its gate to \"{packageId}\" (first in load order).");
            return packageId;
        }

        // Walks a branch subtree (the op itself plus every nested child reachable via
        // GetChildPatches, e.g. a PatchOperationSequence's operations[i]) pulling each
        // already-indexed patch id's recorded matched node ids into "into". visited guards
        // against a shared/cyclic child re-walking (mirrors PatchIdWalker's first-visit-wins
        // guard); patch trees are finite in practice, but this keeps it provably safe.
        private static void CollectMatchedNodeIds(
            PatchOperation op, HashSet<string> into, HashSet<PatchOperation> visited)
        {
            if (op == null || !visited.Add(op))
                return;

            if (patchIds.TryGetValue(op, out string id))
                foreach (string nodeId in graph.GetMatchedNodeIds(id))
                    into.Add(nodeId);

            foreach ((string _, PatchOperation child) in GetChildPatches(op))
                CollectMatchedNodeIds(child, into, visited);
        }

        // Generic fallback for unrecognized branch constructs (issue #40, part 2). Called
        // from the Apply hook's postfix for every operation that is NOT a
        // PatchOperationFindMod. If its type carries the match/nomatch field convention but
        // isn't one of the types we have a dedicated reader for (FindMod here;
        // PatchOperationConditional via issue #25's RecomputeAllowlist, which already
        // consumes the generic .match/.nomatch ids the capture walk assigns it), we cannot
        // resolve WHICH nodes it depends on -- only flag that its owning mod has an
        // unbounded blast radius, mirroring riskyMods' precedent. Capture-only: no
        // DirtySetComputer seed consumes unresolvedGateMods yet (deferred follow-up).
        public static void MaybeRecordUnresolvedGate(PatchOperation patch)
        {
            if (!Active || patch == null)
                return;

            Type type = patch.GetType();
            if (!FindModCapture.NeedsGenericFallback(type.FullName, HasBranchFields(type)))
                return;

            if (!patchIds.TryGetValue(patch, out string id))
                return; // unindexed op -- no sourceMod to attribute this to
            string sourceMod = id.Contains("#") ? id.Substring(0, id.IndexOf('#')) : null;
            if (!string.IsNullOrEmpty(sourceMod))
                graph.AddUnresolvedGateMod(sourceMod);
        }

        // True if type (or a base type) declares an instance field literally named "match"
        // or "nomatch" -- the shared convention PatchOperationFindMod and
        // PatchOperationConditional both use for their branch children. Cached per type
        // since MaybeRecordUnresolvedGate runs on every Apply.
        private static bool HasBranchFields(Type type)
        {
            if (branchFieldCache.TryGetValue(type, out bool cached))
                return cached;

            bool has = HasField(type, "match") || HasField(type, "nomatch");
            branchFieldCache[type] = has;
            return has;
        }

        private static bool HasField(Type type, string name)
        {
            for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
                if (t.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance) != null)
                    return true;
            return false;
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

            // HOT PATH — once per matched node, billions of times at scale (issue #31).
            // No per-call Stopwatch here on purpose (see keyMatchedCount): the timer was
            // costing more than the work it timed. KeyForNode is memoized, so the real
            // remaining cost is the call volume, which keyMatchedCount makes visible.
            keyMatchedCount++;
            return graph.KeyForNode(node);
        }

        // Called from the Apply hook's PREFIX, as each PatchOperation.Apply begins. Pushes the op's
        // id so nested/recursive applies know their enclosing op. The key work: if the op is NOT in
        // the static index (patchIds), it was generated dynamically during the enclosing op's
        // ApplyWorker — attribute it to that enclosing op as "{parentId}.generated[N]" and register
        // it, so RecordPatch (which looks up patchIds) gives it the enclosing op's real sourceMod and
        // a distinct, hierarchical id instead of the collapsed "unindexed#{type}" bucket. That makes
        // invisible-op risk attributable PER MOD. Must be balanced with ExitApply (the hook calls
        // both only when Active, so the stack stays balanced).
        public static void EnterApply(PatchOperation patch)
        {
            if (!Active || patch == null)
                return;
            overhead.Start();
            try
            {
                if (patchIds.TryGetValue(patch, out string id))
                {
                    applyStack.Push(id);
                    return;
                }
                // Not indexed => generated dynamically by the op currently applying (stack top).
                string parent = applyStack.Count > 0 ? applyStack.Peek() : null;
                if (parent != null)
                {
                    int n = generatedChildCounters.TryGetValue(parent, out int c) ? c : 0;
                    generatedChildCounters[parent] = n + 1;
                    id = parent + ".generated[" + n + "]";
                    patchIds[patch] = id; // so RecordPatch resolves it (+ recovers sourceMod from the prefix)
                    applyStack.Push(id);
                }
                else
                {
                    // A top-level op missing from the index (no enclosing op to attribute to). Rare;
                    // fall back to the unindexed bucket exactly as before so RecordPatch is consistent.
                    applyStack.Push("unindexed#" + patch.GetType().Name);
                }
            }
            finally
            {
                overhead.Stop();
            }
        }

        // Called from the Apply hook's POSTFIX, as each PatchOperation.Apply returns. Pops the id
        // pushed by EnterApply. Tolerant of an empty stack (never throws into the patch phase).
        public static void ExitApply()
        {
            if (!Active)
                return;
            if (applyStack.Count > 0)
                applyStack.Pop();
        }

        // Recovers ownership for def nodes an Add-shaped operation creates fresh (issue: the
        // RBM_UnguligradeLegs live gap, 2026-07-09 EDGE_CASES_LOOP_PROGRESS.md). RimWorld's
        // ParseAndProcessXML keys `loadingAsset` by original-file-node identity, so a
        // brand-new top-level def a patch splices in gets RegisterNode called with a null
        // asset -- and the operation's OWN patch edge records only the xpath TARGET it
        // selected (the insertion anchor), never the new child's id, so neither RegisterNode
        // nor the ordinary patch-edge data can attribute it. We recover it here, directly:
        // called from the Apply postfix for a successful Add-shaped op with the raw target
        // nodes it selected (still attached to the live document, BEFORE any DefFromNodeNew
        // call runs over the combined doc), walk each target's NEWLY-APPENDED child
        // elements and record (id, sourceMod) into the graph's patchInjectedOwners index.
        // `priorChildCounts` (parallel to `targets`) is each target's ChildNodes.Count
        // snapshotted at SELECTION time, i.e. before this op appended anything -- required
        // because a broad target (e.g. an Add anchored on the `Defs` root; TestMod_GenOp's
        // doc-path-fallback fixture does this deliberately) can already have thousands of
        // pre-existing children. Without the snapshot every one of those pre-existing
        // children -- most belonging to unrelated mods -- would be misattributed to this
        // op's mod, clobbering the fallback for any of THEM whose own SourceMod happened to
        // be empty (this was the actual live RBM_UnguligradeLegs bug: an unrelated
        // `/Defs`-root Add's mod became every def's fallback owner). This index is
        // consulted only as a fallback when a node's real SourceMod came back empty; a node
        // with real attribution already has a correct SourceMod that takes precedence over
        // this index everywhere it's read.
        //
        // `prepend` reflects the op's own <order> field: the default Append behaviour lands
        // new children AFTER the prior ones, but <order>Prepend</order> lands them BEFORE,
        // pushing the prior children to the tail. See PatchInjectedChildSelector for the
        // index math for each case.
        public static void RecordAddedChildren(
            PatchOperation patch, IList<XmlNode> targets, IList<int> priorChildCounts, bool prepend = false)
        {
            if (!Active || patch == null || targets == null)
                return;
            if (!patchIds.TryGetValue(patch, out string patchId))
                return;
            string sourceMod = patchId.Contains("#") ? patchId.Substring(0, patchId.IndexOf('#')) : null;
            if (string.IsNullOrEmpty(sourceMod))
                return;

            for (int t = 0; t < targets.Count; t++)
            {
                XmlNode target = targets[t];
                if (target == null)
                    continue;
                int priorCount = priorChildCounts != null && t < priorChildCounts.Count
                    ? priorChildCounts[t] : 0;
                foreach (XmlElement child in PatchInjectedChildSelector.SelectNewlyAdded(target, priorCount, prepend))
                {
                    string id = graph.KeyForNode(child);
                    if (id != null)
                        graph.AddPatchInjectedOwner(id, sourceMod);
                }
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

                // FullName (namespace-qualified), not Name: RecomputeAllowlist's SafeLeafOps
                // trusts this string by identity, and a bare simple name could collide with an
                // unrelated mod's own class of the same name.
                graph.AddPatchEdge(patchId, sourceMod, patch.GetType().FullName, xpath,
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
                    $"serializeMs={serializeSw.ElapsedMilliseconds} " +
                    // keyings is the KeyMatched call volume (per matched node) — the driver
                    // of the super-linear capture cost (#31), now counted instead of timed.
                    $"keyings={keyMatchedCount}]");
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
