// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// Program.cs (Piece C — replay harness)
//
// Contains: the console entry point and the automated change-case matrix —
// update, reorder (overlap/independent), add, remove, cross-mod inheritance, and
// forward-wildcard hazard cases — plus the scaled benchmark and naive negative
// control.
//
// Used for: running every case, asserting zero-diff and explicit dirty/clean
// membership, printing timings, and returning a non-zero exit code on any failure
// so the harness doubles as a CI gate.
//
// Why: this is where the incremental-cache project gets its go/no-go answer; the
// cases are engineered to cover exactly the hazards (including the packageId-keyed
// staleness bug Gagarin's current cache gets wrong) that make naive reuse unsafe.

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

            // Optional membership assertions on the computed dirty set. ExpectDirty proves
            // we DID invalidate a node whose value moved (no under-invalidation); ExpectClean
            // proves we did NOT invalidate a node whose value is unchanged (no over-
            // invalidation). Both are checked in addition to the zero-diff oracle.
            public string[] ExpectDirty;
            public string[] ExpectClean;
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

                // (f) UPDATE: a mod's content changes (same packageId, different body).
                //     This is precisely the case Gagarin's current cache gets WRONG -- it
                //     keys cache validity on packageId, not content, so an in-place mod
                //     update (Steam auto-update, local edit) is NOT detected and the stale
                //     Unified.xml is reused. Here Steelworks' SteelPlate beauty changes; the
                //     dirty set must catch SteelPlate (and nothing structurally beyond it).
                //     Doubles as the regression test for that packageId-keyed staleness bug.
                new Case
                {
                    Name = "f) UPDATE: same packageId, changed content (Gagarin-cache regression)",
                    ChangedMod = "steelworks",
                    Expectation = "only SteelPlate dirty; stale-cache bug would miss it",
                    ExpectDirty = new[] { "ThingDef/SteelPlate" },
                    ExpectClean = new[] { "ThingDef/Gold", "ThingDef/Wood" },
                    Mutate = f =>
                    {
                        var m = f.DeepCopy();
                        foreach (var mod in m.Mods)
                            if (mod.PackageId == "steelworks")
                                foreach (var d in mod.Defs)
                                    if (d.DefName == "SteelPlate")
                                        d.Body = "<flammable>false</flammable><beauty>8</beauty><marketValue>3</marketValue>";
                        return m;
                    }
                },

                // (g) REORDER, OVERLAP: swap PaintRed/PaintBlue, which both set Steel's
                //     <tint>. Patches apply in load order, so swapping flips the winner
                //     (blue -> red). No def body and no patch DEFINITION changed -- only the
                //     order -- so a content-diff would miss it. Steel MUST be dirty.
                new Case
                {
                    Name = "g) REORDER (overlap): swap two mods that patch the SAME node",
                    ChangedMod = "paintred",
                    Expectation = "Steel result changes -> Steel MUST be dirty",
                    ExpectDirty = new[] { "ThingDef/Steel" },
                    Mutate = f => SwapLoadOrder(f, "paintred", "paintblue")
                },

                // (h) REORDER, INDEPENDENT: swap HoneTail/HoneWood, which patch DIFFERENT
                //     defs (Gold's beauty vs Wood's marketValue). Order cannot matter on
                //     disjoint targets, so the result is identical and those nodes must stay
                //     CLEAN -- proving the dirty set does not over-invalidate on reorder.
                new Case
                {
                    Name = "h) REORDER (independent): swap two mods with disjoint patches",
                    ChangedMod = "honetail",
                    Expectation = "result identical -> Gold/Wood stay CLEAN",
                    ExpectClean = new[] { "ThingDef/Gold", "ThingDef/Wood" },
                    Mutate = f => SwapLoadOrder(f, "honetail", "honewood")
                },

                // (i) ADD: a brand-new mod whose WILDCARD patch reaches BACK into existing
                //     defs. NewGloss adds <polish> to every ThingDef with <beauty>, so many
                //     existing nodes must be dirtied even though none of THEIR bodies changed.
                new Case
                {
                    Name = "i) ADD: new mod whose wildcard reaches back into existing defs",
                    ChangedMod = "newgloss",
                    Expectation = "every <beauty> def gains <polish> -> all dirtied",
                    ExpectDirty = new[] { "ThingDef/Steel", "ThingDef/Gold" },
                    Mutate = f =>
                    {
                        var m = f.DeepCopy();
                        var gloss = new FixtureMod { PackageId = "newgloss", LoadOrder = 11 };
                        gloss.Patches.Add(new FixturePatch
                        {
                            PatchId = "newgloss#polish", SourceMod = "newgloss",
                            Kind = PatchKind.Wildcard, DefType = "ThingDef",
                            PredicateElement = "beauty", SetElement = "polish", SetValue = "matte"
                        });
                        m.Mods.Add(gloss);
                        return m;
                    }
                },

                // (j) REMOVE: drop Steelworks entirely. Its identity patch on Steel
                //     (marketValue=2) and wildcard on <beauty> (polished=yes) must be
                //     UN-APPLIED, and its own def SteelPlate must disappear. Tests that
                //     removal correctly rolls patches back rather than leaving their effects.
                new Case
                {
                    Name = "j) REMOVE: drop a mod -> its patches un-applied, its defs gone",
                    ChangedMod = "steelworks",
                    Expectation = "SteelPlate removed; Steel/beauty-defs lose Steelworks' edits",
                    ExpectDirty = new[] { "ThingDef/SteelPlate", "ThingDef/Steel" },
                    Mutate = f =>
                    {
                        var m = f.DeepCopy();
                        m.Mods.RemoveAll(mod => mod.PackageId == "steelworks");
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

                // Membership assertions: zero-diff alone proves the SPLICED result matches,
                // but the reorder cases hinge on WHICH nodes were (or weren't) invalidated,
                // so we check those explicitly too -- a node can stay clean and still match
                // only because nothing else touched it, which we must not rely on silently.
                if (c.ExpectDirty != null)
                    foreach (var id in c.ExpectDirty)
                    {
                        bool ok = best.DirtyNodes.Contains(id);
                        allPass &= ok;
                        Console.WriteLine($"  expect dirty: {id} -> {(ok ? "YES" : "NO  <-- FAIL (under-invalidated)")}");
                    }
                if (c.ExpectClean != null)
                    foreach (var id in c.ExpectClean)
                    {
                        bool ok = !best.DirtyNodes.Contains(id);
                        allPass &= ok;
                        Console.WriteLine($"  expect clean: {id} -> {(ok ? "YES" : "NO  <-- FAIL (over-invalidated)")}");
                    }

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

        // Swap the load order of two mods, leaving every def body and patch definition
        // untouched. This is a pure REORDER: nothing a content-diff can see has changed,
        // only the order patches are applied in -- which the dirty set must still account
        // for whenever two reordered patches overlap on a node.
        private static Fixture SwapLoadOrder(Fixture f, string modA, string modB)
        {
            var m = f.DeepCopy();
            FixtureMod a = null, b = null;
            foreach (var mod in m.Mods)
            {
                if (mod.PackageId == modA) a = mod;
                else if (mod.PackageId == modB) b = mod;
            }
            int tmp = a.LoadOrder;
            a.LoadOrder = b.LoadOrder;
            b.LoadOrder = tmp;
            return m;
        }
    }
}
