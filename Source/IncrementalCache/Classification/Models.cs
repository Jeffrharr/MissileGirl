// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// Models.cs (Piece B — patch classification)
//
// Contains: the plain data types shared across the classifier — PatchEdge (the
// subset of a Piece A patchEdge we read), the Classification enum, the
// PatchClassificationResult verdict, and the per-mod ModBreakdown tally.
//
// Used for: carrying patch data from the parsed DependencyGraph.json through the
// classifier and into the PatchClassification.json output.
//
// Why: keeping the contract's shape in one small, dependency-free file makes the
// classifier easy to read and keeps us forward-compatible — we model only the
// fields we consume, so extra metadata Piece A adds later is ignored harmlessly.

using System.Collections.Generic;

namespace Gagarin.IncrementalCache.Classification
{
    // A single patchEdge as emitted by Piece A's DependencyGraph.json. We only model the
    // fields the classifier actually reads; unknown fields in the input are ignored so we
    // stay forward-compatible with Piece A adding more metadata later.
    public sealed class PatchEdge
    {
        public string PatchId;
        public string SourceMod;
        public string OperationType;
        public string Xpath;
        public List<string> MatchedNodeIds = new List<string>();
        public List<string> ModifiedNodeIds = new List<string>();
    }

    public enum Classification
    {
        Identity,
        Wildcard
    }

    // The classifier's verdict for one patch. Mirrors the "patches[]" entries in the
    // PatchClassification.json contract consumed by Piece C.
    public sealed class PatchClassificationResult
    {
        public string PatchId;
        public string SourceMod;
        public Classification Classification;

        // For identity patches: the defName(s) this patch anchors on.
        public List<string> TargetDefNames = new List<string>();

        // For wildcard patches: the structural predicate to re-test against changed nodes
        // later. A string is sufficient for the prototype (typically the xpath itself).
        public string Predicate;

        public string ClassificationString => Classification == Classification.Identity ? "identity" : "wildcard";
    }

    // Per-mod identity/wildcard tally for the summary block.
    public sealed class ModBreakdown
    {
        public int Identity;
        public int Wildcard;
    }
}
