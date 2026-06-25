// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// MayRequireGateTests.cs (Piece D — recompute fidelity for MayRequire)
//
// Contains: the offline correctness gate for MayRequireGate.Passes — the pure mirror of the
// root-level MayRequire / MayRequireAnyOf test LoadedModManager.ParseAndProcessXML applies before a
// def is registered. DefRecompute calls it with the real ModLister oracles in-game; here we drive
// it with a synthetic active-set so the load-bearing semantics are pinned without launching the
// game: MayRequire = ALL must be active, MayRequireAnyOf = AT LEAST ONE, both lowercased and
// comma-split, an absent (null) attribute skips its check, and the two attributes compose (AND).

using System.Collections.Generic;
using System.Linq;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class MayRequireGateTests
    {
        // Build the two ModLister-shaped oracles from a synthetic active set (case-insensitive,
        // matching ModLister.*NoSuffix's lowercased comparison — DefRecompute lowercases inputs
        // before the call, but we keep the oracle case-insensitive to mirror the real one).
        private static (System.Func<IEnumerable<string>, bool> all,
                        System.Func<IEnumerable<string>, bool> any) Oracles(params string[] active)
        {
            var set = new HashSet<string>(active.Select(s => s.ToLower()));
            System.Func<IEnumerable<string>, bool> all = ids => ids.All(id => set.Contains(id.ToLower()));
            System.Func<IEnumerable<string>, bool> any = ids => ids.Any(id => set.Contains(id.ToLower()));
            return (all, any);
        }

        [Test]
        public void NoAttributes_AlwaysPasses()
        {
            var (all, any) = Oracles(/* nothing active */);
            Assert.That(MayRequireGate.Passes(null, null, all, any), Is.True);
        }

        [Test]
        public void MayRequire_PassesWhenActive_DropsWhenInactive()
        {
            var (all, any) = Oracles("author.moda");
            Assert.That(MayRequireGate.Passes("author.moda", null, all, any), Is.True);
            Assert.That(MayRequireGate.Passes("author.modb", null, all, any), Is.False);
        }

        [Test]
        public void MayRequire_RequiresAllListedMods()
        {
            var (all, any) = Oracles("author.moda");
            // Both required, only one active -> dropped.
            Assert.That(MayRequireGate.Passes("author.moda,author.modb", null, all, any), Is.False);

            var (all2, any2) = Oracles("author.moda", "author.modb");
            Assert.That(MayRequireGate.Passes("author.moda,author.modb", null, all2, any2), Is.True);
        }

        [Test]
        public void MayRequireAnyOf_PassesIfAnyActive()
        {
            var (all, any) = Oracles("author.modb");
            // First absent, second active -> at least one active -> kept.
            Assert.That(MayRequireGate.Passes(null, "author.moda,author.modb", all, any), Is.True);
        }

        [Test]
        public void MayRequireAnyOf_DropsIfNoneActive()
        {
            var (all, any) = Oracles("author.modc");
            Assert.That(MayRequireGate.Passes(null, "author.moda,author.modb", all, any), Is.False);
        }

        [Test]
        public void IsCaseInsensitive()
        {
            // ProvenanceRecorder/DirtySetComputer treat packageIds case-insensitively; the real
            // ModLister lowercases too. A MayRequire whose casing differs from the active id passes.
            var (all, any) = Oracles("author.moda");
            Assert.That(MayRequireGate.Passes("Author.ModA", null, all, any), Is.True);
        }

        [Test]
        public void BothAttributes_Compose_AsAnd()
        {
            var (all, any) = Oracles("author.required", "author.optionb");
            // MayRequire satisfied AND one of MayRequireAnyOf satisfied -> kept.
            Assert.That(MayRequireGate.Passes(
                "author.required", "author.optiona,author.optionb", all, any), Is.True);

            // MayRequire satisfied but neither MayRequireAnyOf option active -> dropped.
            var (all2, any2) = Oracles("author.required");
            Assert.That(MayRequireGate.Passes(
                "author.required", "author.optiona,author.optionb", all2, any2), Is.False);

            // MayRequireAnyOf satisfied but MayRequire's mod absent -> dropped.
            var (all3, any3) = Oracles("author.optionb");
            Assert.That(MayRequireGate.Passes(
                "author.required", "author.optiona,author.optionb", all3, any3), Is.False);
        }
    }
}
