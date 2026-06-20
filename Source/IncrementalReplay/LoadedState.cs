// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// LoadedState.cs (Piece C — replay harness)
//
// Contains: LoadedState — the "already-loaded" indices (raw def map, patch list,
// Name index for inheritance, baseline wildcard hit sets) plus per-def Patched and
// Resolve helpers that compute a single def's body on demand.
//
// Used for: modelling the cache state production Gagarin would read at launch, so
// the incremental recompute reuses it and touches only the changed slice plus its
// transitive closure.
//
// Why: the whole value of the incremental design rests on these indices being
// built ONCE. Timing per-change work that reuses them — against a from-scratch full
// rebuild — is the apples-to-apples comparison where the sub-linear speedup shows.

using System.Collections.Generic;

namespace Gagarin.IncrementalReplay
{
    // The "already-loaded" indices that, in production Gagarin, are read from the cache
    // alongside Unified.xml: the raw def map, the patch list, the Name index for
    // inheritance, and the baseline wildcard match sets (from the Piece A graph).
    //
    // The whole point of the incremental design is that THESE ARE BUILT ONCE. On a
    // single-mod change only that mod's slice is re-parsed. The scaled benchmark builds
    // a LoadedState once (untimed, modelling the cache load) and then times only the
    // per-change work that reuses it. That is the apples-to-apples comparison against a
    // from-scratch full rebuild, and it is where the sub-linear speedup actually shows.

    internal sealed class LoadedState
    {
        // Raw (pre-patch) def bodies, keyed by node id.
        public readonly Dictionary<string, ApplyModel.WorkingDef> Raw
            = new Dictionary<string, ApplyModel.WorkingDef>();

        // Patches in load order.
        public readonly List<FixturePatch> Patches = new List<FixturePatch>();

        // Name -> defs declaring it (for cross-mod inheritance parent lookup).
        public readonly Dictionary<string, List<ApplyModel.WorkingDef>> ByName
            = new Dictionary<string, List<ApplyModel.WorkingDef>>();

        // Baseline wildcard match sets, patchId -> matched node ids (from Piece A graph).
        public readonly Dictionary<string, HashSet<string>> BaselineWildcardHits
            = new Dictionary<string, HashSet<string>>();

        public static LoadedState Build(Fixture fixture, DependencyGraph graph)
        {
            var s = new LoadedState();
            foreach (var mod in fixture.InLoadOrder())
            {
                foreach (var d in mod.Defs)
                    s.Raw[d.NodeId] = new ApplyModel.WorkingDef
                    {
                        DefType = d.DefType, DefName = d.DefName, ParentName = d.ParentName,
                        Name = d.Name, LoadOrder = mod.LoadOrder, SourceMod = mod.PackageId,
                        Elements = ParseBody(d.Body)
                    };
                foreach (var p in mod.Patches)
                    s.Patches.Add(p);
            }
            foreach (var d in s.Raw.Values)
                if (!string.IsNullOrEmpty(d.Name))
                {
                    if (!s.ByName.TryGetValue(d.Name, out var list))
                        s.ByName[d.Name] = list = new List<ApplyModel.WorkingDef>();
                    list.Add(d);
                }
            foreach (var e in graph.PatchEdges)
            {
                if (!s.BaselineWildcardHits.TryGetValue(e.PatchId, out var set))
                    s.BaselineWildcardHits[e.PatchId] = set = new HashSet<string>();
                foreach (var n in e.MatchedNodeIds) set.Add(n);
            }
            return s;
        }

        private static Dictionary<string, string> ParseBody(string body)
        {
            var map = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(body)) return map;
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml("<root>" + body + "</root>");
            foreach (System.Xml.XmlNode n in doc.DocumentElement.ChildNodes)
                if (n.NodeType == System.Xml.XmlNodeType.Element)
                    map[n.Name] = n.InnerText;
            return map;
        }

        // Compute the PATCHED body of a single def using the loaded indices. Patches act
        // per-def, so we apply the sequence in load order to one def in isolation.
        public ApplyModel.WorkingDef Patched(string id)
        {
            if (!Raw.TryGetValue(id, out var r)) return null;
            var wd = r.Clone();
            foreach (var p in Patches)
            {
                if (p.Kind == PatchKind.Identity)
                {
                    if (wd.DefType == p.DefType && wd.DefName == p.TargetDefName)
                        wd.Elements[p.SetElement] = p.SetValue;
                }
                else if (ApplyModel.WildcardMatches(p, wd))
                    wd.Elements[p.SetElement] = p.SetValue;
            }
            return wd;
        }

        // Resolve a single def's body (patch + inheritance) using the loaded indices,
        // walking its ParentName chain on demand. Work is proportional to chain depth,
        // not load size.
        public Dictionary<string, string> Resolve(string id, Dictionary<string, Dictionary<string, string>> cache)
        {
            if (cache.TryGetValue(id, out var c)) return c;
            var p = Patched(id);
            if (p == null) { cache[id] = null; return null; }
            if (string.IsNullOrEmpty(p.ParentName) || !ByName.TryGetValue(p.ParentName, out var cands))
            {
                cache[id] = p.Elements;
                return p.Elements;
            }
            ApplyModel.WorkingDef best = null;
            foreach (var cnd in cands)
                if (cnd.LoadOrder <= p.LoadOrder && (best == null || cnd.LoadOrder > best.LoadOrder))
                    best = cnd;
            if (best == null || best.NodeId == id)
            {
                cache[id] = p.Elements;
                return p.Elements;
            }
            var parentElems = Resolve(best.NodeId, cache);
            var merged = new Dictionary<string, string>(parentElems ?? new Dictionary<string, string>());
            foreach (var kv in p.Elements) merged[kv.Key] = kv.Value;
            cache[id] = merged;
            return merged;
        }
    }
}
