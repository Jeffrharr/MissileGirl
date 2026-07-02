// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// FindModCapture.cs (Piece A — provenance capture, issue #40)
//
// Contains: the pure logic behind the PatchOperationFindMod capture reader and the
// generic match/nomatch branch-construct fallback: resolving FindMod's mod DISPLAY
// NAMES to packageIds, and deciding whether an unrecognized PatchOperation type needs
// the conservative fallback treatment.
//
// Used for: ProvenanceRecorder.IndexFindMod / MaybeRecordUnresolvedGate, which are
// RimWorld-coupled (they reflect real PatchOperation instances and call ModLister) and
// so cannot be unit-tested offline. Factoring the mapping/decision logic out here keeps
// that part testable with plain C# — see FindModCaptureTests.
//
// Why this exists at all (root cause, issue #40): PatchOperationFindMod is a
// procedural branch — it tests ModLister.HasActiveModWithName(name) (mod DISPLAY
// NAME, not packageId) and applies either its `match` or `nomatch` child. When the
// gating mod's presence flips, the branch's content flips too, but the mod OWNING the
// FindMod operation never changed, so Seed 2 (PatchModified) never looks at it; and the
// branch is a live procedural skip, not a MayRequire XML attribute surviving in the
// patched doc, so Seed 6's document scan never sees it either. The fix reuses Seed 6
// verbatim by feeding the FindMod-gated branch's already-captured matched node ids into
// the SAME mayRequire index, keyed by the packageId(s) resolved from the tested names.

using System;
using System.Collections.Generic;

namespace Gagarin
{
    public static class FindModCapture
    {
        // Resolves each display name in modNames to a packageId via nameToPackageId,
        // preserving first-seen order and dropping unresolved (nameToPackageId
        // returned null/empty, e.g. an unknown or inactive mod) or duplicate entries.
        // A PatchOperationFindMod can list several names that all belong to the same
        // mod family, or a name RimWorld doesn't currently have installed at all — we
        // only want each REAL packageId indexed once.
        public static List<string> ResolvePackageIds(
            IEnumerable<string> modNames, Func<string, string> nameToPackageId)
        {
            var result = new List<string>();
            if (modNames == null || nameToPackageId == null)
                return result;

            foreach (string name in modNames)
            {
                string pkg = nameToPackageId(name);
                if (!string.IsNullOrEmpty(pkg) && !result.Contains(pkg))
                    result.Add(pkg);
            }
            return result;
        }

        // The branch-shaped PatchOperation constructs we already have a dedicated typed
        // reader for: PatchOperationFindMod (this issue, via IndexFindMod) and
        // PatchOperationConditional (issue #25's RecomputeAllowlist.BranchParentId,
        // which already consumes the generic .match/.nomatch patch ids the capture
        // walk assigns it). Anything else sharing the same field convention is an
        // unrecognized branch construct — a third-party FindMod-alike, most likely.
        private static readonly HashSet<string> KnownBranchReaders =
            new HashSet<string>(StringComparer.Ordinal)
        {
            "PatchOperationFindMod",
            "PatchOperationConditional",
        };

        // True when an operation type needs the generic reflection fallback: it
        // carries the match/nomatch branching convention (hasMatchOrNomatchField,
        // determined by the caller via reflection on the live Type — that part isn't
        // pure) but isn't one of the types we already know how to interpret. Kept as a
        // tiny predicate so "which types are already covered" lives in one place and
        // is unit-testable without reflecting any real PatchOperation subclass.
        public static bool NeedsGenericFallback(string typeName, bool hasMatchOrNomatchField)
            => hasMatchOrNomatchField && !KnownBranchReaders.Contains(typeName);
    }
}
