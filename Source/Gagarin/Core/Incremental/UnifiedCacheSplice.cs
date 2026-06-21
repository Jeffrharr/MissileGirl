// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// UnifiedCacheSplice.cs (Piece D — Milestone 2b: recompute + splice)
//
// Contains: the pure (System.Xml only) splice that turns a prior unified cache plus a small set
// of recomputed defs into the new unified cache — replacing changed defs' <Item>s, dropping
// removed defs, adding new ones, and copying every untouched <Item> through verbatim.
//
// Used for: the "splice into Unified.xml" half of M2b. The recompute (real PatchOperation +
// XmlInheritance over a dirty <Defs> sub-doc) produces the resolved XML for each dirty def; this
// splices those into the baseline DefXmlStorage so unchanged defs are reused byte-for-byte (the
// entire point of incremental — avoid rebuilding the ~99% that did not move). The result is
// gated by UnifiedCacheDiff against a full rebuild: spliced must byte-match rebuilt over ALL defs.
//
// Why pure: it is just XML tree manipulation over the cache schema, so it needs no RimWorld types
// and is unit-tested offline. It is deliberately split from the recompute (which must touch the
// RimWorld engine) so the load-bearing splice logic is verifiable without launching the game.
//
// Schema reminder: DefXmlStorage = <Item path="src" [resolved="true"]><ResolvedDef/></Item> per
// concrete def; defs key by {DefType}/{defName}. The splice preserves a replaced def's existing
// Item wrapper (path/resolved attributes) and only swaps the inner def element, so a reused-or-
// replaced item is indistinguishable from how a full rebuild would have written it.

using System;
using System.Collections.Generic;
using System.Xml;

namespace Gagarin
{
    public static class UnifiedCacheSplice
    {
        // Returns a NEW DefXmlStorage document = baseline with:
        //   * each id in removedIds dropped,
        //   * each id in recomputedById whose def already exists replaced (its Item wrapper kept,
        //     inner def element swapped for the recomputed XML),
        //   * each id in recomputedById not present in baseline appended as a new Item (path from
        //     newPaths if supplied, else empty),
        //   * every other Item copied through unchanged.
        // recomputedById values are the resolved def element as XML text (e.g. "<ThingDef>...").
        public static XmlDocument Splice(XmlDocument baseline,
            IDictionary<string, string> recomputedById, ICollection<string> removedIds,
            IDictionary<string, string> newPaths = null)
        {
            var result = new XmlDocument();
            XmlElement root = result.CreateElement("DefXmlStorage");
            result.AppendChild(root);

            var recomputed = recomputedById ?? new Dictionary<string, string>();
            var removed = removedIds ?? new string[0];
            var removedSet = removed as ICollection<string> ?? new HashSet<string>(removed);
            var used = new HashSet<string>(StringComparer.Ordinal);

            XmlElement baseRoot = baseline?.DocumentElement;
            if (baseRoot != null)
            {
                foreach (XmlNode item in baseRoot.ChildNodes)
                {
                    if (!(item is XmlElement itemEl))
                        continue;
                    XmlElement def = FirstElement(itemEl);
                    string id = def != null ? DefId(def) : null;

                    if (id != null && removedSet.Contains(id))
                        continue; // dropped

                    if (id != null && recomputed.TryGetValue(id, out string newXml))
                    {
                        // Replace: keep this Item's wrapper (path/resolved), swap the inner def.
                        XmlElement wrapper = (XmlElement)result.ImportNode(itemEl, false);
                        wrapper.AppendChild(Parse(result, newXml));
                        root.AppendChild(wrapper);
                        used.Add(id);
                        continue;
                    }

                    root.AppendChild(result.ImportNode(itemEl, true)); // verbatim reuse
                }
            }

            // New defs (recomputed ids not present in the baseline).
            foreach (var kv in recomputed)
            {
                if (used.Contains(kv.Key))
                    continue;
                string path = null;
                newPaths?.TryGetValue(kv.Key, out path);
                XmlElement wrapper = result.CreateElement("Item");
                wrapper.SetAttribute("path", path ?? string.Empty);
                wrapper.AppendChild(Parse(result, kv.Value));
                root.AppendChild(wrapper);
            }

            return result;
        }

        private static XmlNode Parse(XmlDocument owner, string xml)
        {
            var frag = new XmlDocument();
            frag.LoadXml(xml);
            return owner.ImportNode(frag.DocumentElement, true);
        }

        private static XmlElement FirstElement(XmlNode parent)
        {
            foreach (XmlNode c in parent.ChildNodes)
                if (c is XmlElement e)
                    return e;
            return null;
        }

        private static string DefId(XmlElement def)
        {
            string defName = def["defName"]?.InnerText;
            if (!string.IsNullOrEmpty(defName))
                return def.Name + "/" + defName;
            string name = def.GetAttribute("Name");
            if (!string.IsNullOrEmpty(name))
                return def.Name + "@" + name;
            return null;
        }
    }
}
