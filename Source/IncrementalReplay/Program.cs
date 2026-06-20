// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;

namespace Gagarin.IncrementalReplay
{
    // Console entry point AND the automated test matrix. Every case mutates a single
    // mod and asserts the spliced incremental result is byte-for-byte (canonically)
    // identical to a full rebuild. A non-zero exit code means at least one case failed,
    // so this doubles as a CI gate.

    internal static class Program
    {
        private delegate Fixture Mutator(Fixture baseline);

        private sealed class Case
        {
            public string Name;
            public string ChangedMod;
            public Mutator Mutate;
            public string Expectation;
        }

        private static int Main()
        {
            var cases = new List<Case>
            {
                // (a) FRONT of load order, wildcard reaching FORWARD. Broadening the
                //     early wildcard's predicate (flammable -> beauty) makes it sweep
                //     across many later mods' defs: large, but correct, blast radius.
                new Case
                {
                    Name = "a) front-of-order wildcard reaching forward",
                    ChangedMod = "earlywildcard",
                    Expectation = "large blast radius (many forward defs gain fireResist+rated)",
                    Mutate = f =>
                    {
                        var m = f.DeepCopy();
                        foreach (var mod in m.Mods)
                            if (mod.PackageId == "earlywildcard")
                                foreach (var p in mod.Patches)
                                    if (p.PatchId == "earlywildcard#fireproof")
                                        p.PredicateElement = "beauty"; // now matches every def with <beauty>
                        return m;
                    }
                },

                // (b) END of load order. Changing Tail's Gold body should dirty only
                //     Gold (and anything that structurally depends on it — nothing here).
                new Case
                {
                    Name = "b) end-of-order leaf change",
                    ChangedMod = "tail",
                    Expectation = "small subgraph (just Gold)",
                    Mutate = f =>
                    {
                        var m = f.DeepCopy();
                        foreach (var mod in m.Mods)
                            if (mod.PackageId == "tail")
                                foreach (var d in mod.Defs)
                                    if (d.DefName == "Gold")
                                        d.Body = "<beauty>9</beauty><marketValue>10</marketValue>";
                        return m;
                    }
                },

                // (c) WORST CASE: change a mod (Steelworks) targeted by many wildcard
                //     patches. Make SteelPlate gain <flammable> so the EARLY forward
                //     wildcard now hits it too (the cross-mod hazard from the other side).
                new Case
                {
                    Name = "c) worst-case: mod targeted by many wildcards",
                    ChangedMod = "steelworks",
                    Expectation = "SteelPlate flips into early wildcard + chains match sets",
                    Mutate = f =>
                    {
                        var m = f.DeepCopy();
                        foreach (var mod in m.Mods)
                            if (mod.PackageId == "steelworks")
                                foreach (var d in mod.Defs)
                                    if (d.DefName == "SteelPlate")
                                        // add <flammable> -> now caught by earlywildcard#fireproof,
                                        // which adds <fireResist>, which chains#fireflag then catches.
                                        d.Body = "<flammable>true</flammable><beauty>1</beauty><marketValue>3</marketValue>";
                        return m;
                    }
                },

                // (d) Extra hazard: change a LATE mod's def to gain <flammable>, proving
                //     an EARLIER mod's forward wildcard correctly re-fires on it.
                new Case
                {
                    Name = "d) late def gains element matched by earlier wildcard",
                    ChangedMod = "tail",
                    Expectation = "Gold flips into earlywildcard + chains match sets",
                    Mutate = f =>
                    {
                        var m = f.DeepCopy();
                        foreach (var mod in m.Mods)
                            if (mod.PackageId == "tail")
                                foreach (var d in mod.Defs)
                                    if (d.DefName == "Gold")
                                        d.Body = "<flammable>true</flammable><beauty>5</beauty><marketValue>10</marketValue>";
                        return m;
                    }
                },

                // (e) Cross-mod inheritance: change Core's BaseApparel template; Armory's
                //     Vest (in another mod) must be re-resolved.
                new Case
                {
                    Name = "e) cross-mod inheritance parent change",
                    ChangedMod = "core",
                    Expectation = "Vest (other mod) re-resolved from changed parent",
                    Mutate = f =>
                    {
                        var m = f.DeepCopy();
                        foreach (var mod in m.Mods)
                            if (mod.PackageId == "core")
                                foreach (var d in mod.Defs)
                                    if (d.DefName == "BaseApparel")
                                        d.Body = "<layer>OnSkin</layer><armor>1</armor><insulation>5</insulation>";
                        return m;
                    }
                },
            };

            var baseline = SyntheticFixture.Build();

            Console.WriteLine("=== Incremental Replay Harness ===");
            Console.WriteLine($"Baseline: {baseline.Mods.Count} mods, "
                + $"{ApplyModel.FullRebuild(baseline).DocumentElement.ChildNodes.Count} resolved defs\n");

            bool allPass = true;
            foreach (var c in cases)
            {
                var mutated = c.Mutate(baseline);
                // Run a few times and take the best timing to reduce JIT/GC noise.
                ReplayOutcome best = null;
                for (int i = 0; i < 5; i++)
                {
                    var o = Harness.Run(c.Name, baseline, mutated, c.ChangedMod);
                    if (best == null || o.IncrementalMs < best.IncrementalMs) best = o;
                }

                allPass &= best.ZeroDiff;
                Console.WriteLine(c.Name);
                Console.WriteLine($"  expectation : {c.Expectation}");
                Console.WriteLine($"  dirty set   : {best.DirtyCount}/{best.TotalNodes} nodes "
                    + $"({best.Iterations} fixpoint iters)");
                Console.WriteLine($"  zero-diff   : {(best.ZeroDiff ? "YES (correct)" : "NO  <-- FAIL")}");
                Console.WriteLine($"  timing      : full={best.FullRebuildMs:F3}ms  "
                    + $"incremental={best.IncrementalMs:F3}ms  speedup={best.Speedup:F2}x");
                if (!best.ZeroDiff)
                {
                    Console.WriteLine("  --- diffgram ---");
                    Console.WriteLine(best.DiffGram);
                }
                Console.WriteLine();
            }

            Console.WriteLine(allPass
                ? "ALL CASES ZERO-DIFF -> incremental recompute is correct on this fixture."
                : "FAILURES PRESENT -> dirty set is unsound; see diffgrams above.");

            // --- Scaled benchmark: prove the speedup grows with load size ---
            Console.WriteLine("\n=== Scaled speedup benchmark (single end-of-order leaf change) ===");
            Console.WriteLine("(graph + baseline Unified.xml + loaded indices treated as cached; "
                + "timed = dirty-set + targeted recompute + splice.");
            Console.WriteLine(" Note: splice re-emits the doc O(load) and is the residual cap; "
                + "a true in-place DOM mutation would push speedup higher.)\n");
            foreach (var (mods, defs) in new[] { (50, 50), (200, 50), (500, 50) })
            {
                var big = ScaledFixture.Build(mods, defs);
                // Mutate a single def in the LAST mod's block (end-of-order leaf change).
                var changedMod = "mod" + (mods - 1);
                var mutated = big.DeepCopy();
                foreach (var mod in mutated.Mods)
                    if (mod.PackageId == changedMod)
                        mod.Defs[mod.Defs.Count - 1].Body = "<beauty>4</beauty><marketValue>99</marketValue>";

                var o = Harness.RunBenchmark($"{mods} mods x {defs} defs", big, mutated, changedMod, 7);
                allPass &= o.ZeroDiff;
                Console.WriteLine($"{o.TotalNodes,6} defs | dirty {o.DirtyCount,4} "
                    + $"| zero-diff {(o.ZeroDiff ? "YES" : "NO ")} "
                    + $"| full {o.FullRebuildMs,8:F2}ms | incr {o.IncrementalMs,7:F3}ms "
                    + $"| speedup {o.Speedup,7:F1}x");
            }

            // --- Negative control: prove the harness CATCHES an unsound dirty set ---
            // A naive "reuse unchanged prefix" only re-resolves the changed mod's OWN
            // defs. When the changed mod is EarlyWildcard (which declares NO defs, only a
            // forward wildcard patch), broadening that wildcard's predicate must dirty
            // every forward def it now sweeps -- but the naive set is EMPTY, so it splices
            // nothing and silently diverges from the full rebuild. This MUST produce a
            // non-zero diff; if it didn't, the harness wouldn't be detecting the hazard.
            Console.WriteLine("\n=== Negative control: naive prefix-reuse on the hazard case ===");
            {
                var mutated = baseline.DeepCopy();
                foreach (var mod in mutated.Mods)
                    if (mod.PackageId == "earlywildcard")
                        foreach (var p in mod.Patches)
                            if (p.PatchId == "earlywildcard#fireproof")
                                p.PredicateElement = "beauty"; // forward wildcard now sweeps everything

                bool naiveZeroDiff = Harness.RunNaive(baseline, mutated, "earlywildcard");
                Console.WriteLine($"  naive dirty set (changed mod's own defs only): "
                    + $"zero-diff = {(naiveZeroDiff ? "YES" : "NO")}");
                Console.WriteLine(naiveZeroDiff
                    ? "  UNEXPECTED: naive reuse looked correct -> harness is NOT exercising the hazard!"
                    : "  EXPECTED: naive reuse is WRONG (forward wildcard missed) -> harness detects the hazard.");
                // The sound algorithm must succeed exactly where the naive one fails.
                allPass &= !naiveZeroDiff;
            }

            return allPass ? 0 : 1;
        }
    }
}
