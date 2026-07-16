// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// UnifiedCacheDiff.cs (Piece D — Milestone 2b: real-engine zero-diff gate)
//
// Contains: the pure (System.Xml only) comparison of two unified-cache documents
// (DefXmlStorage: a list of <Item path="..."><ResolvedDef/></Item> wrappers, one fully-
// resolved def each) — index them by def id, and find the defs NOT in the dirty set whose
// resolved XML differs between a prior cache and a full rebuild.
//
// Used for: the M2b correctness GATE. Before building the real-engine recompute, we must prove
// the dirty set (M1 structural + M2a wildcard flips) is a true SUPERSET against the actual
// engine, not just on synthetic fixtures. A full rebuild on a changed load produces the
// ground-truth resolved XML for every def; any def the dirty set did NOT flag must be
// byte-identical to its prior-cache value. A non-dirty def that differs is a subset error —
// the exact silent-staleness failure the whole dirty-set effort exists to prevent. For a
// superset-safe dirty set this list is empty.
//
// Why pure: it is just XML-vs-XML over the cache schema, so it needs no RimWorld types and is
// unit-tested offline. The RimWorld-facing DirtySetGate snapshots the prior Unified.xml before
// the load deletes it, lets the normal rebuild write the new one, then feeds both here.

using System;
using System.Collections.Generic;
using System.Xml;

namespace Gagarin
{
    public static class UnifiedCacheDiff
    {
        // id -> resolved def XML (the <Item>'s inner def element, as text), for every item in
        // a loaded DefXmlStorage document. The cache stores only concrete defs (abstract bases
        // never become Def objects, so they are never registered), hence every item keys by
        // "{DefType}/{defName}". Both documents must be parsed with the same reader settings
        // (IgnoreWhitespace) so identical content yields identical OuterXml. Last occurrence
        // wins on the rare duplicate id.
        public static Dictionary<string, string> IndexById(XmlDocument defXmlStorage)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            XmlElement root = defXmlStorage?.DocumentElement;
            if (root == null)
                return map;
            foreach (XmlNode item in root.ChildNodes)
            {
                if (!(item is XmlElement))
                    continue;
                XmlElement def = FirstElement(item);
                if (def == null)
                    continue;
                string id = DefId(def);
                if (id != null)
                    map[id] = def.OuterXml;
            }
            return map;
        }

        // id -> source path (the <Item>'s "path" attribute), for every item in a loaded
        // DefXmlStorage document. Same keying as IndexById. Exists so a trial-discovered new
        // def (issue #72) can recover its path from the ground-truth full rebuild that always
        // precedes a gate/recompute run: TrialExecution's synthetic scratch doc never goes
        // through RimWorld's real DirectXmlToObjectNew.DefFromNodeNew hook (the only place a
        // LoadableXmlAsset is known), so it has no way to produce this path itself, but the
        // rebuild that just ran for comparison already carries it for every def, patch-injected
        // or not. Only valid because gate mode always runs AFTER a real rebuild; would need
        // reconsidering if the incremental path is ever used to skip the rebuild in production.
        public static Dictionary<string, string> IndexPathsById(XmlDocument defXmlStorage)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            XmlElement root = defXmlStorage?.DocumentElement;
            if (root == null)
                return map;
            foreach (XmlNode item in root.ChildNodes)
            {
                if (!(item is XmlElement itemElement))
                    continue;
                XmlElement def = FirstElement(itemElement);
                if (def == null)
                    continue;
                string id = DefId(def);
                if (id != null)
                    map[id] = itemElement.GetAttribute("path");
            }
            return map;
        }

        // The def ids — excluding the dirty set — whose resolved XML differs between baseline
        // and rebuild. Empty iff the dirty set is a true superset of what actually changed.
        // A def present in one cache but not the other (and not dirty) counts as a mismatch:
        // the rebuild added or dropped a def the dirty set did not account for.
        public static List<string> NonDirtyMismatches(
            Dictionary<string, string> baseline, Dictionary<string, string> rebuild,
            ICollection<string> dirtyIds)
        {
            var mismatches = new List<string>();
            var ids = new HashSet<string>(baseline.Keys);
            ids.UnionWith(rebuild.Keys);
            foreach (string id in ids)
            {
                if (dirtyIds != null && dirtyIds.Contains(id))
                    continue; // dirty defs are allowed (expected) to differ
                baseline.TryGetValue(id, out string b);
                rebuild.TryGetValue(id, out string r);
                if (!string.Equals(b, r, StringComparison.Ordinal))
                    mismatches.Add(id);
            }
            return mismatches;
        }

        private static XmlElement FirstElement(XmlNode parent)
        {
            foreach (XmlNode c in parent.ChildNodes)
                if (c is XmlElement e)
                    return e;
            return null;
        }

        // Mirrors the capture's keying for concrete defs: "{DefType}/{defName}", or
        // "{DefType}@{Name}" for a Name-only element (not expected in the cache, but handled
        // so an unexpected abstract item keys consistently rather than silently colliding).
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
