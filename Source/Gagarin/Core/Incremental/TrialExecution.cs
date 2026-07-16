// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// TrialExecution.cs (issue #72 — trial-execution escape hatch, RimWorld-coupled half)
//
// Contains: the live driver SubDocExpander's two blunt declines fall back to before giving up on
// the incremental path entirely. Both declines (a CHANGED mod owns a container op; a CHANGED mod
// is wholly new this load) are structurally correct but blunt -- they decline whenever static
// analysis of the PRIOR graph can't prove safety, even though the declining mod's raw, unexecuted
// PatchOperations ARE available via ModContentPack.Patches at this point in the load. Real (but
// tightly bounded) execution can sometimes prove safety where static analysis can't -- e.g. a
// genuinely new mod whose entire PatchOperationSequence targets defs already inside dirty/context
// needs no baseline evidence at all; running it for real settles the question outright.
//
// BOUNDED-SCOPE SAFETY (why this cannot repeat the reverted DirtySetDiagnostic.
// ComputeAddedContentFlips hang -- see that file's comment on the history): that approach
// replayed a real mod's full patch list against a scratch copy of the WHOLE current candidate
// document (tens of thousands of defs), which is exactly the shape that stalled a real load.
// TryRun here replays exactly ONE declining mod's full patch list, exactly once, against a
// scratch doc holding ONLY the dirty + context ids already computed this load (a few dozen
// nodes at most) -- never another mod's patches, never the full running-mod list, never the
// full def universe. Any exception (a custom op assuming state this scoped doc doesn't carry) is
// caught and treated as "could not prove safe"; it never escapes to abort the load. This mirrors
// the same "unchanged mods behave as captured, changed mods replay their FULL list" precedent
// DefRecompute.Recompute (step 4) already relies on -- the only new risk here is scope-escape
// (an op reaching outside dirty/context), which TrialContainment.IsContained exists to catch.
//
// Why RimWorld-coupled (not offline-tested): faithfulness requires the real PatchOperation.Apply
// and the real ModContentPack.Patches list, so -- like DefRecompute -- this can only be validated
// in-game via the gate (--expect-trial-execution). The pure half it feeds (TrialContainment) is
// offline-tested; DO NOT add this file to Gagarin.Tests.csproj's compile list.

using System;
using System.Collections.Generic;
using System.Xml;
using MissileGirl;
using RimWorld;
using Verse;

namespace Gagarin
{
    public static class TrialExecution
    {
        // Outcome of replaying one mod's patch list against a scoped scratch doc.
        public sealed class Result
        {
            // True when every patch in the mod's list applied without throwing. False means the
            // trial could not prove the mod safe -- caller must keep declining.
            public bool Success;

            // Top-level ids TrialContainment must vet: ids present in the scratch doc's <Defs>
            // root after replay that were NOT part of the known (dirty ∪ context) set fed in --
            // i.e. brand-new top-level elements the mod's own patches created this trial.
            public HashSet<string> TrialTargetIds = new HashSet<string>(StringComparer.Ordinal);

            // Populated only when Success is false; the exception type + message, for the
            // fallback reason string.
            public string FailureReason;
        }

        // Replay `mod`'s FULL patch list (never any other mod's) against a scratch <Defs> doc
        // built from exactly dirtyIds ∪ contextIds -- the same scope DefRecompute would already
        // give this load, just probed ahead of time so we can decide whether to admit it instead
        // of falling back. See the file header for why this bound rules out the hang risk that
        // sank the reverted ComputeAddedContentFlips approach.
        public static Result TryRun(
            ModContentPack mod, ICollection<string> dirtyIds, ICollection<string> contextIds)
        {
            var result = new Result();
            if (mod?.Patches == null)
            {
                result.Success = true; // nothing to run is trivially safe
                return result;
            }

            try
            {
                var rawById = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
                var idByName = new Dictionary<string, string>(StringComparer.Ordinal);
                var modByRaw = new Dictionary<XmlNode, ModContentPack>();
                DefRecompute.BuildRawIndex(rawById, idByName, modByRaw);

                var known = new HashSet<string>(StringComparer.Ordinal);
                if (dirtyIds != null) foreach (string id in dirtyIds) known.Add(id);
                if (contextIds != null) foreach (string id in contextIds) known.Add(id);

                var subDoc = new XmlDocument();
                XmlElement defsRoot = subDoc.CreateElement("Defs");
                subDoc.AppendChild(defsRoot);
                foreach (string id in known)
                {
                    // Not every dirty/context id necessarily has a raw body this run (e.g. a
                    // context id that no longer exists) -- same tolerance DefRecompute's own
                    // sub-doc build applies; absence just means no node to seed for that id.
                    if (rawById.TryGetValue(id, out XmlElement raw))
                        defsRoot.AppendChild((XmlElement)subDoc.ImportNode(raw, true));
                }

                // The one real side effect: every op in the declining mod's list, in file order,
                // exactly like LoadedModManager.ApplyPatches / DefRecompute step 4's loop -- but
                // scoped to this ONE mod, against this tiny scratch doc, nothing else.
                foreach (PatchOperation patch in mod.Patches)
                    patch.Apply(subDoc);

                result.TrialTargetIds = PatchInjectedAdds.NewTopLevelDefIds(subDoc, known);
                result.Success = true;
            }
            catch (Exception e)
            {
                // Any failure -- a custom op assuming ambient state this scoped doc doesn't have,
                // a null-ref on an absent sibling, anything -- means "could not prove safe", never
                // a crash that escapes and aborts the load.
                result.Success = false;
                result.FailureReason = $"{e.GetType().Name}: {e.Message}";
                Logger.Debug("GAGARIN: trial execution failed", exception: e);
            }
            return result;
        }

        // Attempt to admit every declined mod via trial execution. Returns true (with
        // foldedContext populated: contextIds plus every trial-proven-safe new id) only when
        // EVERY declined mod's trial both ran cleanly and passed TrialContainment; false (with a
        // reason naming the first mod that failed or the specific overflow ids) otherwise, and
        // the caller must keep declining. foldedContext accumulates across mods in the same call
        // so a second declined mod's trial sees the first mod's newly-admitted ids as context too.
        public static bool TryAdmit(
            ICollection<string> declinedMods, ICollection<string> dirtyIds, ICollection<string> contextIds,
            DependencyGraphData graph, out HashSet<string> foldedContext, out string reason)
        {
            foldedContext = contextIds != null
                ? new HashSet<string>(contextIds, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            reason = null;
            if (declinedMods == null || declinedMods.Count == 0)
            {
                reason = "no declined mod named to trial-execute";
                return false;
            }

            var knownGraphNodeIds = new HashSet<string>(StringComparer.Ordinal);
            if (graph?.Nodes != null)
                foreach (GraphNode n in graph.Nodes)
                    if (!string.IsNullOrEmpty(n.Id))
                        knownGraphNodeIds.Add(n.Id);

            var modByPackageId = new Dictionary<string, ModContentPack>(StringComparer.OrdinalIgnoreCase);
            var realOrder = new List<string>();
            foreach (ModContentPack mod in LoadedModManager.RunningMods)
            {
                if (mod?.PackageId == null)
                    continue;
                modByPackageId[mod.PackageId] = mod;
                realOrder.Add(mod.PackageId);
            }

            // Real load order, not declinedMods's incidental HashSet enumeration order --
            // foldedContext accumulates across mods in this loop (see method comment), so a mod
            // processed out of true order could see (or fail to see) an earlier mod's
            // trial-discovered ids for reasons that have nothing to do with the actual mod list.
            // Offline-tested only (ModLoadOrderTests) -- issue #83 tracks a live fixture with 2+
            // declined mods to prove ordering actually changes the outcome vs. raw HashSet order.
            foreach (string packageId in ModLoadOrder.Sort(declinedMods, realOrder))
            {
                if (!modByPackageId.TryGetValue(packageId, out ModContentPack mod))
                {
                    reason = $"{packageId} not found among running mods";
                    return false;
                }

                Result trial = TryRun(mod, dirtyIds, foldedContext);
                if (!trial.Success)
                {
                    reason = $"{packageId} could not prove safe ({trial.FailureReason})";
                    return false;
                }

                if (!TrialContainment.IsContained(trial.TrialTargetIds, knownGraphNodeIds, out List<string> overflow))
                {
                    reason = $"{packageId} reached existing content outside dirty/context scope " +
                        $"[{string.Join(", ", overflow)}]";
                    return false;
                }

                foreach (string id in trial.TrialTargetIds)
                    foldedContext.Add(id);
            }

            return true;
        }
    }
}
