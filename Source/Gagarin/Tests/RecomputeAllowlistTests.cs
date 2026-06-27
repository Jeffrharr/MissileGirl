// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// RecomputeAllowlistTests.cs
//
// Contains: the offline correctness gate for RecomputeAllowlist — the safe-by-default check that
// admits the incremental recompute ONLY for proven-faithful patterns and falls back (categorized) on
// everything else. Drives synthetic GraphPatchEdges with the real ProvenanceRecorder id scheme
// ("{mod}#{i}" parent / "{mod}#{i}.match" branch / "{mod}#{i}.operations[N]" sequence child).
//
// Why: this is the load-bearing safety decision — a false "can recompute" ships wrong data in
// production (no full rebuild to catch it). The cases mirror the live test modes: leaf (CASE 1),
// wildcard leaf (CASE 2), sequence-with-context (CASE 3), same-def conditional (CASE 5) all ADMITTED;
// cross-def conditional (CASE 6), positional/Insert, custom/unknown ops, and capture gaps all
// DECLINED with the right category so the metrics backlog is correct.

using System.Collections.Generic;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class RecomputeAllowlistTests
    {
        private static GraphPatchEdge Edge(string patchId, string op, string xpath, params string[] modified)
        {
            var e = new GraphPatchEdge { PatchId = patchId, SourceMod = "mod", OperationType = op, Xpath = xpath };
            e.ModifiedNodeIds.AddRange(modified);
            e.MatchedNodeIds.AddRange(modified);
            return e;
        }

        // A conditional PARENT test edge: Matched = read targets; Modified = the capture's spurious
        // self-mark the allowlist must ignore (parent edges produce nothing).
        private static GraphPatchEdge Conditional(string patchId, string xpath, params string[] readTargets)
        {
            var e = new GraphPatchEdge
            {
                PatchId = patchId, SourceMod = "mod",
                OperationType = "PatchOperationConditional", Xpath = xpath,
            };
            e.MatchedNodeIds.AddRange(readTargets);
            e.ModifiedNodeIds.AddRange(readTargets);
            return e;
        }

        private static DependencyGraphData Graph(params GraphPatchEdge[] edges)
        {
            var g = new DependencyGraphData { Version = 1 };
            g.PatchEdges.AddRange(edges);
            return g;
        }

        private static void Inherit(DependencyGraphData g, string parentNodeId, params string[] children)
        {
            foreach (string c in children)
                g.InheritanceEdges.Add(new GraphInheritanceEdge
                { ParentNodeId = parentNodeId, ChildNodeId = c, ParentName = parentNodeId });
        }

        private static HashSet<string> Set(params string[] ids) => new HashSet<string>(ids);

        private static bool Can(DependencyGraphData g, HashSet<string> dirty, HashSet<string> ctx, out string cat)
            => RecomputeAllowlist.CanRecompute(g, dirty, ctx, out _, out cat);

        // CASE 1: a plain defName-anchored leaf op on the dirty def -> ADMITTED.
        [Test]
        public void LeafOp_DefNameAnchored_Admitted()
        {
            var g = Graph(Edge("mod#0", "PatchOperationReplace",
                "Defs/ThingDef[defName=\"A\"]/label", "ThingDef/A"));
            Assert.That(Can(g, Set("ThingDef/A"), Set(), out _), Is.True);
        }

        // CASE 2: a wildcard leaf op (@ParentName) matching several defs -> ADMITTED. A per-node leaf
        // effect is local, so matching fewer nodes in the sub-doc still gives each dirty def the right
        // value (wildcard != positional).
        [Test]
        public void LeafOp_Wildcard_Admitted()
        {
            var g = Graph(Edge("mod#0", "PatchOperationAdd",
                "Defs/ThingDef[@ParentName=\"Base\"]", "ThingDef/A", "ThingDef/B", "ThingDef/C"));
            Assert.That(Can(g, Set("ThingDef/A"), Set(), out _), Is.True);
        }

        // CASE 3: a sequence child leaf op (siblings already pulled into context by SubDocExpander
        // upstream) -> ADMITTED.
        [Test]
        public void SequenceChild_LeafOp_Admitted()
        {
            var g = Graph(
                Edge("mod#0.operations[0]", "PatchOperationAdd", "Defs/ThingDef[defName=\"Sib\"]", "ThingDef/Sib"),
                Edge("mod#0.operations[1]", "PatchOperationAdd", "Defs/ThingDef[defName=\"Tgt\"]", "ThingDef/Tgt"));
            Assert.That(Can(g, Set("ThingDef/Tgt"), Set("ThingDef/Sib"), out _), Is.True);
        }

        // CASE 5: a same-def conditional — its branch modifies the dirty def and its test reads that
        // same (in-sub-doc) def -> ADMITTED.
        [Test]
        public void SameDefConditional_ReadInSubDoc_Admitted()
        {
            var g = Graph(
                Conditional("mod#1", "Defs/ThingDef[defName=\"D\"]/trigger", "ThingDef/D"),
                Edge("mod#1.nomatch", "PatchOperationAdd", "Defs/ThingDef[defName=\"D\"]", "ThingDef/D"));
            Assert.That(Can(g, Set("ThingDef/D"), Set(), out _), Is.True);
        }

        // A conditional whose test selected nothing (empty read set) -> ADMITTED (reproducible: the
        // sub-doc also selects nothing, same branch).
        [Test]
        public void Conditional_EmptyReadSet_Admitted()
        {
            var g = Graph(
                Conditional("mod#1", "Defs/ThingDef[defName=\"D\"]/trigger" /* no read targets */),
                Edge("mod#1.nomatch", "PatchOperationAdd", "Defs/ThingDef[defName=\"D\"]", "ThingDef/D"));
            Assert.That(Can(g, Set("ThingDef/D"), Set(), out _), Is.True);
        }

        // CASE 6: cross-def conditional — test reads a probe absent from the sub-doc, branch modifies
        // the dirty def -> DECLINED (conditional-cross-def).
        [Test]
        public void CrossDefConditional_Declined()
        {
            var g = Graph(
                Conditional("mod#2", "Defs/ThingDef[defName=\"Probe\"]/flag", "ThingDef/Probe"),
                Edge("mod#2.match", "PatchOperationAdd", "Defs/ThingDef[defName=\"Effect\"]", "ThingDef/Effect"));
            Assert.That(Can(g, Set("ThingDef/Effect"), Set(), out string cat), Is.False);
            Assert.That(cat, Is.EqualTo("conditional-cross-def"));
        }

        // An unknown / unmodelled op kind producing the dirty def -> DECLINED (unknown-op-kind). This
        // is the safe-by-default property: anything we have not proven falls back.
        [Test]
        public void UnknownOpKind_Declined()
        {
            var g = Graph(Edge("mod#0", "PatchOperationAddModExtension",
                "Defs/ThingDef[defName=\"A\"]", "ThingDef/A"));
            Assert.That(Can(g, Set("ThingDef/A"), Set(), out string cat), Is.False);
            Assert.That(cat, Is.EqualTo("unknown-op-kind"));
        }

        // PatchOperationInsert is positional by nature; even as an unmodelled kind it declines.
        [Test]
        public void Insert_Declined()
        {
            var g = Graph(Edge("mod#0", "PatchOperationInsert",
                "Defs/ThingDef[defName=\"A\"]/comps/li", "ThingDef/A"));
            Assert.That(Can(g, Set("ThingDef/A"), Set(), out string cat), Is.False);
            Assert.That(cat, Is.EqualTo("unknown-op-kind"));
        }

        // A safe leaf op but with a positional def-selector -> DECLINED (positional-xpath).
        [Test]
        public void PositionalXpath_Declined()
        {
            var g = Graph(Edge("mod#0", "PatchOperationReplace",
                "Defs/ThingDef[3]/label", "ThingDef/A"));
            Assert.That(Can(g, Set("ThingDef/A"), Set(), out string cat), Is.False);
            Assert.That(cat, Is.EqualTo("positional-xpath"));
        }

        // A producing op with no captured operation type -> DECLINED (capture-gap).
        [Test]
        public void EmptyOpType_Declined_AsCaptureGap()
        {
            var g = Graph(Edge("mod#0", null, "Defs/ThingDef[defName=\"A\"]", "ThingDef/A"));
            Assert.That(Can(g, Set("ThingDef/A"), Set(), out string cat), Is.False);
            Assert.That(cat, Is.EqualTo("capture-gap"));
        }

        // An UNATTRIBUTED op (RecordPatch's "unindexed#{type}" bucket — a dynamically-generated op)
        // looks like a safe leaf op (real OperationType, non-positional xpath) but must DECLINE as a
        // capture-gap: we cannot vouch for what generated it or what it reads. This is the apparel-op
        // false-admit hole (3 ops on a ~100-mod capture).
        [Test]
        public void UnindexedOp_LooksSafeButDeclined_AsCaptureGap()
        {
            var g = Graph(Edge("unindexed#PatchOperationReplace", "PatchOperationReplace",
                "Defs/ThingDef[defName=\"Apparel_Cape\"]/x", "ThingDef/Apparel_Cape"));
            Assert.That(Can(g, Set("ThingDef/Apparel_Cape"), Set(), out string cat), Is.False);
            Assert.That(cat, Is.EqualTo("capture-gap"));
        }

        // A dynamically-generated op (attributed as "{parent}.generated[N]") carries a real op type +
        // sourceMod but is still recompute-unsafe (opaque generator) -> DECLINED, category dynamic-op
        // (distinct from capture-gap: the risk IS attributed to a mod).
        [Test]
        public void GeneratedOp_Declined_AsDynamicOp()
        {
            var g = Graph(Edge("mod.x#3.generated[0]", "PatchOperationReplace",
                "Defs/ThingDef[defName=\"A\"]/x", "ThingDef/A"));
            Assert.That(Can(g, Set("ThingDef/A"), Set(), out string cat), Is.False);
            Assert.That(cat, Is.EqualTo("dynamic-op"));
        }

        // An unsafe op on an ANCESTOR of a dirty def declines: the dirty descendant inherits the
        // ancestor's (cross-def-conditional) value, so it cannot be recomputed faithfully either.
        [Test]
        public void UnsafeOpOnAncestor_Declines()
        {
            var g = Graph(
                Conditional("mod#2", "Defs/ThingDef[defName=\"Probe\"]/flag", "ThingDef/Probe"),
                Edge("mod#2.match", "PatchOperationAdd", "Defs/ThingDef[@Name=\"Base\"]", "ThingDef@Base"));
            Inherit(g, "ThingDef@Base", "ThingDef/Child");
            Assert.That(Can(g, Set("ThingDef/Child"), Set(), out string cat), Is.False);
            Assert.That(cat, Is.EqualTo("conditional-cross-def"));
        }

        // An op unrelated to any dirty def does not force a fallback.
        [Test]
        public void IrrelevantUnsafeOp_DoesNotDecline()
        {
            var g = Graph(
                Edge("mod#0", "PatchOperationReplace", "Defs/ThingDef[defName=\"A\"]/label", "ThingDef/A"),
                Edge("mod#9", "PatchOperationInsert", "Defs/ThingDef[defName=\"Other\"]/li", "ThingDef/Other"));
            Assert.That(Can(g, Set("ThingDef/A"), Set(), out _), Is.True);
        }

        // Empty dirty set -> nothing to recompute -> ADMITTED (the splice changes nothing).
        [Test]
        public void EmptyDirtySet_Admitted()
        {
            var g = Graph(Edge("mod#0", "PatchOperationInsert", "Defs/ThingDef[1]", "ThingDef/A"));
            Assert.That(Can(g, Set(), Set(), out _), Is.True);
        }

        // Null graph -> cannot verify safety -> DECLINED (capture-gap).
        [Test]
        public void NullGraph_Declined()
        {
            Assert.That(RecomputeAllowlist.CanRecompute(null, Set("ThingDef/A"), Set(), out _, out string cat), Is.False);
            Assert.That(cat, Is.EqualTo("capture-gap"));
        }
    }
}
