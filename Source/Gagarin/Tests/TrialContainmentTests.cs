// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using Gagarin;
using NUnit.Framework;

namespace Gagarin.Tests
{
    [TestFixture]
    public class TrialContainmentTests
    {
        private static HashSet<string> Set(params string[] ids) => new HashSet<string>(ids);

        [Test]
        public void IsContained_NoTrialTargets_TriviallyContained()
        {
            bool ok = TrialContainment.IsContained(
                new List<string>(), Set("ThingDef/Existing"), out List<string> overflow);

            Assert.That(ok, Is.True);
            Assert.That(overflow, Is.Empty);
        }

        [Test]
        public void IsContained_NullTrialTargets_TriviallyContained()
        {
            bool ok = TrialContainment.IsContained(
                null, Set("ThingDef/Existing"), out List<string> overflow);

            Assert.That(ok, Is.True);
            Assert.That(overflow, Is.Empty);
        }

        // The genuinely-new-def case (the fixture the live --expect-trial-execution proves):
        // a PatchOperationAdd created a top-level def whose id was never captured by the prior
        // graph at all -- nothing to clobber, safe to fold into context.
        [Test]
        public void IsContained_TrialTargetAbsentFromGraph_Contained()
        {
            bool ok = TrialContainment.IsContained(
                Set("ThingDef/TC_TrialNew"), Set("ThingDef/Unrelated"), out List<string> overflow);

            Assert.That(ok, Is.True);
            Assert.That(overflow, Is.Empty);
        }

        // The overflow case: the trial's "new" id collides with a REAL def the prior graph
        // already knows about (just not part of dirty/context this load). Admitting it as fresh
        // content would silently clobber/duplicate that real def instead of recomputing it
        // faithfully, so this must be reported as overflow, not folded in.
        [Test]
        public void IsContained_TrialTargetCollidesWithKnownGraphNode_Overflow()
        {
            bool ok = TrialContainment.IsContained(
                Set("ThingDef/TC_RealExisting"), Set("ThingDef/TC_RealExisting"), out List<string> overflow);

            Assert.That(ok, Is.False);
            Assert.That(overflow, Is.EquivalentTo(new[] { "ThingDef/TC_RealExisting" }));
        }

        // Mixed case: one new (safe), one colliding (overflow) -- the overall verdict is still
        // "not contained", and only the colliding id is reported so the fallback reason names the
        // specific offender rather than the whole batch.
        [Test]
        public void IsContained_MixedNewAndColliding_ReportsOnlyTheCollision()
        {
            bool ok = TrialContainment.IsContained(
                Set("ThingDef/TC_TrialNew", "ThingDef/TC_RealExisting"),
                Set("ThingDef/TC_RealExisting"),
                out List<string> overflow);

            Assert.That(ok, Is.False);
            Assert.That(overflow, Is.EquivalentTo(new[] { "ThingDef/TC_RealExisting" }));
        }
    }
}
