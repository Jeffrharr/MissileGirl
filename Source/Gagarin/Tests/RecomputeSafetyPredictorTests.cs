// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// RecomputeSafetyPredictorTests.cs
//
// Contains: the offline correctness gate for RecomputeSafetyPredictor — the pure "can this load be
// recomputed faithfully, or must it fall back" check. Drives synthetic GraphPatchEdges with the real
// "{mod}#{i}" parent / "{mod}#{i}.match" branch id scheme ProvenanceRecorder emits for a
// PatchOperationConditional, matching exactly what a live capture records (verified against the
// --expect-recompute-gap run: parent edge Matched = test read set, branch edge Modified = effect).
//
// Why: this decides whether wrong data is served. The two load-bearing cases are CASE 6 (cross-def
// conditional whose probe is absent from the sub-doc -> UNSAFE) and CASE 5 (same-def conditional whose
// read target is the dirty def itself, present in the sub-doc -> SAFE, must still recompute). Both,
// plus inheritance fan-out and the irrelevant-effect / present-as-context spares, are asserted here so
// the in-game gate only has to confirm the prediction matches the engine, not the set algebra.

using System.Collections.Generic;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class RecomputeSafetyPredictorTests
    {
        // A conditional PARENT edge: its Matched set is the test (read) targets; Modified is the
        // spurious self-attribution the capture also records and the predictor must ignore.
        private static GraphPatchEdge Conditional(string patchId, string mod, params string[] readTargets)
        {
            var e = new GraphPatchEdge
            {
                PatchId = patchId,
                SourceMod = mod,
                OperationType = "PatchOperationConditional",
            };
            e.MatchedNodeIds.AddRange(readTargets);
            e.ModifiedNodeIds.AddRange(readTargets); // mirrors the live capture's redundant self-mark
            return e;
        }

        // A conditional BRANCH child edge (.match / .nomatch): its Modified set is the effect.
        private static GraphPatchEdge Branch(string patchId, string mod, params string[] effect)
        {
            var e = new GraphPatchEdge
            {
                PatchId = patchId,
                SourceMod = mod,
                OperationType = "PatchOperationAdd",
            };
            e.ModifiedNodeIds.AddRange(effect);
            e.MatchedNodeIds.AddRange(effect);
            return e;
        }

        private static DependencyGraphData Graph(params GraphPatchEdge[] edges)
        {
            var g = new DependencyGraphData { Version = 1 };
            g.PatchEdges.AddRange(edges);
            return g;
        }

        private static void Inherit(DependencyGraphData g, string parentNodeId, params string[] childNodeIds)
        {
            foreach (string c in childNodeIds)
                g.InheritanceEdges.Add(new GraphInheritanceEdge
                {
                    ParentNodeId = parentNodeId,
                    ChildNodeId = c,
                    ParentName = parentNodeId,
                });
        }

        private static HashSet<string> Set(params string[] ids) => new HashSet<string>(ids);

        // CASE 6: cross-def conditional. Test reads TC_CrossProbe (absent from the sub-doc); branch
        // modifies the dirty TC_CrossEffect. The branch choice is unreliable -> UNSAFE.
        [Test]
        public void CrossDefConditional_ReadAbsent_EffectDirty_IsUnsafe()
        {
            var g = Graph(
                Conditional("mod.static#2", "mod.static", "ThingDef/TC_CrossProbe"),
                Branch("mod.static#2.match", "mod.static", "ThingDef/TC_CrossEffect"));

            bool unsafe_ = RecomputeSafetyPredictor.IsUnsafe(
                g, Set("ThingDef/TC_CrossEffect"), Set(), out string reason);

            Assert.That(unsafe_, Is.True);
            Assert.That(reason, Is.Not.Null);
        }

        // CASE 5: same-def conditional. Test reads the very def it modifies, which is dirty and so
        // present in the sub-doc -> the test re-evaluates correctly -> SAFE (must still recompute).
        [Test]
        public void SameDefConditional_ReadIsTheDirtyDef_IsSafe()
        {
            var g = Graph(
                Conditional("mod.static#1", "mod.static", "ThingDef/TC_Conditional"),
                Branch("mod.static#1.nomatch", "mod.static", "ThingDef/TC_Conditional"));

            bool unsafe_ = RecomputeSafetyPredictor.IsUnsafe(
                g, Set("ThingDef/TC_Conditional"), Set(), out string reason);

            Assert.That(unsafe_, Is.False);
            Assert.That(reason, Is.Null);
        }

        // A conditional whose test selected nothing (empty read set) is trivially reproducible: the
        // sub-doc would also select nothing -> same branch -> SAFE.
        [Test]
        public void Conditional_EmptyReadSet_IsSafe()
        {
            var g = Graph(
                Conditional("mod.static#1", "mod.static" /* no read targets */),
                Branch("mod.static#1.nomatch", "mod.static", "ThingDef/TC_Conditional"));

            bool unsafe_ = RecomputeSafetyPredictor.IsUnsafe(
                g, Set("ThingDef/TC_Conditional"), Set(), out _);

            Assert.That(unsafe_, Is.False);
        }

        // Read target absent, but the branch effect does not touch any dirty def -> the unreliable
        // choice changes nothing we recompute -> SAFE.
        [Test]
        public void ReadAbsent_ButEffectNotDirty_IsSafe()
        {
            var g = Graph(
                Conditional("mod.static#2", "mod.static", "ThingDef/Probe"),
                Branch("mod.static#2.match", "mod.static", "ThingDef/Untouched"));

            bool unsafe_ = RecomputeSafetyPredictor.IsUnsafe(
                g, Set("ThingDef/SomethingElseDirty"), Set(), out _);

            Assert.That(unsafe_, Is.False);
        }

        // The read target is present in the sub-doc as CONTEXT (pulled in by SubDocExpander) -> the
        // test re-evaluates correctly -> SAFE even though the effect hits a dirty def.
        [Test]
        public void ReadTargetPresentAsContext_IsSafe()
        {
            var g = Graph(
                Conditional("mod.static#2", "mod.static", "ThingDef/Probe"),
                Branch("mod.static#2.match", "mod.static", "ThingDef/Effect"));

            bool unsafe_ = RecomputeSafetyPredictor.IsUnsafe(
                g, Set("ThingDef/Effect"), Set("ThingDef/Probe"), out _);

            Assert.That(unsafe_, Is.False);
        }

        // The branch modifies an ABSTRACT base; a dirty concrete def inherits from it. The effect
        // must propagate DOWN inheritance to reach the dirty descendant -> UNSAFE.
        [Test]
        public void EffectOnAbstractBase_ReachesDirtyDescendant_IsUnsafe()
        {
            var g = Graph(
                Conditional("mod.static#2", "mod.static", "ThingDef/Probe"),
                Branch("mod.static#2.match", "mod.static", "ThingDef@WaterBase"));
            Inherit(g, "ThingDef@WaterBase", "ThingDef/SpringFlood");

            bool unsafe_ = RecomputeSafetyPredictor.IsUnsafe(
                g, Set("ThingDef/SpringFlood"), Set(), out string reason);

            Assert.That(unsafe_, Is.True);
            Assert.That(reason, Is.Not.Null);
        }

        // No conditional ops at all -> nothing to predict -> SAFE.
        [Test]
        public void NoConditionals_IsSafe()
        {
            var g = Graph(
                Branch("mod.x#0", "mod.x", "ThingDef/A")); // a plain Add, not a conditional branch

            bool unsafe_ = RecomputeSafetyPredictor.IsUnsafe(
                g, Set("ThingDef/A"), Set(), out _);

            Assert.That(unsafe_, Is.False);
        }

        // Empty dirty set -> nothing is being recomputed -> SAFE regardless of conditionals.
        [Test]
        public void EmptyDirtySet_IsSafe()
        {
            var g = Graph(
                Conditional("mod.static#2", "mod.static", "ThingDef/Probe"),
                Branch("mod.static#2.match", "mod.static", "ThingDef/Effect"));

            bool unsafe_ = RecomputeSafetyPredictor.IsUnsafe(g, Set(), Set(), out _);

            Assert.That(unsafe_, Is.False);
        }
    }
}
