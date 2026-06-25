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
            out List<string> removedConcreteIds)
        {
            removedConcreteIds = new List<string>();
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
            foreach (string id in dirtyIds)
            {
                if (!rawById.ContainsKey(id))
                {
                    if (IsConcrete(id))
                        removedConcreteIds.Add(id);
                    continue;
                }
                if (needed.Add(id))
                    AddAncestors(id, rawById, idByName, needed);
            }
            if (contextIds != null)
            {
                foreach (string id in contextIds)
                {
                    if (!rawById.ContainsKey(id))
                        continue; // context-only; absence just means no sub-doc target to add
                    if (needed.Add(id))
                        AddAncestors(id, rawById, idByName, needed);
                }
            }
            if (needed.Count == 0)
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

            // 4. Apply the real patches (every running mod, load order) over the sub-doc —
            //    exactly LoadedModManager.ApplyPatches's loop, inlined so we add no behaviour.
            foreach (ModContentPack mod in LoadedModManager.RunningMods)
            {
                if (mod?.Patches == null)
                    continue;
                foreach (PatchOperation patch in mod.Patches)
                {
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

        // Concrete defs key "{DefType}/{defName}"; abstract bases key "{DefType}@{Name}".
        private static bool IsConcrete(string id) => id.IndexOf('/') >= 0;

        // Follow the ParentName chain, adding every ancestor id to needed so the sub-doc can
        // resolve inheritance exactly as the full load would.
        private static void AddAncestors(string id, Dictionary<string, XmlElement> rawById,
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
