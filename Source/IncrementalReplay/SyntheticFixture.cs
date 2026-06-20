// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// SyntheticFixture.cs (Piece C — replay harness)
//
// Contains: SyntheticFixture.Build — the hand-authored baseline load order, with
// mods engineered to embed every correctness hazard (forward-reaching wildcard,
// cross-mod inheritance, patch-depends-on-patch-output, and overlapping vs
// independent reorder pairs).
//
// Used for: the deterministic baseline that the Program change-case matrix mutates
// and asserts against.
//
// Why: a curated fixture makes the hazards explicit and reproducible, so each test
// case targets a known failure mode of naive prefix-reuse rather than relying on
// incidental structure to surface bugs.

namespace Gagarin.IncrementalReplay
{
    // The standalone universe the harness reasons about. Seven "mods" in load order,
    // engineered to contain every correctness hazard that makes naive prefix-reuse wrong:
    //
    //   * Core (order 0) declares base defs and a base Name'd template (BaseApparel).
    //   * EarlyWildcard (order 1) runs a WILDCARD patch keyed on <flammable> that
    //     reaches FORWARD to defs declared by LATER mods. This is THE hazard: change a
    //     late mod's def to gain/lose <flammable> and an early patch's blast radius moves.
    //   * Steelworks (order 2) is heavily targeted by wildcard patches (worst case).
    //   * Armory (order 3) declares apparel that inherits BaseApparel from Core
    //     (cross-mod inheritance).
    //   * Chains (order 4) has a patch that depends on the OUTPUT of EarlyWildcard's
    //     patch (it keys on the element EarlyWildcard sets), exercising the fixpoint walk.
    //   * LateAddon (order 5) declares a def that EarlyWildcard's forward wildcard hits.
    //   * Tail (order 6) is a leaf mod at the END of load order whose change should
    //     dirty only a small subgraph.

    internal static class SyntheticFixture
    {
        public static Fixture Build()
        {
            var f = new Fixture();

            // --- Core (0): bases + a Name'd inheritance template ---
            var core = new FixtureMod { PackageId = "core", LoadOrder = 0 };
            core.Defs.Add(new FixtureDef
            {
                DefType = "ThingDef", DefName = "Steel",
                Body = "<flammable>false</flammable><beauty>0</beauty><marketValue>1</marketValue>"
            });
            core.Defs.Add(new FixtureDef
            {
                DefType = "ThingDef", DefName = "Wood",
                Body = "<flammable>true</flammable><beauty>2</beauty><marketValue>1</marketValue>"
            });
            core.Defs.Add(new FixtureDef
            {
                DefType = "ThingDef", DefName = "BaseApparel", Name = "BaseApparel",
                Body = "<layer>OnSkin</layer><armor>1</armor>"
            });
            f.Mods.Add(core);

            // --- EarlyWildcard (1): forward-reaching wildcard on <flammable> ---
            var ew = new FixtureMod { PackageId = "earlywildcard", LoadOrder = 1 };
            ew.Patches.Add(new FixturePatch
            {
                PatchId = "earlywildcard#fireproof", SourceMod = "earlywildcard",
                Kind = PatchKind.Wildcard, DefType = "ThingDef",
                PredicateElement = "flammable",            // matches ANY ThingDef with <flammable>
                SetElement = "fireResist", SetValue = "high"
            });
            f.Mods.Add(ew);

            // --- Steelworks (2): worst-case target of many wildcard patches ---
            var sw = new FixtureMod { PackageId = "steelworks", LoadOrder = 2 };
            sw.Defs.Add(new FixtureDef
            {
                DefType = "ThingDef", DefName = "SteelPlate",
                Body = "<flammable>false</flammable><beauty>1</beauty><marketValue>3</marketValue>"
            });
            // A pile of identity patches plus more wildcard patches keyed on <beauty>,
            // all hammering Steelworks' defs (and any other def with <beauty>).
            sw.Patches.Add(new FixturePatch
            {
                PatchId = "steelworks#beauty", SourceMod = "steelworks",
                Kind = PatchKind.Wildcard, DefType = "ThingDef",
                PredicateElement = "beauty", SetElement = "polished", SetValue = "yes"
            });
            sw.Patches.Add(new FixturePatch
            {
                PatchId = "steelworks#steelvalue", SourceMod = "steelworks",
                Kind = PatchKind.Identity, DefType = "ThingDef", TargetDefName = "Steel",
                SetElement = "marketValue", SetValue = "2"
            });
            f.Mods.Add(sw);

            // --- Armory (3): cross-mod inheritance from Core's BaseApparel ---
            var arm = new FixtureMod { PackageId = "armory", LoadOrder = 3 };
            arm.Defs.Add(new FixtureDef
            {
                DefType = "ThingDef", DefName = "Vest", ParentName = "BaseApparel",
                Body = "<armor>3</armor><beauty>1</beauty>"   // overrides armor, adds beauty
            });
            f.Mods.Add(arm);

            // --- Chains (4): patch that depends on EarlyWildcard's OUTPUT (<fireResist>) ---
            var ch = new FixtureMod { PackageId = "chains", LoadOrder = 4 };
            ch.Patches.Add(new FixturePatch
            {
                PatchId = "chains#fireflag", SourceMod = "chains",
                Kind = PatchKind.Wildcard, DefType = "ThingDef",
                PredicateElement = "fireResist",   // only present AFTER EarlyWildcard ran
                SetElement = "rated", SetValue = "true"
            });
            f.Mods.Add(ch);

            // --- LateAddon (5): a def the early forward wildcard reaches ---
            var la = new FixtureMod { PackageId = "lateaddon", LoadOrder = 5 };
            la.Defs.Add(new FixtureDef
            {
                DefType = "ThingDef", DefName = "Plastic",
                Body = "<flammable>true</flammable><beauty>0</beauty><marketValue>2</marketValue>"
            });
            f.Mods.Add(la);

            // --- Tail (6): leaf mod at the very end of load order ---
            var tail = new FixtureMod { PackageId = "tail", LoadOrder = 6 };
            tail.Defs.Add(new FixtureDef
            {
                DefType = "ThingDef", DefName = "Gold",
                Body = "<beauty>5</beauty><marketValue>10</marketValue>"
            });
            // An identity patch on its own def — small, contained blast radius.
            tail.Patches.Add(new FixturePatch
            {
                PatchId = "tail#goldvalue", SourceMod = "tail",
                Kind = PatchKind.Identity, DefType = "ThingDef", TargetDefName = "Gold",
                SetElement = "marketValue", SetValue = "15"
            });
            f.Mods.Add(tail);

            // --- Reorder pair: two mods whose patches OVERLAP on the same node/element ---
            // PaintRed (7) and PaintBlue (8) both set Steel's <tint>. Patches apply in load
            // order, so whichever loads LAST wins -> swapping them changes the result, and
            // those nodes MUST land in the dirty set on a reorder.
            var red = new FixtureMod { PackageId = "paintred", LoadOrder = 7 };
            red.Patches.Add(new FixturePatch
            {
                PatchId = "paintred#tint", SourceMod = "paintred",
                Kind = PatchKind.Identity, DefType = "ThingDef", TargetDefName = "Steel",
                SetElement = "tint", SetValue = "red"
            });
            f.Mods.Add(red);

            var blue = new FixtureMod { PackageId = "paintblue", LoadOrder = 8 };
            blue.Patches.Add(new FixturePatch
            {
                PatchId = "paintblue#tint", SourceMod = "paintblue",
                Kind = PatchKind.Identity, DefType = "ThingDef", TargetDefName = "Steel",
                SetElement = "tint", SetValue = "blue"   // loads after red -> blue wins
            });
            f.Mods.Add(blue);

            // --- Independent pair: two mods whose patches DO NOT overlap ---
            // HoneTail (9) and HoneWood (10) tune different defs/elements, so swapping them
            // leaves the result identical: their nodes must stay CLEAN (no over-invalidation).
            var honeTail = new FixtureMod { PackageId = "honetail", LoadOrder = 9 };
            honeTail.Patches.Add(new FixturePatch
            {
                PatchId = "honetail#beauty", SourceMod = "honetail",
                Kind = PatchKind.Identity, DefType = "ThingDef", TargetDefName = "Gold",
                SetElement = "beauty", SetValue = "7"
            });
            f.Mods.Add(honeTail);

            var honeWood = new FixtureMod { PackageId = "honewood", LoadOrder = 10 };
            honeWood.Patches.Add(new FixturePatch
            {
                PatchId = "honewood#value", SourceMod = "honewood",
                Kind = PatchKind.Identity, DefType = "ThingDef", TargetDefName = "Wood",
                SetElement = "marketValue", SetValue = "4"
            });
            f.Mods.Add(honeWood);

            return f;
        }
    }
}
