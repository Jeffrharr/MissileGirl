// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// SubDocExpanderTests.cs (Piece D — Milestone 2b-2b)
//
// Contains: the offline correctness gate for SubDocExpander — the pure sibling-expansion and
// changed-mod container-op fallback that grows the recompute sub-doc just enough that
// PatchOperationSequences don't abort on absent targets. Drives synthetic GraphPatchEdges with
// the real "{mod}#{i}.operations[N]" / ".match" id scheme ProvenanceRecorder emits.
//
// Why: this is the load-bearing decision for M2b-2b correctness — too little context and a
// sequence aborts (the 12-mismatch dead-end); a wrong fallback and we either recompute a stale
// container op or needlessly fall back to a full rebuild. Both halves are asserted here so the
// in-game gate only has to confirm engine fidelity, not the set algebra.

using System.Collections.Generic;
using System.Linq;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class SubDocExpanderTests
    {
        private static GraphPatchEdge Edge(string patchId, string sourceMod, params string[] modified)
        {
            var e = new GraphPatchEdge { PatchId = patchId, SourceMod = sourceMod };
            e.ModifiedNodeIds.AddRange(modified);
            e.MatchedNodeIds.AddRange(modified);
            return e;
        }

        private static DependencyGraphData Graph(params GraphPatchEdge[] edges)
        {
            var g = new DependencyGraphData { Version = 1 };
            g.PatchEdges.AddRange(edges);
            return g;
        }

        private static HashSet<string> Set(params string[] ids) => new HashSet<string>(ids);

        // A 3-op sequence touching three distinct defs; one def dirty pulls in the other two.
        [Test]
        public void SequenceSiblings_PulledInAsContext()
        {
            var g = Graph(
                Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/A"),
                Edge("vendor.seq#0.operations[1]", "vendor.seq", "ThingDef/B"),
                Edge("vendor.seq#0.operations[2]", "vendor.seq", "ThingDef/C"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A"), Set(),
                out bool fullRebuild, out string reason);

            Assert.That(fullRebuild, Is.False);
            Assert.That(reason, Is.Null);
            Assert.That(context, Is.EquivalentTo(new[] { "ThingDef/B", "ThingDef/C" }));
        }

        // Sibling defs that are themselves dirty are not "context" — context is the EXTRA defs
        // the sub-doc needs beyond the dirty set.
        [Test]
        public void DirtySiblings_ExcludedFromContext()
        {
            var g = Graph(
                Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/A"),
                Edge("vendor.seq#0.operations[1]", "vendor.seq", "ThingDef/B"),
                Edge("vendor.seq#0.operations[2]", "vendor.seq", "ThingDef/C"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A", "ThingDef/B"), Set(),
                out _, out _);

            Assert.That(context, Is.EquivalentTo(new[] { "ThingDef/C" }));
        }

        // Two dirty defs in the same sequence expand it once; the result is still just the
        // remaining sibling (no duplication, no missing sibling).
        [Test]
        public void SameSequenceTwiceDirty_ExpandedOnce()
        {
            var g = Graph(
                Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/A"),
                Edge("vendor.seq#0.operations[1]", "vendor.seq", "ThingDef/B"),
                Edge("vendor.seq#0.operations[2]", "vendor.seq", "ThingDef/C"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A", "ThingDef/C"), Set(),
                out _, out _);

            Assert.That(context, Is.EquivalentTo(new[] { "ThingDef/B" }));
        }

        // A top-level op (no ".operations[N]" suffix) is not a sequence child — nothing to expand.
        [Test]
        public void TopLevelOp_NoExpansion()
        {
            var g = Graph(Edge("vendor.simple#0", "vendor.simple", "ThingDef/A"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A"), Set(), out _, out _);

            Assert.That(context, Is.Empty);
        }

        // A conditional branch (".match") is NOT a list sibling: its ops don't share a sequence
        // parent, so no sibling expansion happens for an unchanged mod's conditional.
        [Test]
        public void ConditionalBranch_NotExpandedAsSibling()
        {
            var g = Graph(Edge("vendor.cond#0.match", "vendor.cond", "ThingDef/A"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A"), Set(), out bool fullRebuild, out _);

            Assert.That(fullRebuild, Is.False);
            Assert.That(context, Is.Empty);
        }

        // Only sequence siblings under the SAME parent are pulled in; an unrelated sequence's
        // children are left out.
        [Test]
        public void UnrelatedSequence_NotPulledIn()
        {
            var g = Graph(
                Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/A"),
                Edge("vendor.seq#0.operations[1]", "vendor.seq", "ThingDef/B"),
                Edge("vendor.seq#1.operations[0]", "vendor.seq", "ThingDef/X"),
                Edge("vendor.seq#1.operations[1]", "vendor.seq", "ThingDef/Y"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A"), Set(), out _, out _);

            Assert.That(context, Is.EquivalentTo(new[] { "ThingDef/B" }));
        }

        // The changed mod owns a Sequence child op: its baseline path is stale -> full rebuild.
        // Mirrors the --expect-fallback live fixture (Change_Run{A,B}_Fallback.xml): the change
        // mod joof.testharness.change owns a 2-op PatchOperationSequence whose children patch
        // TC_SeqTarget/TC_Identity, so the baseline graph carries ".operations[N]" edges with
        // that mod as SourceMod. The graph also contains an UNCHANGED mod's sequence whose
        // sibling expansion WOULD otherwise contribute context for the dirty def — proving the
        // fallback short-circuits before any expansion (returned context must be empty, not the
        // expanded set). The reason must name the container op type and the exact offending
        // patchId so the live RecomputeReport.json's fallbackReason is diagnosable.
        [Test]
        public void ChangedModSequenceOp_TriggersFallback()
        {
            var g = Graph(
                // The change mod's own sequence (captured on Run A; edited on Run B).
                Edge("joof.testharness.change#0.operations[0]", "joof.testharness.change", "ThingDef/TC_SeqTarget"),
                Edge("joof.testharness.change#0.operations[1]", "joof.testharness.change", "ThingDef/TC_Identity"),
                // An unchanged static mod's sequence touching the same dirty def plus a sibling:
                // if the fallback did NOT fire, TC_SeqSibling would be pulled in as context.
                Edge("joof.testharness.static#0.operations[0]", "joof.testharness.static", "ThingDef/TC_SeqSibling"),
                Edge("joof.testharness.static#0.operations[1]", "joof.testharness.static", "ThingDef/TC_SeqTarget"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/TC_SeqTarget"),
                Set("joof.testharness.change"), out bool fullRebuild, out string reason);

            Assert.That(fullRebuild, Is.True);
            // Fallback short-circuits expansion: even though the static mod's sequence would
            // otherwise contribute TC_SeqSibling, the context is empty when falling back.
            Assert.That(context, Is.Empty);
            Assert.That(reason, Does.Contain("PatchOperationSequence"));
            Assert.That(reason, Does.Contain("joof.testharness.change"));
            Assert.That(reason, Does.Contain(".operations["));
        }

        // The changed mod owns a Conditional branch op -> full rebuild, named as a Conditional.
        [Test]
        public void ChangedModConditionalOp_TriggersFallback()
        {
            var g = Graph(Edge("joof.testharness.change#0.match", "joof.testharness.change", "ThingDef/A"));

            SubDocExpander.Expand(g, Set("ThingDef/A"), Set("joof.testharness.change"),
                out bool fullRebuild, out string reason);

            Assert.That(fullRebuild, Is.True);
            Assert.That(reason, Does.Contain("PatchOperationConditional"));
        }

        // The changed mod's op is top-level (not in a container): the M2a wildcard re-test covers
        // re-matching, so no fallback is needed.
        [Test]
        public void ChangedModTopLevelOp_NoFallback()
        {
            var g = Graph(Edge("joof.testharness.change#0", "joof.testharness.change", "ThingDef/A"));

            SubDocExpander.Expand(g, Set("ThingDef/A"), Set("joof.testharness.change"),
                out bool fullRebuild, out string reason);

            Assert.That(fullRebuild, Is.False);
            Assert.That(reason, Is.Null);
        }

        // A container op owned by an UNCHANGED mod is exactly what sibling expansion handles —
        // it must NOT trigger the fallback (the snapshot path for it is trustworthy).
        [Test]
        public void UnchangedModContainerOp_NoFallback()
        {
            var g = Graph(
                Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/A"),
                Edge("vendor.seq#0.operations[1]", "vendor.seq", "ThingDef/B"));

            // The changed mod is a DIFFERENT mod with no container op in the graph.
            var context = SubDocExpander.Expand(g, Set("ThingDef/A"), Set("other.mod"),
                out bool fullRebuild, out _);

            Assert.That(fullRebuild, Is.False);
            Assert.That(context, Is.EquivalentTo(new[] { "ThingDef/B" }));
        }

        // An op nested inside a conditional inside a sequence: the parent key strips only the
        // trailing ".operations[N]", so siblings are the conditional's list-level peers.
        [Test]
        public void NestedSequenceChild_ParentIsImmediateList()
        {
            var g = Graph(
                Edge("vendor.x#0.match.operations[0]", "vendor.x", "ThingDef/A"),
                Edge("vendor.x#0.match.operations[1]", "vendor.x", "ThingDef/B"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A"), Set(), out _, out _);

            Assert.That(context, Is.EquivalentTo(new[] { "ThingDef/B" }));
        }

        [Test]
        public void NoDirty_EmptyContextNoFallback()
        {
            var g = Graph(Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/A"));

            var context = SubDocExpander.Expand(g, Set(), Set(), out bool fullRebuild, out _);

            Assert.That(context, Is.Empty);
            Assert.That(fullRebuild, Is.False);
        }

        // Run 20260714-160405: a mod that is wholly new this load has ZERO edges anywhere in the
        // prior graph, so the container-op loop above can never see it — without the newModIds
        // check the incremental path would silently proceed and recompute Plant_Rice's vanilla,
        // un-retextured value (regrowth.botr.core's own PatchOperationSequence never gets a
        // chance to decline). The graph here only knows an unrelated vendor mod, mirroring a
        // prior graph captured before the new mod ever existed.
        [Test]
        public void NewModWithNoBaselineRepresentation_TriggersFallback()
        {
            var g = Graph(Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/Unrelated"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/Plant_Rice"),
                Set("regrowth.botr.core"), out bool fullRebuild, out string reason,
                Set("regrowth.botr.core"));

            Assert.That(fullRebuild, Is.True);
            Assert.That(context, Is.Empty);
            Assert.That(reason, Does.Contain("regrowth.botr.core"));
        }

        // A mod can be "new" (present in CurrentLoadOrder, absent from PriorLoadOrder) yet own no
        // patches at all — textures/defs only, so it never lands in changedMods (no /Patches/
        // file changed this load). Being new on its own must not force a fallback.
        [Test]
        public void NewModAbsentFromChangedMods_NoFallback()
        {
            var g = Graph(Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/A"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A"), Set(), out bool fullRebuild,
                out string reason, Set("textures.only.mod"));

            Assert.That(fullRebuild, Is.False);
            Assert.That(reason, Is.Null);
        }

        // Callers that omit newModIds entirely (every pre-fix call site) must see identical
        // behaviour to before this parameter existed.
        [Test]
        public void NewModIds_OmittedDefault_BackwardCompatible()
        {
            var g = Graph(
                Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/A"),
                Edge("vendor.seq#0.operations[1]", "vendor.seq", "ThingDef/B"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A"), Set(), out bool fullRebuild, out _);

            Assert.That(fullRebuild, Is.False);
            Assert.That(context, Is.EquivalentTo(new[] { "ThingDef/B" }));
        }

        // Two new+changed mods can independently qualify in the same load (live case:
        // vanillaexpanded.vwe and regrowth.botr.core both new+changed in run 20260714-160405).
        // Naming only the first one HashSet iteration happens to hit would mislead someone who
        // later proves that mod's ops safe and re-runs expecting fallback:false — the reason
        // must name every qualifying mod, sorted for a stable, diffable message.
        [Test]
        public void MultipleNewChangedMods_ReasonListsAll()
        {
            var g = Graph(Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/Unrelated"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/A"),
                Set("regrowth.botr.core", "vanillaexpanded.vwe"), out bool fullRebuild,
                out string reason, Set("regrowth.botr.core", "vanillaexpanded.vwe"));

            Assert.That(fullRebuild, Is.True);
            Assert.That(context, Is.Empty);
            Assert.That(reason, Does.Contain("regrowth.botr.core"));
            Assert.That(reason, Does.Contain("vanillaexpanded.vwe"));
            Assert.That(reason.IndexOf("regrowth.botr.core", System.StringComparison.Ordinal),
                Is.LessThan(reason.IndexOf("vanillaexpanded.vwe", System.StringComparison.Ordinal)),
                "reason should list qualifying mods in a stable (sorted) order");
        }

        // newModIds and changedModIds can disagree on a mod's packageId casing (e.g. captured at
        // different times, or from a manifest vs. LoadedModManager) — the intersection must still
        // recognize them as the same mod, matching the OrdinalIgnoreCase convention every other
        // mod-id set in the incremental pipeline uses.
        [Test]
        public void NewModVsChangedMod_CaseInsensitiveMatch()
        {
            var g = Graph(Edge("vendor.seq#0.operations[0]", "vendor.seq", "ThingDef/Unrelated"));

            var context = SubDocExpander.Expand(g, Set("ThingDef/Plant_Rice"),
                Set("ReGrowth.BOTR.Core"), out bool fullRebuild, out string reason,
                Set("regrowth.botr.core"));

            Assert.That(fullRebuild, Is.True);
            Assert.That(reason, Does.Contain("regrowth.botr.core"));
        }
    }
}
