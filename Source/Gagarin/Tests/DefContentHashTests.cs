// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DefContentHashTests.cs
//
// Offline gate for DefContentHash — the per-def content fingerprint used as the staleness substrate.
// Asserts the hash is deterministic and value-sensitive, the diff yields the true changed/added/removed
// sets, and serialize/parse round-trips (incl. truncation robustness).

using System.Collections.Generic;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class DefContentHashTests
    {
        [Test]
        public void Hash_IsDeterministic_AndValueSensitive()
        {
            Assert.That(DefContentHash.Hash("<ThingDef><defName>A</defName></ThingDef>"),
                Is.EqualTo(DefContentHash.Hash("<ThingDef><defName>A</defName></ThingDef>")));
            Assert.That(DefContentHash.Hash("a"), Is.Not.EqualTo(DefContentHash.Hash("b")));
            Assert.That(DefContentHash.Hash("a"), Has.Length.EqualTo(16)); // 64-bit -> 16 hex
        }

        [Test]
        public void Hash_NullAndEmpty_AreStable()
        {
            Assert.That(DefContentHash.Hash(null), Is.EqualTo(DefContentHash.Hash(null)));
            Assert.That(DefContentHash.Hash(""), Is.EqualTo(DefContentHash.Hash(null))); // both = offset basis
        }

        [Test]
        public void HashAll_HashesEachValue()
        {
            var map = new Dictionary<string, string>
            {
                ["ThingDef/A"] = "<A>1</A>",
                ["ThingDef/B"] = "<B>2</B>",
            };
            var hashes = DefContentHash.HashAll(map);
            Assert.That(hashes["ThingDef/A"], Is.EqualTo(DefContentHash.Hash("<A>1</A>")));
            Assert.That(hashes["ThingDef/B"], Is.EqualTo(DefContentHash.Hash("<B>2</B>")));
        }

        [Test]
        public void Diff_ReportsChangedAddedRemoved()
        {
            var prior = DefContentHash.HashAll(new Dictionary<string, string>
            {
                ["ThingDef/Same"] = "<x>1</x>",
                ["ThingDef/Changed"] = "<x>1</x>",
                ["ThingDef/Removed"] = "<x>1</x>",
            });
            var current = DefContentHash.HashAll(new Dictionary<string, string>
            {
                ["ThingDef/Same"] = "<x>1</x>",
                ["ThingDef/Changed"] = "<x>2</x>", // value differs -> changed
                ["ThingDef/Added"] = "<x>9</x>",
            });

            DefContentHash.Diff(prior, current,
                out List<string> changed, out List<string> added, out List<string> removed);

            Assert.That(changed, Is.EqualTo(new[] { "ThingDef/Changed" }));
            Assert.That(added, Is.EqualTo(new[] { "ThingDef/Added" }));
            Assert.That(removed, Is.EqualTo(new[] { "ThingDef/Removed" }));
        }

        [Test]
        public void Diff_NullPrior_AllAdded()
        {
            var current = DefContentHash.HashAll(new Dictionary<string, string> { ["ThingDef/A"] = "x" });
            DefContentHash.Diff(null, current, out var changed, out var added, out var removed);
            Assert.That(changed, Is.Empty);
            Assert.That(added, Is.EqualTo(new[] { "ThingDef/A" }));
            Assert.That(removed, Is.Empty);
        }

        [Test]
        public void SerializeParse_RoundTrips_Sorted()
        {
            var map = new Dictionary<string, string>
            {
                ["ThingDef/Z"] = "ffff",
                ["ThingDef/A"] = "0001",
            };
            string tsv = DefContentHash.Serialize(map);
            // Sorted by id: A before Z.
            Assert.That(tsv, Is.EqualTo("ThingDef/A\t0001\nThingDef/Z\tffff\n"));
            var back = DefContentHash.Parse(tsv);
            Assert.That(back, Is.EquivalentTo(map));
        }

        [Test]
        public void Parse_SkipsTruncatedOrTablessLines()
        {
            // A trailing truncated line (no tab) must not throw or corrupt the map.
            var back = DefContentHash.Parse("ThingDef/A\t0001\nThingDef/Btrunc");
            Assert.That(back, Is.EqualTo(new Dictionary<string, string> { ["ThingDef/A"] = "0001" }));
        }
    }
}
