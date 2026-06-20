// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;

namespace Gagarin.IncrementalReplay
{
    // A larger synthetic load order used purely to MEASURE speedup honestly. The 7-mod
    // correctness fixture is too small to time meaningfully (everything is dominated by
    // fixed overhead), and a real mod list is thousands of defs. This generator builds
    // a parametric load order so we can show how the incremental path scales relative to
    // a full rebuild as the def count grows.
    //
    // Structure per generated mod: a block of plain defs, plus a couple of identity
    // patches on its own defs, plus the same cross-mod hazards as the hand fixture
    // (one early forward wildcard, one inheritance chain) so the scaled run still
    // exercises the dirty-set logic rather than a trivial best case.

    internal static class ScaledFixture
    {
        public static Fixture Build(int modCount, int defsPerMod)
        {
            var f = new Fixture();
            var rng = new Random(1234);   // deterministic so timings are repeatable

            // Core declares an inheritance template used cross-mod.
            var core = new FixtureMod { PackageId = "core", LoadOrder = 0 };
            core.Defs.Add(new FixtureDef
            {
                DefType = "ThingDef", DefName = "BaseThing", Name = "BaseThing",
                Body = "<beauty>0</beauty><marketValue>1</marketValue>"
            });
            f.Mods.Add(core);

            // One early forward wildcard (the hazard) keyed on a rare element.
            var ew = new FixtureMod { PackageId = "earlywildcard", LoadOrder = 1 };
            ew.Patches.Add(new FixturePatch
            {
                PatchId = "earlywildcard#w", SourceMod = "earlywildcard",
                Kind = PatchKind.Wildcard, DefType = "ThingDef",
                PredicateElement = "flammable", SetElement = "fireResist", SetValue = "high"
            });
            f.Mods.Add(ew);

            for (int i = 0; i < modCount; i++)
            {
                var mod = new FixtureMod { PackageId = "mod" + i, LoadOrder = 2 + i };
                for (int j = 0; j < defsPerMod; j++)
                {
                    string name = $"Def_{i}_{j}";
                    // ~10% of defs carry <flammable> so the early wildcard has real reach.
                    bool flammable = rng.Next(10) == 0;
                    // ~5% inherit the cross-mod base template.
                    bool inherits = rng.Next(20) == 0;
                    var body = (flammable ? "<flammable>true</flammable>" : "")
                        + $"<beauty>{rng.Next(5)}</beauty><marketValue>{rng.Next(20)}</marketValue>";
                    mod.Defs.Add(new FixtureDef
                    {
                        DefType = "ThingDef", DefName = name,
                        ParentName = inherits ? "BaseThing" : null,
                        Body = body
                    });
                }
                // A couple identity patches on this mod's own first defs.
                mod.Patches.Add(new FixturePatch
                {
                    PatchId = $"mod{i}#p0", SourceMod = mod.PackageId, Kind = PatchKind.Identity,
                    DefType = "ThingDef", TargetDefName = $"Def_{i}_0",
                    SetElement = "tuned", SetValue = "yes"
                });
                f.Mods.Add(mod);
            }
            return f;
        }
    }
}
