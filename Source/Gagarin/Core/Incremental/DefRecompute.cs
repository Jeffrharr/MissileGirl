// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DefRecompute.cs (Piece D — Milestone 2b: real-engine recompute)
//
// Contains: the engine-touching recompute — given the dirty set, rebuild each dirty CONCRETE
// def's resolved XML the way a full load would, but over a tiny sub-document instead of the whole
// ~28k-def database. It drives the REAL pipeline: the actual PatchOperation.Apply of every
// running mod (same loop as LoadedModManager.ApplyPatches) over a <Defs> sub-doc of the dirty
// defs plus their inheritance ancestors, then the real XmlInheritance resolution, then the exact
// same post-resolution massaging CachedDefHelper.Save does — so the output is byte-identical to a
// full rebuild's cache entry. UnifiedCacheSplice merges these into the prior cache; the gate
// proves the result matches a full rebuild over every def.
//
// Why it is RimWorld-coupled (not offline-tested): faithfulness REQUIRES the real engine, so this
// can only be validated in-game via the gate. The pure halves it feeds (splice, diff) are
// offline-tested; this is the part the in-game zero-diff gate exists to verify.
//
// HAZARD — XmlInheritance is a global static. We Clear() it, register only our sub-doc nodes,
// Resolve(), extract, then Clear() again. This runs at end of load (after CachedDefHelper.Save
// has already consumed the live load's inheritance state), so clobbering it is expected to be
// safe; the gate / a game error would reveal otherwise. It must never run concurrently with the
// live load's own resolution.
//
// Known fidelity limits (the gate quantifies them): a patch whose match/effect depends on defs
// NOT in the sub-doc (positional xpath, cross-def reads, FindMod/Conditional scoping) can differ;
// candidate bodies are RAW, so a match that only appears after an earlier patch mutates an
// unrelated def is missed. Strategy is to widen coverage until the gate is zero-diff.

using System;
using System.Collections.Generic;
using System.Xml;
using MissileGirl;
using Verse;

namespace Gagarin
{
    public static class DefRecompute
    {
        // Recompute resolved XML (by def id) for every dirty CONCRETE def, via the real engine.
        // removedConcreteIds = dirty concrete ids whose raw body no longer exists (deleted defs),
        // for the splice to drop. Abstract (@Name) dirty ids are never cache items themselves but
        // are pulled in as inheritance ancestors when a dirty concrete def needs them.
        public static Dictionary<string, string> Recompute(
            ICollection<string> dirtyIds, out List<string> removedConcreteIds)
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

            // 2. Collect the nodes the sub-doc needs: each present dirty def + its transitive
            //    inheritance ancestors. A dirty concrete id absent from raw is a deletion.
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
                    catch (Exception e) { Logger.Debug("GAGARIN: recompute patch.Apply failed", exception: e); }
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

                // 6. Extract + massage each dirty concrete def to match Save's cache entry.
                foreach (string id in dirtyIds)
                {
                    if (!IsConcrete(id) || !nodeById.TryGetValue(id, out XmlElement node))
                        continue;
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
