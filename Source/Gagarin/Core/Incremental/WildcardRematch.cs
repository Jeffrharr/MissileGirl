// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// WildcardRematch.cs (Piece D — Milestone 2a: superset-safe dirty set)
//
// Contains: a pure (System.Xml only) re-test of patch xpaths against the current raw def
// bodies, and the one direction of wildcard-membership flip that the M1 structural seeds
// miss — newly matched defs.
//
// Used for: closing the wildcard-flip hazard so the dirty set is a true SUPERSET of the
// nodes a recompute must touch (M1 is only a sound LOWER bound). A wildcard matches by
// STRUCTURE, so when a changed mod's patch predicate changes it can match defs it did not
// before. Those new matches may be otherwise-unchanged defs, so none of M1's seeds (changed
// def bodies / a changed mod's OLD matched nodes / reorder) catch them. Left unseeded, a
// later recompute would silently keep a stale resolved value for them — a subset error,
// which is exactly the failure mode this guards against.
//
// CRITICAL — re-test the CURRENT predicate, not the baseline one. The hazard is a *widened*
// predicate, and the widened xpath exists only in the changed mod's CURRENT patch op; the
// baseline graph still holds the OLD (narrow) xpath. So the driver reflects each changed
// mod's current patch xpaths (keyed by the same stable patch id as the capture) and passes
// them here; we evaluate THOSE against the current bodies and subtract each patch's baseline
// match set (looked up by id in the graph). Re-testing the graph's stored xpath instead
// would be blind to exactly the predicate change this milestone exists to catch.
//
// Why only NEW matches? The flip directions split cleanly:
//   * A changed mod's patch widening its match set -> newly matched defs. ADDITIVE: these
//     are the nodes nothing else seeds, so this is the gap M2a fills.
//   * A changed mod's patch narrowing its match set (or being removed) -> its OLD matched
//     nodes. Already covered by DirtySetComputer Seed 2 (a changed mod dirties every node
//     its baseline patches modified).
//   * An UNCHANGED mod's wildcard flipping because a def's body moved -> only a CHANGED def
//     can flip it, and a changed def is already dirty (Seed 1). So unchanged mods add no new
//     nodes and the driver does not pass their predicates. (Piece C reached the same
//     conclusion.)
//
// Why pure: matching is just XPath over XML, so this needs no RimWorld types and is unit-
// tested offline. The RimWorld-facing DirtySetDiagnostic supplies the candidate raw def
// nodes (from Context.XmlAssets) and the changed mods' current (patchId -> xpath) map; this
// file owns the load-bearing matching logic.

using System.Collections.Generic;
using System.Xml;

namespace Gagarin
{
    public static class WildcardRematch
    {
        // Builds the <Defs> document the xpaths are evaluated against, ONCE, from the
        // candidate raw def nodes. Built once and reused across every patch xpath: a single
        // changed mod can declare many patches, and re-importing ~16k def bodies per patch
        // would dominate the cost. Patch xpaths are written relative to the document root
        // (e.g. "Defs/ThingDef[...]"), so we mirror that root here.
        public static XmlDocument BuildCandidateDocument(IEnumerable<XmlNode> candidateDefs)
        {
            var doc = new XmlDocument();
            var defs = doc.CreateElement("Defs");
            doc.AppendChild(defs);
            if (candidateDefs != null)
                foreach (var node in candidateDefs)
                    if (node != null)
                        defs.AppendChild(doc.ImportNode(node, true));
            return doc;
        }

        // The def ids an xpath currently matches in a prebuilt candidate document. Each
        // matched node is mapped to the id of the top-level def that contains it (via the
        // same keying the capture uses), so a patch that targets a child element still
        // attributes to its def. The keyer is passed in so its by-reference cache is shared
        // across every xpath run against the same document.
        public static HashSet<string> MatchedDefIds(XmlDocument candidateDoc, string xpath,
            ProvenanceGraph keyer)
        {
            var result = new HashSet<string>();
            if (candidateDoc == null || string.IsNullOrEmpty(xpath))
                return result;

            XmlNodeList matched;
            try
            {
                matched = candidateDoc.SelectNodes(xpath);
            }
            catch
            {
                return result; // malformed/unsupported xpath matches nothing
            }
            if (matched == null)
                return result;

            foreach (XmlNode m in matched)
            {
                string id = keyer.KeyForNode(m);
                if (!string.IsNullOrEmpty(id) && !IsDocumentPath(id))
                    result.Add(id);
            }
            return result;
        }

        // A KeyForNode result that is a positional document path rather than a stable def
        // identity. KeyForNode falls back to a document path ("Defs", "Defs/ThingDef[3]/comps")
        // only for nodes that live outside any top-level def (e.g. a Defs-root add); the path
        // always begins with the <Defs> root wrapper, whereas a real def id begins with the
        // def TYPE ("ThingDef/Steel", "ThingDef@BuildingBase"). These paths are not stable
        // identities, so comparing them against a baseline match set would produce phantom
        // flips — exclude them.
        private static bool IsDocumentPath(string id)
            => id == "Defs" || id.StartsWith("Defs/", System.StringComparison.Ordinal);

        // Convenience overload (used by tests and Flips): builds a fresh document and keyer.
        public static HashSet<string> MatchedDefIds(IEnumerable<XmlNode> candidateDefs, string xpath)
            => MatchedDefIds(BuildCandidateDocument(candidateDefs), xpath, new ProvenanceGraph());

        // Def ids whose membership in this xpath flipped vs the baseline match set — i.e.
        // newly matched or no longer matched (the symmetric difference). Kept for offline
        // analysis/tests; the dirty-set path uses NewlyMatched, which wants only the additive
        // direction.
        public static HashSet<string> Flips(IEnumerable<XmlNode> candidateDefs, string xpath,
            IEnumerable<string> baselineMatched)
        {
            var flips = MatchedDefIds(candidateDefs, xpath);
            flips.SymmetricExceptWith(baselineMatched ?? new string[0]);
            return flips;
        }

        // The superset-safety additions for the dirty set: every def id a changed mod's
        // CURRENT patch predicate now matches that was NOT in that patch's baseline match set.
        // These are the otherwise-unchanged defs a widened predicate newly captures (see file
        // header) — the one direction M1's structural seeds miss. The caller folds these into
        // the seed set before the inheritance closure, so a newly-matched abstract parent
        // still fans out to its descendants.
        //
        // currentXpathByPatchId: the changed mods' current patch ops, keyed by the same stable
        // patch id the capture used, so each pairs to its baseline graph edge. A patch id with
        // no baseline edge (a brand-new patch) has an empty baseline, so all of its current
        // matches are additions.
        public static HashSet<string> NewlyMatched(DependencyGraphData graph,
            IDictionary<string, string> currentXpathByPatchId, XmlDocument candidateDoc)
        {
            var added = new HashSet<string>();
            if (graph == null || currentXpathByPatchId == null
                || currentXpathByPatchId.Count == 0 || candidateDoc == null)
                return added;

            // Baseline match set per patch id, unioned across that id's edges.
            var baselineByPatchId = new Dictionary<string, HashSet<string>>(System.StringComparer.Ordinal);
            foreach (var e in graph.PatchEdges)
            {
                if (e.PatchId == null)
                    continue;
                if (!baselineByPatchId.TryGetValue(e.PatchId, out var set))
                    baselineByPatchId[e.PatchId] = set = new HashSet<string>();
                set.UnionWith(e.MatchedNodeIds);
            }

            var keyer = new ProvenanceGraph();
            // Distinct predicates share match sets (it depends only on xpath + the fixed
            // candidate doc), so memoize the current match set per xpath. Baseline subtraction
            // is per patch id.
            var nowByXpath = new Dictionary<string, HashSet<string>>(System.StringComparer.Ordinal);

            foreach (var kv in currentXpathByPatchId)
            {
                string xpath = kv.Value;
                if (string.IsNullOrEmpty(xpath))
                    continue;

                if (!nowByXpath.TryGetValue(xpath, out var now))
                    nowByXpath[xpath] = now = MatchedDefIds(candidateDoc, xpath, keyer);
                if (now.Count == 0)
                    continue;

                baselineByPatchId.TryGetValue(kv.Key, out var baseline);
                foreach (var id in now)
                    if (baseline == null || !baseline.Contains(id))
                        added.Add(id);
            }
            return added;
        }
    }
}
