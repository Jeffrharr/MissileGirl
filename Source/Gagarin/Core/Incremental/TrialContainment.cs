// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// TrialContainment.cs (issue #72 — trial-execution escape hatch, pure half)
//
// Contains: the pure containment decision for TrialExecution.cs (the RimWorld-coupled driver
// that actually replays a declined mod's patches against a scoped scratch doc). Given the
// top-level ids the trial discovered as NEW (TrialExecution.Result.TrialTargetIds, sourced from
// PatchInjectedAdds.NewTopLevelDefIds), decide whether they are safe to fold into the recompute
// context or whether the mod reached content outside the provably-safe universe.
//
// Why "new relative to dirty/context" isn't automatically safe: TrialExecution's scratch doc is
// built from EXACTLY the dirty + context ids, so by construction it can never contain, and the
// trial can never "touch", any node the doc didn't start with -- an op whose real target is
// outside that scope simply finds nothing (a leaf op no-ops; a PatchOperationSequence aborts).
// So a trialTargetId can only ever be a top-level element the mod's own patches just CREATED
// (e.g. a PatchOperationAdd targeting xpath="Defs"). That is not automatically safe to splice in
// as fresh content, though: its defName might collide with a REAL def that already exists
// elsewhere in the full, unscoped universe (captured in the prior graph's Nodes) but simply
// wasn't part of dirty/context this load. Admitting a colliding id as "new content" would
// silently duplicate/clobber that real def instead of recomputing it faithfully -- so any
// trialTargetId that IS a known graph node is reported as overflow, and the caller must keep
// declining rather than guess.
//
// Why pure: this is a set-membership check against DependencyGraphData ids, no RimWorld types --
// unit-tested offline exactly like SubDocExpander/RecomputeAllowlist.

using System;
using System.Collections.Generic;

namespace Gagarin
{
    public static class TrialContainment
    {
        // trialTargetIds: top-level ids the trial run created that were not already part of the
        // dirty/context set fed into it. knownGraphNodeIds: every node id the PRIOR graph knows
        // about (DependencyGraphData.Nodes), regardless of whether it is dirty/context this load.
        //
        // Returns true (overflow empty) when every trialTargetId is genuinely new -- absent from
        // the prior graph entirely, so there is no existing baseline value it could clobber.
        // Returns false (overflow populated, sorted for a deterministic reason string) the moment
        // any trialTargetId collides with a real, already-known def outside the scope we vetted.
        public static bool IsContained(
            ICollection<string> trialTargetIds,
            ICollection<string> knownGraphNodeIds,
            out List<string> overflow)
        {
            overflow = new List<string>();
            if (trialTargetIds == null || trialTargetIds.Count == 0)
                return true;

            var known = knownGraphNodeIds as HashSet<string>
                ?? new HashSet<string>(knownGraphNodeIds ?? Array.Empty<string>(), StringComparer.Ordinal);

            foreach (string id in trialTargetIds)
                if (known.Contains(id))
                    overflow.Add(id);

            if (overflow.Count > 1)
                overflow.Sort(StringComparer.Ordinal);
            return overflow.Count == 0;
        }
    }
}
