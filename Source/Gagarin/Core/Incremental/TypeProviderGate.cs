// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// TypeProviderGate.cs (Piece D — recompute fidelity for custom Def-type providers, issue #86)
//
// Contains: a pure mirror of the root-level type-resolution gate the real loader applies before
// a def is registered. In Assembly-CSharp 1.6, DirectXmlLoader.DefFromNode resolves the node's
// .NET Type by element name and drops the def entirely (Log.ErrorOnce, then `return null` -- it
// never reaches the cache) when that Type cannot be found in ANY loaded assembly:
//
//     Type typeInAnyAssembly = GenTypes.GetTypeInAnyAssembly(node.Name);
//     if (typeInAnyAssembly == null || !GenTypes.IsDef(typeInAnyAssembly))
//     {
//         Log.ErrorOnce(...);
//         return null;
//     }
//
// Unlike MayRequire (see MayRequireGate.cs), that failure mode isn't visible in the def's own XML
// attributes at all -- a custom Def type simply stops existing when the mod whose C# assembly
// declared it leaves the load, for every OTHER mod's XML that instantiates it (the
// FacialAnimation.EyeballColorDef / nals.facialanimation case #40 and #86 were split from). The
// only place that ownership is recorded is the captured graph's typeProviders index
// (DependencyGraphModel.TypeProviderIndex, written by ProvenanceRecorder.RegisterNode via
// AssemblyOwnerLookup). DefRecompute inverts it once per call (BuildTypeProviderByNodeId) and
// looks up each dirty concrete id's provider, if any, before massaging it into the splice.
//
// Why a separate pure gate rather than inlining the check: keeps the load-bearing "is the
// provider mod still active" decision offline-testable, exactly like MayRequireGate, so
// DefRecompute (RimWorld-coupled, gate-only validated) stays a thin wire-up of already-pinned
// pieces.

using System;
using System.Collections.Generic;

namespace Gagarin
{
    public static class TypeProviderGate
    {
        // True if a def node whose .NET Type could have been supplied by any of
        // `providerPackageIds` (null/empty when the node's Type came from a base-game/vanilla
        // assembly -- i.e. no gate applies at all) would still resolve under the CURRENT mod
        // list, i.e. should be KEPT in the recomputed cache. False means every candidate
        // providing mod has left the load, so RimWorld can no longer construct instances of
        // that Type in ANY mod's XML -- the def should be spliced out, treated the same as a
        // deletion.
        //
        // More than one candidate can be recorded for a single node: two mods can share an
        // identity-matching assembly (.NET/Mono's Assembly.LoadFrom binding can resolve both
        // to the same Assembly object -- see AssemblyOwnerLookup), so removing whichever one
        // happened to be captured must NOT drop the def if another recorded candidate is still
        // active. Passing as long as ANY candidate is active is the correct, conservative
        // reading: the def only becomes unconstructable once every possible source is gone.
        //
        // anyActive is the ModLister oracle (DefRecompute supplies ModLister.AnyModActiveNoSuffix
        // in-game, matching MayRequireGate's usage); tests supply a synthetic lambda.
        public static bool Passes(IEnumerable<string> providerPackageIds, Func<IEnumerable<string>, bool> anyActive)
        {
            if (providerPackageIds == null)
                return true;
            List<string> ids = providerPackageIds as List<string> ?? new List<string>(providerPackageIds);
            return ids.Count == 0 || anyActive(ids);
        }

        // Pulled out of DefRecompute so the inversion is offline-testable rather than only
        // provable by a live run. DefRecompute's own method is RimWorld-coupled (ModContentPack,
        // XmlInheritance, ModLister) and can't run in the offline suite, but this is a pure
        // DependencyGraphData transform. Unconditional since the 2026-07-22 --expect-typeprovider
        // live run confirmed recomputeMismatches==0 (issue #86 PR 2) -- no gating flag anymore,
        // matching MayRequireGate's wiring.
        public static Dictionary<string, List<string>> ResolveProviderIndex(DependencyGraphData graph)
        {
            return graph?.BuildTypeProviderByNodeId() ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }
    }
}
