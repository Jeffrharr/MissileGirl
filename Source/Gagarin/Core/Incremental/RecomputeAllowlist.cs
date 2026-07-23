// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// RecomputeAllowlist.cs (recompute-fidelity ALLOWLIST — the safe-by-default MVP gate)
//
// Contains: a PURE static check that decides, from the dependency graph and the dirty+context sets,
// whether the sub-doc recompute can be trusted for THIS load. It is the INVERSE of a blocklist: the
// default answer is "fall back to a full rebuild", and we take the incremental path ONLY when every
// op that produces a dirty def is one of a small set of patterns proven faithful over a sub-doc.
//
// Why allowlist, not blocklist: DefRecompute re-applies every running mod's PatchOperations over a
// tiny <Defs> sub-doc holding only the dirty defs (+ SubDocExpander's sequence context + inheritance
// ancestors). That is faithful for ops whose effect is a pure, local function of the def they modify,
// but NOT for an op whose effect/branch depends on reading state absent from the sub-doc (cross-def
// conditional), whose match is positional (re-selects a different node in a smaller doc), or whose
// semantics we simply have not modelled (custom ops, capture gaps). A blocklist ("recompute unless we
// recognise a hazard") ships wrong data on any UNMODELLED hazard — a false negative with no full
// rebuild in production to catch it. An allowlist makes the unmodelled case SAFE: unknown => fall back
// => full rebuild => correct, just slower. Each newly-proven pattern only WIDENS the fast path; it can
// never introduce a correctness regression.
//
// v1 allowlist (exactly the patterns the live recompute gate has proven faithful — keeps every green
// test mode green and declines the cross-def gap, TestMods CASE 6):
//   (1) Pure LEAF ops {Add, Replace, Remove, AttributeSet/Add/Remove, SetName} with a NON-positional
//       xpath. A per-node leaf effect is identical and local, so a wildcard/defName selector is safe
//       even though it matches fewer nodes in the sub-doc (each matched dirty def still gets the right
//       local effect). Only a POSITIONAL def-selector ([n]/last()/position()/sibling-axis) can
//       re-match a different node in a smaller doc, so those fall back. (Covers CASE 1 + CASE 2.)
//   (2) SEQUENCE children — trusted because they are only reached after SubDocExpander pulled their
//       siblings into context (it would have set needsFullRebuild otherwise), so the sequence runs to
//       completion exactly as the full load did. The child op itself must still be a safe leaf.
//       (Covers CASE 3/4.)
//   (3) SAME-DEF / in-sub-doc CONDITIONALS — a conditional branch is allowed iff its parent's READ set
//       (the test's MatchedNodeIds) is wholly present in the sub-doc, so the test re-evaluates to the
//       same branch. A cross-def conditional (reads a def absent from the sub-doc) is NOT allowlisted.
//       (Covers CASE 5; declines CASE 6.)
// Everything else falls back with a category so the cause is logged and the backlog is frequency-
// ranked: unknown-op-kind (custom/third-party ops not yet proven safe), positional-xpath,
// conditional-cross-def, dynamic-op (an op generated at runtime by an
// opaque enclosing op, attributed as "...generated[N]"), capture-gap (an empty/missing op type on a
// producing edge — an op the capture could not attribute).
//
// Producing edges: an op produces a dirty def D if it modifies D OR any of D's inheritance ANCESTORS
// (D inherits the patched ancestor's value). So we check every edge whose ModifiedNodeIds touches
// dirty ∪ ancestors(dirty). Parent conditional TEST edges are skipped (they do not modify anything;
// their .match/.nomatch children carry the effect and are checked there).
//
// Why pure: graph/set/string analysis over DependencyGraphData, no RimWorld types, unit-tested offline
// like SubDocExpander / DirtySetComputer. The live --expect-recompute-gap (declines) and default
// (admits) runs are the end-to-end proof.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Gagarin
{
    public static class RecomputeAllowlist
    {
        // Pure-leaf modifiers: effect is local to the matched node, so faithful over a sub-doc that
        // contains that node.
        //
        // Keyed by FULLY-QUALIFIED type name (namespace + class), not the bare simple name: the
        // capture side (ProvenanceRecorder.RecordPatch/RecordFindModEdge) now records
        // GetType().FullName specifically so this allowlist can't be spoofed by an unrelated mod
        // shipping its own class that happens to share a base-game op's simple name (e.g. some
        // other mod's "PatchOperationAddIf" with real doc-content-dependent semantics would have
        // silently matched a bare "PatchOperationAddIf" string).
        private static readonly HashSet<string> SafeLeafOps = new HashSet<string>(StringComparer.Ordinal)
        {
            "Verse.PatchOperationAdd",
            "Verse.PatchOperationReplace",
            "Verse.PatchOperationRemove",
            "Verse.PatchOperationAttributeSet",
            "Verse.PatchOperationAttributeAdd",
            "Verse.PatchOperationAttributeRemove",
            "Verse.PatchOperationSetName",
            // Decompiled 2026-07-14 (edge-cases loop fallback for memegoddess.tdpack): inserts the
            // xpath-matched node's own new sibling(s) via InsertBefore/InsertAfter anchored on the
            // XmlNode REFERENCE SelectNodes returned -- not a numeric child index -- so, exactly like
            // Add/Replace/Remove, its effect is local to whichever node the (non-positional) xpath
            // matched, regardless of how many other nodes/defs are absent from a smaller sub-doc.
            // Still gated by IsUnsafePositional below: an xpath using [n]/last()/position()/sibling-
            // axis to pick the anchor really could re-select a different node in a smaller doc, so
            // that case still declines. NOT yet covered: an Insert anchored so its new sibling(s)
            // become entirely new TOP-LEVEL DEFS (mirroring PatchOperationAdd's `/Defs`-root
            // pattern) would need the same patchInjectedOwners provenance PatchOperation_Patch's
            // RecordAddedChildren gives Add (CASE 14) to stay correct if the owning mod is itself
            // unchanged and the new def is only dirtied via inheritance fan-out -- RecordAddedChildren
            // is not wired for Insert. Live-observed case (memegoddess.tdpack#2) inserts a `<li>`
            // sibling inside an existing def's `specialDesignatorClasses` list, not a new top-level
            // def, so this gap doesn't apply yet; revisit if/when a live run actually exercises it.
            "Verse.PatchOperationInsert",
            // Decompiled 2026-07-10 (seed-7211's fallback reason named this op): a plain
            // PatchOperationPathed leaf -- for every xpath match, ensures a "modExtensions"
            // child element exists and imports the configured node's children into it. Same
            // shape as PatchOperationAdd (local mutation under each matched node, no cross-def
            // read, no positional/doc-content dependence), just targeting a fixed child element
            // instead of appending directly.
            "Verse.PatchOperationAddModExtension",
            // The following are third-party subclasses of the above ops, gated by a check that
            // is load-invariant (a static settings field or ModsConfig.IsActive, never doc
            // content) before delegating to an UNMODIFIED base ApplyWorker -- so their capture
            // (matched/modified node ids, via the same generic SelectNodes/SelectSingleNode
            // hooks every PatchOperationPathed subclass goes through) is exactly as faithful as
            // the base op's. Verified by decompiling each (2026-07-09), reference mods:
            //   AnomalyPatch.PatchOperationAddIf     : PatchOperationAdd,     gated on a static
            //   AnomalyPatch.PatchOperationReplaceIf : PatchOperationReplace, AnomalyPatchSettings
            //   AnomalyPatch.PatchOperationRemoveIf  : PatchOperationRemove, field (unchanged for
            //                                          the whole load) -- "1trickPwnyta's Anomaly
            //                                          Patch" (anomalypatch.1trickPwnyta)
            //   TTPF.PatchOperationEditResearch      : PatchOperationPathed, gated on
            //                                          ModsConfig.IsActive (doesRequire), then
            //                                          mutates only its own matched node's fixed
            //                                          research-def children (no cross-def read)
            //                                          -- "Tech Tree Patch Framework"
            //                                          (GonDragon.TTPF)
            "AnomalyPatch.PatchOperationAddIf",
            "AnomalyPatch.PatchOperationReplaceIf",
            "AnomalyPatch.PatchOperationRemoveIf",
            "TTPF.PatchOperationEditResearch",
            // Decompiled 2026-07-14 (edge-cases loop fallback for sicafe.chair.overhaul):
            // Verse.PatchOperationTest.ApplyWorker is `return xml.SelectSingleNode(xpath) != null;`
            // -- a pure read, it never touches the XmlDocument. PatchOperation_Patch's capture
            // records matched==modified for every successful pathed op (see its comment on why:
            // that's true for every OTHER PatchOperationPathed subclass, and SubDocExpander relies
            // on it to pull the tested node into sequence-sibling context so the real Sequence.Apply
            // replay sees the same node PatchOperationTest gated on). So Test can show up as a
            // "producing" edge for a dirty def it merely tested, never mutated. Since it contributes
            // zero content change to that def either way, admitting it as a safe leaf is a true
            // no-op from the recomputed def's perspective -- and it still gets the same
            // IsUnsafePositional guard below as any other pathed op, so a positional/cross-def Test
            // (which really could gate a sequence differently in a smaller sub-doc) still declines.
            "Verse.PatchOperationTest",
            // Decompiled 2026-07-14 (edge-cases loop fallback for dubwise.dubsbadhygiene): same
            // gated-load-invariant shape as the AnomalyPatch.*If trio above --
            // DubsBadHygieneMod.CentralHeating_Active is a static settings field (never doc
            // content); when true, ApplyWorker no-ops (returns true without touching xml), else it
            // delegates unmodified to base PatchOperationAdd.ApplyWorker.
            "DubsBadHygiene.PatchOperationAddDesignator",
        };

        // A numeric index predicate or last()/position() call makes def-SELECTION unstable when the
        // same xpath is re-run over a smaller sub-doc -- UNLESS it indexes within an already
        // uniquely-anchored def's own children (see DefNameAnchor below). Categories: positional-xpath.
        private static readonly Regex NumericIndexOrPositionFn = new Regex(
            @"\[\s*\d+\s*\]|\blast\s*\(|\bposition\s*\(", RegexOptions.Compiled);

        // A sibling axis re-selects a DIFFERENT node than the current context node (a sibling of it),
        // so it is never made safe by an anchor on an earlier step -- the anchor scopes the current
        // node's own descendants, not its siblings.
        private static readonly Regex SiblingAxis = new Regex(
            @"following-sibling|preceding-sibling", RegexOptions.Compiled);

        // A step that walks back UP out of the current node (parent:: / ancestor(-or-self)::/ "..")
        // breaks any anchor established by an earlier step: subsequent steps are no longer scoped to
        // that anchored def's own descendants, so re-anchoring resets.
        private static readonly Regex ScopeBreakStep = new Regex(
            @"^\s*\.\.\s*$|^\s*parent::|^\s*ancestor(-or-self)?::", RegexOptions.Compiled);

        // A defName/Name predicate anchors the def SELECTION itself to a stable identity, independent
        // of which other defs are present in a smaller sub-doc. A positional predicate on a step at or
        // after such an anchor (and not past an intervening scope-break step) only indexes within that
        // already-uniquely-selected def's own descendants (e.g. .../ThingDef[defName="A"]/comps/li[2])
        // — safe. One before/without an anchor, or reached via a sibling axis, or after a scope-break
        // step (e.g. .../ThingDef[defName="A"]/../ThingDef[3]) participates in selecting a DIFFERENT
        // def and is genuinely unsafe.
        private static readonly Regex DefNameAnchor = new Regex(
            @"\[\s*(defName|Name)\s*=", RegexOptions.Compiled);

        // Splits an xpath into '/'-separated steps, ignoring '/' characters nested inside '[...]'
        // predicates (predicates can themselves contain path expressions, e.g. "[defName='A']").
        private static IEnumerable<string> SplitSteps(string xpath)
        {
            var steps = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < xpath.Length; i++)
            {
                char c = xpath[i];
                if (c == '[') depth++;
                else if (c == ']') depth--;
                else if (c == '/' && depth == 0)
                {
                    steps.Add(xpath.Substring(start, i - start));
                    start = i + 1;
                }
            }
            steps.Add(xpath.Substring(start));
            return steps;
        }

        // Structural (step-by-step) check, not a string-position heuristic: a lexically-earlier
        // defName/Name anchor does NOT make a later positional/sibling predicate safe unless it is
        // actually an ancestor step of that predicate with no intervening scope-break (".."/parent::/
        // ancestor::) or sibling-axis step in between.
        private static bool IsUnsafePositional(string xpath)
        {
            if (string.IsNullOrEmpty(xpath))
                return false;

            bool anchorInScope = false;
            foreach (string step in SplitSteps(xpath))
            {
                if (step.Length == 0)
                    continue;

                if (ScopeBreakStep.IsMatch(step))
                {
                    anchorInScope = false;
                    continue;
                }

                if (SiblingAxis.IsMatch(step))
                    return true;

                bool stepAnchored = DefNameAnchor.IsMatch(step);
                if (NumericIndexOrPositionFn.IsMatch(step) && !anchorInScope && !stepAnchored)
                    return true;

                if (stepAnchored)
                    anchorInScope = true;
            }
            return false;
        }

        // Returns true when the sub-doc recompute is trusted for this load. When false, blockCategory
        // / blockReason explain why (for the metrics backlog) and the caller must full-rebuild.
        //
        // relevantTargets (issue #75) exposes the PRIOR-graph-based dirty ∪ ancestors(dirty) set this
        // method computes internally (see below), so a caller can cross-check it against
        // DefRecompute.Recompute's separate CURRENT-raw-XML ParentName walk (its
        // ancestorIdsFromRawXml out param) — the two can diverge whenever a dirty def's ParentName
        // changed since the prior load (see the parentOf comment below for why). Assigned on every
        // return path so a caller always has something to compare against, regardless of which branch
        // admitted or declined.
        public static bool CanRecompute(
            DependencyGraphData graph,
            ICollection<string> dirtyIds,
            ICollection<string> contextIds,
            out string blockReason,
            out string blockCategory,
            out HashSet<string> relevantTargets)
        {
            blockReason = null;
            blockCategory = null;

            var dirty = AsSet(dirtyIds);
            if (dirty.Count == 0)
            {
                relevantTargets = dirty; // nothing dirty, so nothing is relevant either
                return true; // nothing to recompute — trivially safe (the splice changes nothing)
            }

            if (graph == null)
            {
                blockCategory = "capture-gap";
                blockReason = "no dependency graph available to verify recompute safety";
                relevantTargets = new HashSet<string>(StringComparer.Ordinal); // no graph to derive ancestors from
                return false;
            }

            var context = contextIds != null
                ? new HashSet<string>(contextIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            // childNodeId -> parentNodeId, to walk a dirty def UP to its inheritance ancestors: an op
            // modifying an ancestor produces the dirty descendant's value, so it must be allowlisted too.
            // Built from the PRIOR load's captured graph (see DirtySetGate.RunRecompute's graphPath
            // comment) -- NOT the current raw XML. DefRecompute.AddAncestorsFromRawXml walks the
            // current ParentName chain instead, so the two can diverge if a dirty def's ParentName
            // changed since the prior load; see that method's comment for the consequence and why
            // it's currently caught downstream rather than silently wrong.
            var parentOf = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (GraphInheritanceEdge e in graph.InheritanceEdges)
                if (!string.IsNullOrEmpty(e.ChildNodeId) && !string.IsNullOrEmpty(e.ParentNodeId))
                    parentOf[e.ChildNodeId] = e.ParentNodeId;

            // relevantTargets = dirty ∪ ancestors(dirty). An edge produces a dirty def iff it modifies
            // one of these. Assigned into the out param directly (issue #75) so callers can cross-
            // check it against DefRecompute's separate current-raw-XML ancestor walk.
            relevantTargets = new HashSet<string>(dirty, StringComparer.Ordinal);
            foreach (string d in dirty)
            {
                string cur = d;
                while (parentOf.TryGetValue(cur, out string parent) && relevantTargets.Add(parent))
                    cur = parent;
            }

            // The recompute sub-doc physically holds dirty ∪ context PLUS each one's transitive
            // inheritance ancestors -- DefRecompute's own AddAncestors call (step 2) pulls ancestor
            // raw bodies in too, because real XmlInheritance resolution needs them. A conditional
            // whose test reads an ANCESTOR of a dirty/context def (e.g. an abstract Name-based
            // template like ThingDef@BasePawn) is therefore already reproducible, even though the
            // ancestor is neither itself dirty nor a sequence-sibling. Missing this made every such
            // ancestor-scoped conditional wrongly fall back as "conditional-cross-def" — 2026-07-14,
            // edge-cases loop (petetimessix.simplesidearms#0 testing ThingDef@BasePawn, an ancestor
            // of the actual dirty Pawn-derived def).
            var subDoc = new HashSet<string>(relevantTargets, StringComparer.Ordinal);
            foreach (string id in context)
            {
                subDoc.Add(id);
                string cur = id;
                while (parentOf.TryGetValue(cur, out string parent) && subDoc.Add(parent))
                    cur = parent;
            }

            // Conditional id -> its own test READ set, so a branch child can be checked against its
            // IMMEDIATE gating conditional. Keyed by every conditional edge's OWN PatchId — including a
            // conditional nested inside another conditional's branch, whose id (e.g. "mod#1.match") also
            // looks like a branch child of its OUTER parent. That outer-branch-child-ness is irrelevant
            // here: what matters is that THIS op is a Conditional, so it has its own read set to record.
            var conditionalReads = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (GraphPatchEdge edge in graph.PatchEdges)
                if (IsConditional(edge.OperationType))
                    conditionalReads[edge.PatchId] = edge.MatchedNodeIds;

            // Tracks which ids in relevantTargets (dirty ∪ ancestors) were actually produced by a
            // real (non-conditional-test) edge during the loop below. An id left OUT of this set
            // falls through to the nodeById/PatchInjectedOwners check just before the final return —
            // the "no producing edge found" case is only trivially safe for a genuinely unpatched raw
            // def; a patch-injected node with the same (empty) producing-edge shape needs that extra
            // check to tell the two apart. Originally only tracked `dirty` (issue #73); widened to
            // `relevantTargets` (issue #81) so an unrecoverable patch-injected ANCESTOR is caught the
            // same way an unrecoverable patch-injected dirty def is — DefRecompute's AddAncestors
            // pulls an ancestor's raw body into the sub-doc too, so an unreproducible ancestor
            // corrupts the recompute exactly like an unreproducible dirty def would.
            var produced = new HashSet<string>(StringComparer.Ordinal);

            foreach (GraphPatchEdge edge in graph.PatchEdges)
            {
                // Skip conditional TEST edges entirely — they modify nothing (their spurious self-mark is
                // not a real effect); the effect is in their .match/.nomatch children, checked below. This
                // must skip EVERY conditional, not just top-level ones: a nested conditional's own id can
                // also look like a branch child of its outer parent (see conditionalReads above), but it
                // is still a test edge, not a producing edge.
                if (IsConditional(edge.OperationType))
                    continue;

                if (!TouchesRelevant(edge.ModifiedNodeIds, relevantTargets))
                    continue; // this op does not produce any dirty def — irrelevant

                if (edge.ModifiedNodeIds != null)
                    foreach (string id in edge.ModifiedNodeIds)
                        if (relevantTargets.Contains(id))
                            produced.Add(id);

                // Capture gap: an op the capture could not attribute a kind to. Cannot prove safe.
                if (string.IsNullOrEmpty(edge.OperationType))
                {
                    Block("capture-gap",
                        $"producing op {edge.PatchId ?? "<unknown>"} has no recorded operation type", out blockReason, out blockCategory);
                    return false;
                }

                // Capture gap: an UNATTRIBUTED op. ProvenanceRecorder buckets any op it could not
                // map to a stable patchId at all (no enclosing op to attribute it to) under
                // "unindexed#{type}". Such an op keeps a real OperationType (e.g. PatchOperationReplace)
                // so without this it would sail through the safe-leaf test below — but we can't even say
                // which mod it belongs to, let alone reproduce it. Capture gap -> fall back.
                if (IsUnattributed(edge.PatchId))
                {
                    Block("capture-gap",
                        $"producing op {edge.PatchId} is unattributed (no enclosing op) — cannot prove recompute-safe", out blockReason, out blockCategory);
                    return false;
                }

                // A DYNAMICALLY GENERATED op (created during an enclosing op's Apply, now attributed to
                // it as "{parentId}.generated[N]"). It carries a real op type + sourceMod, but it is
                // still recompute-unsafe: we cannot reproduce the OPAQUE GENERATOR over a sub-doc (it
                // may read cross-def state or emit different children there). Distinct category from
                // capture-gap because the RISK IS ATTRIBUTED to a mod (which is what the deterministic
                // per-mod serve rule needs) — it just isn't admittable until the generator is modelled.
                if (IsGenerated(edge.PatchId))
                {
                    Block("dynamic-op",
                        $"producing op {edge.PatchId} is dynamically generated by an opaque op — cannot prove recompute-safe", out blockReason, out blockCategory);
                    return false;
                }

                // Not a known-safe leaf op (custom op, Insert, AddIf/ReplaceIf/RemoveIf, FindMod, …).
                if (!SafeLeafOps.Contains(edge.OperationType))
                {
                    Block("unknown-op-kind",
                        $"producing op {edge.PatchId} is {edge.OperationType}, not a proven-safe leaf op", out blockReason, out blockCategory);
                    return false;
                }

                // Positional def-selection re-matches a different node in a smaller sub-doc.
                if (edge.Xpath != null && IsUnsafePositional(edge.Xpath))
                {
                    Block("positional-xpath",
                        $"producing op {edge.PatchId} has a positional xpath ({edge.Xpath})", out blockReason, out blockCategory);
                    return false;
                }

                // A conditional branch's safe-leaf effect is still only faithful if the parent test
                // re-evaluates to the same branch — i.e. its read set is wholly in the sub-doc.
                if (IsBranchChild(edge.PatchId))
                {
                    string parentId = BranchParentId(edge.PatchId, conditionalReads);
                    if (parentId == null)
                    {
                        Block("capture-gap",
                            $"conditional branch {edge.PatchId} has no captured parent conditional", out blockReason, out blockCategory);
                        return false;
                    }
                    if (!ReadSetInSubDoc(conditionalReads[parentId], subDoc))
                    {
                        Block("conditional-cross-def",
                            $"cross-def conditional {parentId} reads a def absent from the sub-doc; its branch {edge.PatchId} produces a dirty def", out blockReason, out blockCategory);
                        return false;
                    }
                }
                // else: a top-level or sequence-child safe leaf op with a stable xpath — allowlisted.
                // (Sequence children are only reached because SubDocExpander already pulled their
                // siblings into context; otherwise it would have set needsFullRebuild upstream.)
            }

            // Every relevant id (dirty ∪ ancestors) with a producing edge has been allowlisted above.
            // What remains is any relevant id with ZERO producing edges — today's fallthrough treats
            // that as trivially safe (a genuinely unpatched raw def/ancestor carries its own body
            // verbatim, nothing to recompute). But a patch-injected node (no raw SourceFile — see
            // ProvenanceGraph's patchInjectedOwners comment) can ALSO show zero producing edges: its
            // creating op's captured edge names the `Defs`-root anchor it matched, never the child's
            // own id (the same target-vs-content asymmetry DefRecompute's step 3c works around for
            // PatchOperationAdd). When the owning mod is additionally missing from
            // graph.PatchInjectedOwners (the only fallback attribution for that shape), we have no
            // way to reproduce or even attribute this def over a sub-doc — decline rather than admit
            // by silence. A relevant id with NO GraphNode entry at all is left on the existing "admit"
            // path: an id truly unknown to the graph is not evidence of a patch-injected node either
            // way. Originally checked only `dirty` (issue #73); widened to `relevantTargets` (issue
            // #81) now that RegisterAbstract threads a real SourceFile through for genuine raw-XML
            // abstract bases (see its comment), so this no longer misfires on every ordinary ancestor
            // template.
            if (produced.Count < relevantTargets.Count)
            {
                var nodeById = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
                foreach (GraphNode node in graph.Nodes)
                    if (!string.IsNullOrEmpty(node.Id))
                        nodeById[node.Id] = node;

                foreach (string id in relevantTargets)
                {
                    if (produced.Contains(id))
                        continue;

                    if (!nodeById.TryGetValue(id, out GraphNode node))
                        continue; // unknown to the graph entirely — not evidence of patch-injection

                    if (!string.IsNullOrEmpty(node.SourceFile))
                        continue; // has a real raw body — genuinely unpatched, trivially safe

                    if (graph.PatchInjectedOwners.ContainsKey(id))
                        continue; // attributed to its owning mod — recoverable

                    Block("unrecoverable-patch-injected",
                        $"relevant id {id} has no producing edge, no raw SourceFile, and no PatchInjectedOwners attribution — cannot prove recompute-safe",
                        out blockReason, out blockCategory);
                    return false;
                }
            }

            return true; // every producing op is allowlisted
        }

        // Ancestor ids DefRecompute's current-raw-XML ParentName walk pulled in
        // (ancestorIdsFromRawXml) that CanRecompute's relevantTargets (this method's own
        // prior-graph InheritanceEdges walk) never vetted -- see CanRecompute's comment for why the
        // two can diverge (issue #75). Pulled out of DirtySetGate.RunRecompute as a pure function so
        // the set-subtraction itself is offline-testable, independent of the RimWorld-coupled
        // recompute path it's normally called from. relevantTargets is never null here: CanRecompute
        // assigns it on every return path, and the one call site only reaches this after CanRecompute
        // has already succeeded.
        public static List<string> ComputeAncestorDivergence(
            List<string> ancestorIdsFromRawXml, HashSet<string> relevantTargets)
        {
            var divergence = new List<string>();
            if (ancestorIdsFromRawXml == null)
                return divergence;
            foreach (string id in ancestorIdsFromRawXml)
                if (!relevantTargets.Contains(id))
                    divergence.Add(id);
            return divergence;
        }

        private static void Block(string category, string reason, out string blockReason, out string blockCategory)
        {
            blockCategory = category;
            blockReason = reason;
        }

        private static bool TouchesRelevant(List<string> modifiedNodeIds, HashSet<string> relevant)
        {
            if (modifiedNodeIds == null)
                return false;
            foreach (string id in modifiedNodeIds)
                if (relevant.Contains(id))
                    return true;
            return false;
        }

        private static bool ReadSetInSubDoc(List<string> readSet, HashSet<string> subDoc)
        {
            if (readSet == null)
                return true; // empty read set: the test selected nothing — reproducible in the sub-doc
            foreach (string id in readSet)
                if (!subDoc.Contains(id))
                    return false;
            return true;
        }

        // ProvenanceRecorder.RecordPatch buckets any op missing from the patch-id index under the
        // literal id "unindexed#{type}" (see its comment). The prefix is fixed and a real packageId
        // can never be "unindexed" with no dots before '#', so a prefix test is unambiguous.
        private static bool IsUnattributed(string patchId) =>
            patchId != null && patchId.StartsWith("unindexed#", StringComparison.Ordinal);

        // A dynamically-generated op's id carries ".generated[" (ProvenanceRecorder.EnterApply
        // synthesizes "{parentId}.generated[N]" when an op applied at runtime was never in the static
        // index). Look only after '#' so a packageId containing the literal can't false-positive.
        private static bool IsGenerated(string patchId)
        {
            if (string.IsNullOrEmpty(patchId))
                return false;
            int hash = patchId.IndexOf('#');
            string suffix = hash >= 0 ? patchId.Substring(hash + 1) : patchId;
            return suffix.IndexOf(".generated[", StringComparison.Ordinal) >= 0;
        }

        // Branch-shaped ops whose OWN edge is a TEST, not a producing edge: a real
        // PatchOperationConditional (doc-content xpath test) plus PatchOperationFindMod (a
        // ModLister-state test, never doc content -- see RecordFindModEdge's comment). Both key
        // their branch children under ".match"/".nomatch"; treating both uniformly here lets
        // BranchParentId's conditionalReads lookup find FindMod's (always-empty, always
        // trivially-in-subdoc) read set the same way it finds a real Conditional's.
        private static bool IsConditional(string opType) =>
            !string.IsNullOrEmpty(opType) &&
            (opType.IndexOf("Conditional", StringComparison.Ordinal) >= 0
                || opType == "Verse.PatchOperationFindMod");

        // A conditional branch child's patchId carries ".match"/".nomatch" after the parent id; a
        // parent conditional edge does not. Look only after '#' so a packageId containing the literal
        // cannot false-positive (mirrors SubDocExpander.ContainerOpType).
        private static bool IsBranchChild(string patchId)
        {
            if (string.IsNullOrEmpty(patchId))
                return false;
            int hash = patchId.IndexOf('#');
            string suffix = hash >= 0 ? patchId.Substring(hash + 1) : patchId;
            return suffix.IndexOf(".match", StringComparison.Ordinal) >= 0
                || suffix.IndexOf(".nomatch", StringComparison.Ordinal) >= 0;
        }

        // The parent conditional id of a branch child ("<parentId>.match[...]" / ".nomatch[...]"), or
        // null when it is not a child of a known conditional parent.
        //
        // Must cut at the LAST ".match"/".nomatch" segment, not the first: a conditional nested inside
        // another conditional's branch (or inside a sequence inside that branch) produces an id like
        // "mod#1.match.match" or "mod#1.match.operations[2].nomatch". Cutting at the first occurrence
        // would resolve to the OUTER conditional and check its (possibly in-sub-doc) read set instead of
        // the immediate gating conditional's — silently admitting a cross-def conditional nested under a
        // same-def one. That is a false "safe to recompute", not just an over-conservative fallback.
        //
        // Ids build outer-to-inner left-to-right, so the LAST ".match"/".nomatch" is the boundary right
        // before the leaf's own position — like a file path, the immediate parent is the last segment,
        // not the first. See RecomputeAllowlistTests.NestedConditional_InnerCrossDef_Declined.
        private static string BranchParentId(string patchId, Dictionary<string, List<string>> parents)
        {
            if (string.IsNullOrEmpty(patchId) || !IsBranchChild(patchId))
                return null;
            int match = patchId.LastIndexOf(".match", StringComparison.Ordinal);
            int nomatch = patchId.LastIndexOf(".nomatch", StringComparison.Ordinal);
            int cut = Math.Max(match, nomatch);
            string parentId = patchId.Substring(0, cut);
            return parents.ContainsKey(parentId) ? parentId : null;
        }

        private static HashSet<string> AsSet(ICollection<string> ids) =>
            ids is HashSet<string> set ? set
            : ids == null ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(ids, StringComparer.Ordinal);
    }
}
