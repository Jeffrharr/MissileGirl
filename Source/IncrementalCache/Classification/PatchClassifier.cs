// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Gagarin.IncrementalCache.Classification
{
    // Classifies each patchEdge as "identity" (anchored on a specific def, cheaply indexable
    // by defName) or "wildcard" (matches by structure/attribute and must be re-tested against
    // changed nodes during incremental recompute).
    //
    // Why this split matters: the future incremental cache answers "which patches now match
    // this changed def?" on every def edit. Identity patches collapse to an O(1) dictionary
    // lookup keyed by nodeId. Wildcard patches have no such anchor, so each one must be
    // re-evaluated against candidate nodes — the expensive path. The identity:wildcard ratio
    // therefore decides whether incremental recompute is worth building at all.
    public static class PatchClassifier
    {
        // An identity anchor is a defName predicate inside an XPath step, e.g.
        //   Defs/ThingDef[defName="Steel"]/statBases
        //   /Defs/ThingDef[defName = 'Steel']
        // We accept single or double quotes and surrounding whitespace. The defName must be a
        // literal equality test, not a function call or a partial match.
        private static readonly Regex DefNamePredicate = new Regex(
            @"defName\s*=\s*(?:""(?<name>[^""]+)""|'(?<name>[^']+)')",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // Tokens that betray a structural / set-based match: descendant axis, wildcards,
        // positional or text predicates, contains()/starts-with(), or attribute matches other
        // than defName. Any of these means the patch can match nodes that don't exist yet, so
        // it cannot be reduced to a defName key. This is a conservative blocklist: when in
        // doubt we classify as wildcard, because mis-labelling a wildcard as identity would
        // silently drop patches during incremental recompute (a correctness bug), whereas the
        // reverse only costs a little extra re-testing.
        private static readonly Regex WildcardSignal = new Regex(
            @"(//)" +                       // descendant-or-self axis
            @"|(\*)" +                      // element/attribute wildcard
            @"|(\[\s*\d)" +                 // positional predicate, e.g. [1]
            @"|(text\s*\(\s*\))" +          // text() predicate
            @"|(contains\s*\()" +           // contains(...)
            @"|(starts-with\s*\()" +        // starts-with(...)
            @"|(\.\.)" +                    // parent axis navigation
            @"|(\b(following|preceding|ancestor|descendant)(-sibling)?::)", // explicit axes
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static PatchClassificationResult Classify(PatchEdge edge)
        {
            if (edge == null) throw new ArgumentNullException(nameof(edge));

            var result = new PatchClassificationResult
            {
                PatchId = edge.PatchId,
                SourceMod = edge.SourceMod
            };

            string xpath = edge.Xpath ?? string.Empty;

            // 1. Prefer defName predicates parsed straight from the XPath: they are the anchor.
            var anchoredNames = ExtractDefNames(xpath);

            // 2. A defName anchor only counts as identity if the XPath has no structural signal
            //    that could broaden the match beyond that single def (e.g. a descendant axis or
            //    wildcard combined with a defName test could still fan out).
            bool hasStructuralSignal = WildcardSignal.IsMatch(xpath);

            if (anchoredNames.Count > 0 && !hasStructuralSignal)
            {
                result.Classification = Classification.Identity;
                result.TargetDefNames = anchoredNames;
                result.Predicate = null;
                return result;
            }

            // 3. No usable defName anchor in the XPath. Fall back to the resolved match set from
            //    Piece A: if the patch demonstrably touched exactly one node and that node's id
            //    carries a defName, we can still index it. This catches patches whose XPath is
            //    odd but which empirically resolved to a single def. We only trust this when
            //    there is no structural signal, for the same conservative reason as above.
            if (!hasStructuralSignal && TryDeriveSingleNodeIdentity(edge, out var derivedNames))
            {
                result.Classification = Classification.Identity;
                result.TargetDefNames = derivedNames;
                result.Predicate = null;
                return result;
            }

            // 4. Everything else is a wildcard. Keep the XPath as the predicate to re-test later.
            result.Classification = Classification.Wildcard;
            result.Predicate = string.IsNullOrEmpty(xpath) ? "(empty)" : xpath;
            return result;
        }

        // Pull every distinct defName="..." literal out of an XPath, preserving first-seen order.
        private static List<string> ExtractDefNames(string xpath)
        {
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in DefNamePredicate.Matches(xpath))
            {
                string name = m.Groups["name"].Value;
                if (seen.Add(name))
                    names.Add(name);
            }
            return names;
        }

        // If the patch resolved to exactly one node id (e.g. "ThingDef/Steel"), treat the part
        // after the last '/' as the defName. Multi-node match sets are inherently wildcard-ish
        // and are left for the wildcard path.
        private static bool TryDeriveSingleNodeIdentity(PatchEdge edge, out List<string> names)
        {
            names = null;
            var ids = edge.MatchedNodeIds;
            if (ids == null || ids.Count != 1)
                return false;

            string id = ids[0];
            if (string.IsNullOrEmpty(id))
                return false;

            int slash = id.LastIndexOf('/');
            string defName = slash >= 0 ? id.Substring(slash + 1) : id;
            if (string.IsNullOrEmpty(defName))
                return false;

            names = new List<string> { defName };
            return true;
        }
    }
}
