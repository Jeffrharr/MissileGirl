// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DefRecompute.cs (Piece D — Milestone 2b-2b: real-engine recompute over a sub-doc)
//
// Contains: the engine-touching recompute — given the dirty set (and the CONTEXT set
// SubDocExpander derived from it), rebuild each dirty CONCRETE def's resolved XML the way a
// full load would, but over a tiny sub-document instead of the whole ~28k-def database. It
// drives the REAL pipeline: the actual PatchOperation.Apply of every running mod (same loop as
// LoadedModManager.ApplyPatches) over a <Defs> sub-doc, then the real XmlInheritance resolution,
// then the exact same post-resolution massaging CachedDefHelper.Save does — so the output is
// byte-identical to a full rebuild's cache entry. UnifiedCacheSplice merges these into the prior
// cache; the gate proves the result matches a full rebuild over every def.
//
// Why the CONTEXT set: a PatchOperationSequence aborts on its first child whose xpath finds no
// match. If only one def from a bundle is dirty, the sequence's other child ops target sibling
// defs absent from a dirty-only sub-doc, so the sequence aborts early and the dirty def keeps an
// un-patched value (the 12-mismatch dead-end). SubDocExpander pulls those siblings in as context
// so the sequence finds all its targets and runs to completion. Context defs are present ONLY to
// keep sequences alive — their resolved values are discarded (step 6), because the baseline cache
// already holds their correct values; the splice reuses those verbatim.
//
// Why it is RimWorld-coupled (not offline-tested): faithfulness REQUIRES the real engine, so this
// can only be validated in-game via the gate. The pure halves it feeds (SubDocExpander, splice,
// diff) are offline-tested; this is the part the in-game zero-diff gate exists to verify.
//
// HAZARD — XmlInheritance is a global static. We Clear() it, register only our sub-doc nodes,
// Resolve(), extract, then Clear() again. This runs at end of load (after CachedDefHelper.Save
// has already consumed the live load's inheritance state), so clobbering it is expected to be
// safe; the gate / a game error would reveal otherwise. It must never run concurrently with the
// live load's own resolution.
//
// Known fidelity limits (the gate quantifies them): a patch whose match/effect depends on defs
// NOT in the sub-doc (positional xpath, cross-def reads, FindMod/Conditional scoping) can still
// differ; candidate bodies are RAW, so a match that only appears after an earlier patch mutates
// an unrelated def is missed. The changed-mod container-op case is excluded up front by
// SubDocExpander's full-rebuild fallback. Strategy is to widen coverage until the gate is
// zero-diff.

using System;
using System.Collections.Generic;
using System.Xml;
using MissileGirl;
using RimWorld;
using Verse;

namespace Gagarin
{
    public static class DefRecompute
    {
        // Recompute resolved XML (by def id) for every dirty CONCRETE def, via the real engine.
        // contextIds are sibling defs SubDocExpander pulled in so PatchOperationSequences don't
        // abort on absent targets; they populate the sub-doc but their resolved values are
        // discarded (the baseline cache already holds them). removedConcreteIds = dirty concrete
        // ids whose raw body no longer exists (deleted defs), for the splice to drop. Abstract
        // (@Name) dirty ids are never cache items themselves but are pulled in as inheritance
        // ancestors when a dirty concrete def needs them.
        public static Dictionary<string, string> Recompute(
            ICollection<string> dirtyIds, ICollection<string> contextIds,
            DependencyGraphData graph, ICollection<string> changedModIds,
            out List<string> removedConcreteIds,
            out List<string> ancestorIdsFromRawXml)
        {
            removedConcreteIds = new List<string>();
            // Ids pulled into the sub-doc ONLY via AddAncestorsFromRawXml (i.e. present in `needed`
            // but never a dirty/context id themselves). Reported so a consumer can tell which sub-doc
            // members came from the current-load raw-XML walk rather than dirty/context directly --
            // see AddAncestorsFromRawXml's comment for why that source matters (it can diverge from
            // RecomputeAllowlist.CanRecompute's prior-graph-based ancestor set).
            ancestorIdsFromRawXml = new List<string>();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (dirtyIds == null || dirtyIds.Count == 0)
                return result;

            // 1. Index every raw def body (concrete + abstract) by id, plus Name -> id (for
            //    walking ParentName chains) and node -> owning mod (for XmlInheritance).
            var rawById = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            var idByName = new Dictionary<string, string>(StringComparer.Ordinal);
            var modByRaw = new Dictionary<XmlNode, ModContentPack>();
            BuildRawIndex(rawById, idByName, modByRaw);

            // 2. Collect the nodes the sub-doc needs: every present dirty AND context def, plus
            //    each one's transitive inheritance ancestors. A dirty concrete id absent from raw
            //    is a deletion; a context id absent from raw is simply skipped (it only exists to
            //    keep a sequence alive, and an absent one cannot be a sequence target this run).
            var needed = new HashSet<string>(StringComparer.Ordinal);
            // Dirty concrete ids absent from the raw index are not necessarily deletions: a
            // patch (PatchOperationAdd targeting xpath="Defs", or a custom op wrapping one,
            // e.g. Big and Small's PatchOp_AddBionics) can inject a brand-new top-level def
            // that never had a raw source file at all. Defer the removed/added decision until
            // after step 4's real patch replay, the only place such a def can materialize.
            var pendingUnresolved = new List<string>();
            // Dirty/context ids themselves, as opposed to the ancestors AddAncestorsFromRawXml pulls
            // in on top of them -- the difference (needed minus this set) is reported as
            // ancestorIdsFromRawXml below.
            var topLevelIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in dirtyIds)
            {
                if (!rawById.ContainsKey(id))
                {
                    if (IsConcrete(id))
                        pendingUnresolved.Add(id);
                    continue;
                }
                if (needed.Add(id))
                {
                    topLevelIds.Add(id);
                    AddAncestorsFromRawXml(id, rawById, idByName, needed);
                }
            }
            if (contextIds != null)
            {
                foreach (string id in contextIds)
                {
                    if (!rawById.ContainsKey(id))
                        continue; // context-only; absence just means no sub-doc target to add
                    if (needed.Add(id))
                    {
                        topLevelIds.Add(id);
                        AddAncestorsFromRawXml(id, rawById, idByName, needed);
                    }
                }
            }
            foreach (string id in needed)
                if (!topLevelIds.Contains(id))
                    ancestorIdsFromRawXml.Add(id);
            if (needed.Count == 0 && pendingUnresolved.Count == 0)
                return result;

            // 3. Build the <Defs> sub-doc from imported copies (never mutate the real bodies).
            var subDoc = new XmlDocument();
            XmlElement defsRoot = subDoc.CreateElement("Defs");
            subDoc.AppendChild(defsRoot);
            var nodeById = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
            var modByNode = new Dictionary<XmlNode, ModContentPack>();
            foreach (string id in needed)
            {
                XmlElement raw = rawById[id];
                var imported = (XmlElement)subDoc.ImportNode(raw, true);
                defsRoot.AppendChild(imported);
                nodeById[id] = imported;
                if (modByRaw.TryGetValue(raw, out ModContentPack m))
                    modByNode[imported] = m;
            }

            // 3b. Build the set of top-level patch ids we actually need to reapply. Blindly
            //     replaying every mod's ENTIRE patch list (thousands of ops on a real modlist)
            //     against a sub-doc that holds only a handful of nodes is what made this stall
            //     for minutes on a real 57-mod run with no forward progress — most of those ops
            //     can never match anything in `needed` and exist purely to burn CPU on failed
            //     xpath lookups (each also going through the catch below). For any UNCHANGED
            //     mod we can safely skip a top-level op whose whole subtree (self + nested
            //     children) never touched a `needed` node id in the PRIOR graph — that's the
            //     exact same "unchanged mods behave as captured" trust SubDocExpander/
            //     RecomputeAllowlist already rely on. CHANGED mods are never filtered: they
            //     aren't faithfully represented by the prior graph (that's the whole reason
            //     RecomputeAllowlist vets them op-by-op), so we keep replaying their full patch
            //     list exactly as before.
            var changedModSet = new HashSet<string>(
                changedModIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            HashSet<string> topLevelIdsToRun = BuildTopLevelIdsToRun(graph, needed);

            // 3c. A PENDING id (no raw body — patch-injected) can only ever be resolved by
            //     actually replaying the patch that creates it. But BuildTopLevelIdsToRun above
            //     can never mark that op relevant on its own: PatchOperationAdd's captured edge
            //     records the xpath TARGET/anchor it selected (e.g. the <Defs> root), never the
            //     new child's own id (the same target-vs-content asymmetry patchInjectedOwners
            //     exists to work around — see its field comment), so the anchor essentially never
            //     intersects `needed`. Without this, an UNCHANGED mod's Add-injected def that goes
            //     dirty ONLY via inheritance-closure fan-out (its own patch file never changed, so
            //     it is reached solely by an ancestor's Seed 2 dirty) is silently misrecomputed as
            //     deleted — the op that (re-)creates it never runs. Recover the owning mod for
            //     each pending id from patchInjectedOwners (populated at capture time by
            //     ProvenanceRecorder.RecordAddedChildren) and exempt that mod's FULL patch list
            //     from the filter, exactly like a changed mod — scoped to pendingUnresolved
            //     specifically (not all of `needed`), since only a pending id is unreachable any
            //     other way.
            var modsOwningPendingContent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (graph?.PatchInjectedOwners != null)
            {
                foreach (string pendingId in pendingUnresolved)
                    if (graph.PatchInjectedOwners.TryGetValue(pendingId, out string owner) &&
                        !string.IsNullOrEmpty(owner))
                        modsOwningPendingContent.Add(owner);
            }

            // 4. Apply the real patches (every running mod, load order) over the sub-doc —
            //    exactly LoadedModManager.ApplyPatches's loop, inlined so we add no behaviour,
            //    except skipping unchanged-mod top-level ops proven irrelevant above.
            foreach (ModContentPack mod in LoadedModManager.RunningMods)
            {
                if (mod?.Patches == null)
                    continue;
                bool modChanged = changedModSet.Contains(mod.PackageId ?? string.Empty)
                    || modsOwningPendingContent.Contains(mod.PackageId ?? string.Empty);
                string sourceMod = mod.PackageId ?? mod.Name ?? "unknown";
                int index = 0;
                foreach (PatchOperation patch in mod.Patches)
                {
                    string rootId = $"{sourceMod}#{index++}";
                    if (!modChanged && topLevelIdsToRun != null && !topLevelIdsToRun.Contains(rootId))
                        continue;
                    try { patch.Apply(subDoc); }
                    catch (Exception e)
                    {
                        Logger.Debug("GAGARIN: recompute patch.Apply failed", exception: e);
                        // Patches routinely no-op or throw on a sub-doc missing their target; this
                        // is the expected sub-doc fidelity hazard the gate exists to quantify, so
                        // we record it as an error only under the metrics flag for the evidence
                        // base. Each record names the stage so it is filterable from real crashes.
                        if (GagarinPrefs.Metrics)
                            MetricsLog.Append(MetricsLog.BuildError(
                                MetricsLog.NewEnvelope(
                                    cold: !Context.IsUsingCache,
                                    packageIds: System.Linq.Enumerable.Select(
                                        LoadedModManager.RunningMods, m => m.PackageId)),
                                "recompute.patchApply", e.GetType().Name, e.Message));
                    }
                }
            }

            // 4b. Promote any pending ids that materialized via a real patch during step 4
            //     (e.g. a PatchOperationAdd appending straight into defsRoot). Their ParentName
            //     ancestor (if any) is pulled in from the raw index too, so inheritance resolves
            //     exactly as the full load would. An id still absent here is a genuine deletion.
            if (pendingUnresolved.Count > 0)
            {
                var byTopLevelId = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
                foreach (var (id, el) in TopLevelDefs.Enumerate(defsRoot))
                    byTopLevelId[id] = el;
                foreach (string id in pendingUnresolved)
                {
                    if (byTopLevelId.TryGetValue(id, out XmlElement promoted))
                    {
                        nodeById[id] = promoted;
                        PromoteAncestors(promoted, rawById, idByName, nodeById, subDoc, defsRoot);
                    }
                    else
                    {
                        removedConcreteIds.Add(id);
                    }
                }
            }

            // 5. Real inheritance resolution on isolated (clobbered) global state.
            XmlInheritance.Clear();
            try
            {
                foreach (var kv in nodeById)
                {
                    modByNode.TryGetValue(kv.Value, out ModContentPack mod);
                    XmlInheritance.TryRegister(kv.Value, mod);
                }
                XmlInheritance.Resolve();

                // 6. Extract + massage each DIRTY concrete def to match Save's cache entry.
                //    Context defs are deliberately NOT extracted: they were present only to keep
                //    sequences from aborting, and the baseline cache already holds their values.
                foreach (string id in dirtyIds)
                {
                    if (!IsConcrete(id) || !nodeById.TryGetValue(id, out XmlElement node))
                        continue;

                    // MayRequire fidelity: the real loader (ParseAndProcessXML) drops a def whose
                    // root node carries a failing MayRequire / MayRequireAnyOf — it `continue`s
                    // before registering it, so it never reaches Unified.xml. DefRecompute reads raw
                    // bodies and does not parse defs, so without this it would recompute a gated def
                    // as present and the splice would diverge from a full rebuild that dropped it
                    // (the classic case: a mod providing the required packageId leaves the load).
                    // `node` is the post-patch sub-doc node — exactly the state the loader evaluates
                    // MayRequire against (it reads item4's own attributes, post-patch, pre-resolve).
                    // Read via Attributes[...]?.Value so an absent attribute is null (GetAttribute
                    // would return "" and falsely trip the gate). A failing def is routed to
                    // removedConcreteIds so the splice drops it, matching the rebuild byte-for-byte.
                    if (!MayRequireGate.Passes(
                            node.Attributes["MayRequire"]?.Value,
                            node.Attributes["MayRequireAnyOf"]?.Value,
                            ModLister.AllModsActiveNoSuffix,
                            ModLister.AnyModActiveNoSuffix))
                    {
                        removedConcreteIds.Add(id);
                        continue;
                    }

                    result[id] = Massage(node, subDoc).OuterXml;
                }
            }
            finally
            {
                XmlInheritance.Clear();
            }
            return result;
        }

        // Reproduces CachedDefHelper.Save's per-def transform: a def with no ParentName is cached
        // as its post-patch node verbatim; an inherited def is cached as its resolved node with
        // ParentName stripped, an element-name mismatch rebuilt under the def's own name, and the
        // def's Class attribute carried over if the resolved node lacks it.
        private static XmlElement Massage(XmlElement node, XmlDocument owner)
        {
            XmlNode resolvedNode = XmlInheritance.GetResolvedNodeFor(node);
            if (ReferenceEquals(resolvedNode, node))
                return node; // no inheritance — post-patch node is the cache value

            var resolved = (XmlElement)resolvedNode;
            resolved.RemoveAttribute("ParentName");
            if (resolved.Name != node.Name)
            {
                XmlElement temp = owner.CreateElement(node.Name);
                foreach (XmlNode n in resolved.ChildNodes)
                    if (n.NodeType == XmlNodeType.Element)
                        temp.AppendChild(owner.ImportNode(n, true));
                resolved = temp;
            }
            else if (node.HasAttribute("Class") && !resolved.HasAttribute("Class"))
            {
                resolved.SetAttribute("Class", node.GetAttribute("Class"));
            }
            return resolved;
        }

        // Returns the set of top-level patch ids ("{sourceMod}#{index}") whose subtree (the op
        // itself or any nested child, e.g. a PatchOperationSequence's ".operations[2]") touched a
        // `needed` node id per the PRIOR graph's patch edges. Null graph means "no info" — caller
        // must not filter (falls back to applying everything, the pre-optimization behaviour).
        private static HashSet<string> BuildTopLevelIdsToRun(DependencyGraphData graph, HashSet<string> needed)
        {
            if (graph?.PatchEdges == null)
                return null;

            var relevantIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (GraphPatchEdge edge in graph.PatchEdges)
            {
                if (edge.PatchId == null || edge.ModifiedNodeIds == null)
                    continue;
                foreach (string nid in edge.ModifiedNodeIds)
                {
                    if (needed.Contains(nid))
                    {
                        relevantIds.Add(edge.PatchId);
                        break;
                    }
                }
            }
            if (relevantIds.Count == 0)
                return new HashSet<string>(StringComparer.Ordinal); // no live op relevant at all

            var topLevel = new HashSet<string>(StringComparer.Ordinal);
            foreach (string id in relevantIds)
            {
                // The nested-operation suffix (".operations[2]", ".match", ...) never contains
                // '#', but packageIds routinely do contain '.' (reverse-DNS style, e.g.
                // "joof.testharness.static") -- so a bare id.IndexOf('.') can land inside the
                // packageId itself. Search for the suffix's dot only after the '#' that
                // separates sourceMod from its index.
                int hash = id.IndexOf('#');
                int dot = hash >= 0 ? id.IndexOf('.', hash) : id.IndexOf('.');
                topLevel.Add(dot >= 0 ? id.Substring(0, dot) : id);
            }
            return topLevel;
        }

        // Concrete defs key "{DefType}/{defName}"; abstract bases key "{DefType}@{Name}".
        private static bool IsConcrete(string id) => id.IndexOf('/') >= 0;

        // Follow the ParentName chain through the CURRENT load's raw XML, adding every ancestor id
        // to needed so the sub-doc can resolve inheritance exactly as the full load would. Distinct
        // from RecomputeAllowlist.CanRecompute's own ancestor walk, which reads InheritanceEdges out
        // of the PRIOR load's captured graph (see DirtySetGate.RunRecompute's graphPath comment) —
        // the two agree only when a dirty def's ParentName hasn't changed since the prior load. If
        // it has, CanRecompute's relevantTargets can miss an ancestor this walk pulls in, so an edge
        // touching that ancestor is never vetted for safety. Currently caught downstream: the
        // recompute path isn't live-serving yet (DirtySetGate runs it after the real rebuild's Save
        // and diffs the splice against it), so a bad admission surfaces as a recompute mismatch in
        // the report rather than corrupting what the player sees. That safety net goes away if this
        // path is ever promoted to skip the full rebuild — close this divergence first.
        private static void AddAncestorsFromRawXml(string id, Dictionary<string, XmlElement> rawById,
            Dictionary<string, string> idByName, HashSet<string> needed)
        {
            string cur = id;
            while (rawById.TryGetValue(cur, out XmlElement node))
            {
                string parentName = node.GetAttribute("ParentName");
                if (string.IsNullOrEmpty(parentName) || !idByName.TryGetValue(parentName, out string parentId))
                    return;
                if (!needed.Add(parentId))
                    return; // already pulled in (and its ancestors)
                cur = parentId;
            }
        }

        // Mirrors AddAncestorsFromRawXml for a node PROMOTED after step 4 (a patch-injected def that had
        // no raw body of its own, so it could not go through step 2's ancestor walk). Follows
        // node's ParentName chain through the raw index, importing each still-missing ancestor
        // straight into the already-built sub-doc/nodeById so step 5's inheritance resolution
        // sees it exactly as it would a def pulled in up front.
        private static void PromoteAncestors(XmlElement node, Dictionary<string, XmlElement> rawById,
            Dictionary<string, string> idByName, Dictionary<string, XmlElement> nodeById,
            XmlDocument subDoc, XmlElement defsRoot)
        {
            string parentName = node.GetAttribute("ParentName");
            while (!string.IsNullOrEmpty(parentName) && idByName.TryGetValue(parentName, out string parentId))
            {
                if (nodeById.ContainsKey(parentId))
                    return; // already present (imported earlier, or itself promoted)
                if (!rawById.TryGetValue(parentId, out XmlElement rawParent))
                    return; // ancestor itself missing -- inheritance resolution will surface it
                var imported = (XmlElement)subDoc.ImportNode(rawParent, true);
                defsRoot.AppendChild(imported);
                nodeById[parentId] = imported;
                parentName = imported.GetAttribute("ParentName");
            }
        }

        // Builds the raw-body indices from the def assets loaded this run (Context.XmlAssets,
        // <Defs>-rooted). Concrete -> "{Type}/{defName}", abstract -> "{Type}@{Name}"; Name -> id;
        // node -> owning mod (resolved from the asset's mod).
        private static void BuildRawIndex(Dictionary<string, XmlElement> rawById,
            Dictionary<string, string> idByName, Dictionary<XmlNode, ModContentPack> modByRaw)
        {
            if (Context.XmlAssets == null)
                return;
            foreach (LoadableXmlAsset asset in Context.XmlAssets.Values)
            {
                XmlElement root = asset?.xmlDoc?.DocumentElement;
                if (root == null || root.Name != "Defs")
                    continue;
                foreach (XmlNode child in root.ChildNodes)
                {
                    if (!(child is XmlElement def))
                        continue;
                    string nameAttr = def.GetAttribute("Name");
                    string defName = def["defName"]?.InnerText;
                    string id = !string.IsNullOrEmpty(defName) ? def.Name + "/" + defName
                              : !string.IsNullOrEmpty(nameAttr) ? def.Name + "@" + nameAttr
                              : null;
                    if (id == null)
                        continue;
                    rawById[id] = def;
                    if (!string.IsNullOrEmpty(nameAttr))
                        idByName[nameAttr] = id;
                    if (asset.mod != null)
                        modByRaw[def] = asset.mod;
                }
            }
        }
    }
}
