// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// MayRequireGate.cs (Piece D — recompute fidelity for MayRequire)
//
// Contains: a pure mirror of the root-level MayRequire / MayRequireAnyOf gate the real loader
// applies before a def is registered. In Assembly-CSharp 1.6, LoadedModManager.ParseAndProcessXML
// runs this exact test on every def node and `continue`s (drops the def entirely — it never reaches
// the cache) when it fails:
//
//     string text = node.Attributes?["MayRequire"]?.Value.ToLower();
//     if (text != null && !ModLister.AllModsActiveNoSuffix(text.Split(',')))
//         continue;                                     // ALL listed mods must be active
//     string[] anyOf = node.Attributes?["MayRequireAnyOf"]?.Value.ToLower().Split(',');
//     if (!anyOf.NullOrEmpty() && !ModLister.AnyModActiveNoSuffix(anyOf))
//         continue;                                     // AT LEAST ONE listed mod must be active
//
// Why it exists separately from DefRecompute: DefRecompute is RimWorld-coupled (real engine, gate-
// only validation), but the load-bearing part here is the AND-vs-OR / lowercase / comma-split
// semantics, which we want to pin offline. So the decision is a pure function taking the two raw
// attribute values plus the two ModLister oracles as delegates; DefRecompute supplies the real
// ModLister.AllModsActiveNoSuffix / AnyModActiveNoSuffix in-game, tests supply lambdas. This keeps
// the recompute byte-faithful to a full rebuild: a dirty def whose gating mod left the load is
// dropped from the splice exactly as the rebuild dropped it, instead of being recomputed as present.

using System;
using System.Collections.Generic;

namespace Gagarin
{
    public static class MayRequireGate
    {
        // True if a def node carrying these (possibly null) MayRequire / MayRequireAnyOf attribute
        // values would be loaded by the real engine, i.e. should be KEPT in the recomputed cache.
        // False if the engine would drop it, i.e. it should be spliced out (treated as removed).
        //
        // mayRequire / mayRequireAnyOf are the raw attribute values (null when the attribute is
        // absent — read them via node.Attributes["X"]?.Value, NOT GetAttribute which returns "").
        // allActive / anyActive are the ModLister oracles: allActive returns true iff every id is an
        // active mod (suffix-insensitive); anyActive returns true iff at least one id is.
        public static bool Passes(string mayRequire, string mayRequireAnyOf,
            Func<IEnumerable<string>, bool> allActive, Func<IEnumerable<string>, bool> anyActive)
        {
            // MayRequire: present (non-null) means ALL listed packageIds must be active. Mirrors the
            // loader's `text != null` guard (an absent attribute is null and skips the check).
            if (mayRequire != null)
            {
                string[] all = mayRequire.ToLower().Split(',');
                if (!allActive(all))
                    return false;
            }

            // MayRequireAnyOf: present and non-empty means AT LEAST ONE listed packageId must be
            // active. The loader splits first then checks NullOrEmpty on the array, so an absent
            // attribute (null) short-circuits; we match that ordering.
            string[] anyOf = mayRequireAnyOf?.ToLower().Split(',');
            if (anyOf != null && anyOf.Length > 0 && !anyActive(anyOf))
                return false;

            return true;
        }
    }
}
