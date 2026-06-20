// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// Fixture.cs (Piece C — replay harness)
//
// Contains: the fixture data model — FixtureMod, FixtureDef, the PatchKind enum,
// FixturePatch, and the Fixture aggregate with load-order enumeration and a
// DeepCopy used to derive mutated variants.
//
// Used for: describing a synthetic load order (mods, defs, patches) that is both
// the harness's input and its ground truth, with no dependency on the real game.
//
// Why: a hand-authored universe lets us deliberately encode the three hazards that
// break naive prefix-reuse — a forward-reaching wildcard, cross-mod inheritance,
// and a patch depending on another patch's output — so the dirty-set algorithm is
// tested against the cases that actually matter.

using System.Collections.Generic;

namespace Gagarin.IncrementalReplay
{
    // A synthetic stand-in for RimWorld's load order. Each "mod" contributes a few
    // raw def documents plus patch operations, in a fixed load order. This is enough
    // to exercise the three hazards that make naive prefix-reuse wrong:
    //   * an early mod's WILDCARD patch matching a LATER mod's def,
    //   * cross-mod ParentName inheritance,
    //   * a patch that depends on the OUTPUT of an earlier patch.
    // We never touch the real game; the fixture is the entire universe the harness
    // reasons about, so it doubles as both the input and the ground truth.

    internal sealed class FixtureMod
    {
        public string PackageId;
        public int LoadOrder;

        // Raw def nodes this mod declares (defType, defName, and a snippet of body XML).
        public List<FixtureDef> Defs = new List<FixtureDef>();

        // Patch operations this mod runs, in declaration order.
        public List<FixturePatch> Patches = new List<FixturePatch>();
    }

    internal sealed class FixtureDef
    {
        public string DefType;
        public string DefName;

        // Optional ParentName for inheritance (may resolve to a parent in ANOTHER mod).
        public string ParentName;

        // Optional Name (so other defs can inherit from this one).
        public string Name;

        // Inner body XML (everything between <DefType>...</DefType>, excluding the
        // ParentName/Name attributes which live on the element). Kept as a string so
        // the fixture reads like real def XML.
        public string Body;

        public string NodeId => DefType + "/" + DefName;
    }

    // Mirrors the kinds Piece B classifies. We only implement the two operation
    // shapes the dirty-set algorithm actually has to reason about differently:
    // identity (targets a specific defName) and wildcard (targets by predicate and
    // can sweep across mods).
    internal enum PatchKind
    {
        // xpath pins an exact defName, e.g. .../ThingDef[defName="Steel"]/...
        Identity,

        // xpath matches by a non-identity predicate or wildcard, e.g.
        // .../ThingDef[statBases/Flammable]/... — may hit defs from any mod.
        Wildcard
    }

    internal sealed class FixturePatch
    {
        public string PatchId;
        public string SourceMod;
        public PatchKind Kind;

        // The def type this patch's xpath is rooted at (Defs/ThingDef[...]).
        public string DefType;

        // For Identity patches: the exact defName targeted.
        public string TargetDefName;

        // For Wildcard patches: the child element whose PRESENCE selects a def
        // (predicate). A def is matched iff its resolved body contains this element.
        // This is a deliberately small but representative predicate model.
        public string PredicateElement;

        // The element to set/replace inside each matched def, and its new value.
        // (A faithful stand-in for PatchOperationReplace / PatchOperationAdd.)
        public string SetElement;
        public string SetValue;
    }

    internal sealed class Fixture
    {
        public List<FixtureMod> Mods = new List<FixtureMod>();

        // Convenience: mods in load order.
        public IEnumerable<FixtureMod> InLoadOrder()
        {
            var copy = new List<FixtureMod>(Mods);
            copy.Sort((a, b) => a.LoadOrder.CompareTo(b.LoadOrder));
            return copy;
        }

        public Fixture DeepCopy()
        {
            var f = new Fixture();
            foreach (var m in Mods)
            {
                var nm = new FixtureMod { PackageId = m.PackageId, LoadOrder = m.LoadOrder };
                foreach (var d in m.Defs)
                    nm.Defs.Add(new FixtureDef
                    {
                        DefType = d.DefType, DefName = d.DefName, ParentName = d.ParentName,
                        Name = d.Name, Body = d.Body
                    });
                foreach (var p in m.Patches)
                    nm.Patches.Add(new FixturePatch
                    {
                        PatchId = p.PatchId, SourceMod = p.SourceMod, Kind = p.Kind,
                        DefType = p.DefType, TargetDefName = p.TargetDefName,
                        PredicateElement = p.PredicateElement, SetElement = p.SetElement,
                        SetValue = p.SetValue
                    });
                f.Mods.Add(nm);
            }
            return f;
        }
    }
}
