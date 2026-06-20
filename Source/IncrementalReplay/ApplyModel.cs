// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// ApplyModel.cs (Piece C — replay harness)
//
// Contains: the headless apply model — WorkingDef plus the patch-application,
// cross-mod ParentName inheritance resolution, Unified-style serialization,
// FullRebuild (the ground-truth from-scratch path), and the state-reusing
// targeted RecomputeDirtyWithState.
//
// Used for: standing in for Verse.PatchOperation.Apply so the replay harness can
// run entirely offline; it produces both the full-rebuild oracle and the
// incremental recompute the dirty-set algorithm drives.
//
// Why: constructing real PatchOperation objects needs a booted game, so we model
// the two semantics the dirty-set algorithm must respect — patches matching by
// structure across mods, and load-order/child-over-parent inheritance — faithfully
// but minimally, clearly labelled as the documented fallback from the task brief.

using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace Gagarin.IncrementalReplay
{
    // Minimal, FAITHFUL re-implementation of the RimWorld def pipeline, headless.
    //
    // Why not call Verse.PatchOperation.Apply directly? Its public Apply() exists and
    // is trivial, but producing a real PatchOperation instance requires
    // DirectXmlToObject + GenTypes.AllTypes + ModContentPacks, i.e. a booted game.
    // We therefore model the two semantics that the dirty-set algorithm must respect:
    //
    //   1. PATCHES run, in load order, against the single combined raw document
    //      (Defs/...). An xpath can match ANY def regardless of which mod declared it
    //      (this is the cross-mod wildcard hazard). PatchOperationReplace-style: it
    //      sets a child element's text on every matched def.
    //
    //   2. INHERITANCE (ParentName) is resolved AFTER patching, child-over-parent,
    //      and parent lookup crosses mods by load order (GetBestParentFor). We model
    //      the common case: child element bodies override / add over the parent body.
    //
    // The output is a Unified-style document: one <def> element per resolved def,
    // in stable (defType, defName) order so diffs are order-insensitive by construction.
    //
    // This is the documented fallback from the task brief: a faithful minimal apply
    // model, clearly labelled, standing in for headless PatchOperation.Apply.

    internal static class ApplyModel
    {
        // A def as it exists mid-pipeline: a mutable bag of child element -> value,
        // plus inheritance metadata. Real RimWorld bodies are arbitrary XML trees;
        // a flat element->value map is a deliberate simplification that still captures
        // patch targeting, predicate matching, and child-over-parent override.
        internal sealed class WorkingDef
        {
            public string DefType;
            public string DefName;
            public string ParentName;
            public string Name;
            public int LoadOrder;        // load order of the declaring mod (for parent resolution)
            public string SourceMod;
            public Dictionary<string, string> Elements = new Dictionary<string, string>();

            public string NodeId => DefType + "/" + DefName;

            public WorkingDef Clone()
            {
                return new WorkingDef
                {
                    DefType = DefType, DefName = DefName, ParentName = ParentName,
                    Name = Name, LoadOrder = LoadOrder, SourceMod = SourceMod,
                    Elements = new Dictionary<string, string>(Elements)
                };
            }
        }

        // Parse a fixture body (a tiny XML fragment of <elem>value</elem> children)
        // into the flat element map.
        private static Dictionary<string, string> ParseBody(string body)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(body))
                return map;
            var doc = new XmlDocument();
            doc.LoadXml("<root>" + body + "</root>");
            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
            {
                if (n.NodeType != XmlNodeType.Element)
                    continue;
                map[n.Name] = n.InnerText;
            }
            return map;
        }

        // Build the combined working set from the fixture (no patches/inheritance yet).
        private static Dictionary<string, WorkingDef> BuildRaw(Fixture fixture)
        {
            var defs = new Dictionary<string, WorkingDef>();
            foreach (var mod in fixture.InLoadOrder())
                foreach (var d in mod.Defs)
                {
                    var wd = new WorkingDef
                    {
                        DefType = d.DefType, DefName = d.DefName, ParentName = d.ParentName,
                        Name = d.Name, LoadOrder = mod.LoadOrder, SourceMod = mod.PackageId,
                        Elements = ParseBody(d.Body)
                    };
                    // Last writer wins on duplicate defName, matching RimWorld's later-mod-overrides.
                    defs[wd.NodeId] = wd;
                }
            return defs;
        }

        // Does this wildcard patch match this def? Predicate = "def body contains the
        // named child element" — a faithful stand-in for an xpath predicate that
        // selects by structure rather than by defName, and so can sweep across mods.
        public static bool WildcardMatches(FixturePatch p, WorkingDef d)
        {
            return d.DefType == p.DefType && d.Elements.ContainsKey(p.PredicateElement);
        }

        // Apply every patch, in load order, against the combined document.
        // Returns the patched (pre-inheritance) working set.
        private static Dictionary<string, WorkingDef> ApplyPatches(
            Fixture fixture, Dictionary<string, WorkingDef> defs)
        {
            foreach (var mod in fixture.InLoadOrder())
                foreach (var p in mod.Patches)
                {
                    if (p.Kind == PatchKind.Identity)
                    {
                        var id = p.DefType + "/" + p.TargetDefName;
                        if (defs.TryGetValue(id, out var target))
                            target.Elements[p.SetElement] = p.SetValue;
                    }
                    else // Wildcard
                    {
                        foreach (var d in defs.Values)
                            if (WildcardMatches(p, d))
                                d.Elements[p.SetElement] = p.SetValue;
                    }
                }
            return defs;
        }

        // Resolve ParentName inheritance, child-over-parent, after patching.
        // Parent lookup is by Name and crosses mods (closest mod at-or-before the
        // child's load order, matching XmlInheritance.GetBestParentFor).
        private static void ResolveInheritance(Dictionary<string, WorkingDef> defs)
        {
            // Index declared Names -> candidate defs.
            var byName = new Dictionary<string, List<WorkingDef>>();
            foreach (var d in defs.Values)
                if (!string.IsNullOrEmpty(d.Name))
                {
                    if (!byName.TryGetValue(d.Name, out var list))
                        byName[d.Name] = list = new List<WorkingDef>();
                    list.Add(d);
                }

            // Resolve in dependency order: a def can only resolve once its parent has.
            var resolved = new Dictionary<string, Dictionary<string, string>>();

            Dictionary<string, string> Resolve(WorkingDef d)
            {
                if (resolved.TryGetValue(d.NodeId, out var cached))
                    return cached;
                if (string.IsNullOrEmpty(d.ParentName) || !byName.TryGetValue(d.ParentName, out var cands))
                {
                    resolved[d.NodeId] = d.Elements;
                    return d.Elements;
                }
                // GetBestParentFor: highest loadOrder that is <= child's loadOrder.
                WorkingDef best = null;
                foreach (var c in cands)
                    if (c.LoadOrder <= d.LoadOrder && (best == null || c.LoadOrder > best.LoadOrder))
                        best = c;
                if (best == null || best == d)
                {
                    resolved[d.NodeId] = d.Elements;
                    return d.Elements;
                }
                var parentElems = Resolve(best);
                // Child overrides/adds over a copy of the parent body.
                var merged = new Dictionary<string, string>(parentElems);
                foreach (var kv in d.Elements)
                    merged[kv.Key] = kv.Value;
                resolved[d.NodeId] = merged;
                return merged;
            }

            foreach (var d in defs.Values)
                d.Elements = Resolve(d);
        }

        // Serialize the resolved working set to a Unified-style XmlDocument.
        // Stable ordering (defType, defName, element name) makes the canonical diff
        // independent of dictionary/iteration order.
        public static XmlDocument ToUnified(Dictionary<string, WorkingDef> defs)
        {
            var keys = new List<string>(defs.Keys);
            keys.Sort(System.StringComparer.Ordinal);

            var sb = new StringBuilder();
            sb.Append("<Defs>");
            foreach (var k in keys)
            {
                var d = defs[k];
                sb.Append('<').Append(d.DefType).Append(" defName=\"").Append(d.DefName).Append("\">");
                var elemKeys = new List<string>(d.Elements.Keys);
                elemKeys.Sort(System.StringComparer.Ordinal);
                foreach (var ek in elemKeys)
                {
                    sb.Append('<').Append(ek).Append('>');
                    sb.Append(System.Security.SecurityElement.Escape(d.Elements[ek]));
                    sb.Append("</").Append(ek).Append('>');
                }
                sb.Append("</").Append(d.DefType).Append('>');
            }
            sb.Append("</Defs>");

            var doc = new XmlDocument();
            doc.LoadXml(sb.ToString());
            return doc;
        }

        // Full from-scratch rebuild: raw -> patch -> inherit -> unified.
        // This is the ground-truth the incremental result must match exactly.
        public static XmlDocument FullRebuild(Fixture fixture)
        {
            var defs = BuildRaw(fixture);
            ApplyPatches(fixture, defs);
            ResolveInheritance(defs);
            return ToUnified(defs);
        }

        // Expose the patched-but-unresolved view so the dirty-set algorithm can test
        // wildcard predicates against the same body state the real pipeline would see.
        public static Dictionary<string, WorkingDef> BuildPatched(Fixture fixture)
        {
            var defs = BuildRaw(fixture);
            ApplyPatches(fixture, defs);
            return defs;
        }

        // State-reusing targeted recompute: resolve ONLY the dirty nodes using the
        // already-loaded indices (LoadedState), so work is proportional to the dirty set
        // and its inheritance chains — NOT the whole load order. This is what the scaled
        // benchmark times as "the incremental recompute".
        public static Dictionary<string, WorkingDef> RecomputeDirtyWithState(
            LoadedState state, HashSet<string> dirty)
        {
            var resolvedCache = new Dictionary<string, Dictionary<string, string>>();
            var result = new Dictionary<string, WorkingDef>();
            foreach (var id in dirty)
            {
                var p = state.Patched(id);
                if (p == null) continue;            // dirty def was deleted -> stays gone
                var resolved = p.Clone();
                resolved.Elements = state.Resolve(id, resolvedCache);
                result[id] = resolved;
            }
            return result;
        }
    }
}
