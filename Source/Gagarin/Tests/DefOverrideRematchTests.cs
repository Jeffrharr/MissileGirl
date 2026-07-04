// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DefOverrideRematchTests.cs (issue #43 — add-direction rematch)
//
// Contains: offline correctness gate for DefOverrideRematch.NewlyDetectedOverrides --
// dictionary-only fixtures, no XML needed (unlike WildcardRematch, this is a plain id/mod
// comparison against baseline node ownership).

using System.Collections.Generic;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class DefOverrideRematchTests
    {
        private static DependencyGraphData Baseline()
        {
            var g = new DependencyGraphData { Version = 1 };
            g.Nodes.Add(new GraphNode { Id = "GeneDef/Eyes_Red", SourceMod = "ludeon.rimworld.biotech" });
            g.Nodes.Add(new GraphNode { Id = "GeneDef/Eyes_Gray", SourceMod = "ludeon.rimworld.biotech" });
            g.Nodes.Add(new GraphNode { Id = "ThingDef/Steel", SourceMod = "ludeon.rimworld" });
            return g;
        }

        [Test]
        public void NewlyDetectedOverrides_BaselineNodeUnderDifferentMod_ReturnsId()
        {
            var currentIdOwner = new Dictionary<string, string>
            {
                ["GeneDef/Eyes_Red"] = "oppey.eyegenes2" // newly-added mod now owns this id
            };
            var newlyAddedMods = new HashSet<string> { "oppey.eyegenes2" };

            var result = DefOverrideRematch.NewlyDetectedOverrides(Baseline(), currentIdOwner, newlyAddedMods);

            Assert.That(result, Contains.Item("GeneDef/Eyes_Red"));
        }

        [Test]
        public void NewlyDetectedOverrides_SameOwner_NoFlip()
        {
            var currentIdOwner = new Dictionary<string, string>
            {
                ["GeneDef/Eyes_Red"] = "ludeon.rimworld.biotech" // unchanged owner
            };
            var newlyAddedMods = new HashSet<string> { "ludeon.rimworld.biotech" };

            var result = DefOverrideRematch.NewlyDetectedOverrides(Baseline(), currentIdOwner, newlyAddedMods);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void NewlyDetectedOverrides_NotInBaseline_NoFlip()
        {
            // A genuinely new def (no baseline node at all) is the added-defs channel's job
            // (Seed 5), not this rematch's -- must not be reported here.
            var currentIdOwner = new Dictionary<string, string>
            {
                ["GeneDef/Eyes_Blue"] = "oppey.eyegenes2"
            };
            var newlyAddedMods = new HashSet<string> { "oppey.eyegenes2" };

            var result = DefOverrideRematch.NewlyDetectedOverrides(Baseline(), currentIdOwner, newlyAddedMods);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void NewlyDetectedOverrides_OwnerNotInNewlyAddedMods_NoFlip()
        {
            // The current owner differs from baseline, but that mod is NOT newly added --
            // an existing mod's Defs file legitimately changing its own content is an
            // ordinary file edit, already covered by Seed 1.
            var currentIdOwner = new Dictionary<string, string>
            {
                ["GeneDef/Eyes_Red"] = "some.existing.mod"
            };
            var newlyAddedMods = new HashSet<string> { "oppey.eyegenes2" }; // different mod

            var result = DefOverrideRematch.NewlyDetectedOverrides(Baseline(), currentIdOwner, newlyAddedMods);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void NewlyDetectedOverrides_EmptyInputs_NoOpAndNoThrow()
        {
            Assert.That(DefOverrideRematch.NewlyDetectedOverrides(null, null, null), Is.Empty);
            Assert.That(DefOverrideRematch.NewlyDetectedOverrides(Baseline(), new Dictionary<string, string>(), new HashSet<string> { "x" }), Is.Empty);
            Assert.That(DefOverrideRematch.NewlyDetectedOverrides(Baseline(), new Dictionary<string, string> { ["a"] = "b" }, new HashSet<string>()), Is.Empty);
        }
    }
}
