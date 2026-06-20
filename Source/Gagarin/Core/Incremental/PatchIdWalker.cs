// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// PatchIdWalker.cs (Piece A — provenance capture)
//
// Contains: the pure, generic hierarchical-id walker that assigns a unique,
// stable id to every operation in a patch tree (root plus all nested children).
//
// Used for: IndexPatches, which must give every PatchOperation — including the
// children of containers like PatchOperationSequence / PatchOperationConditional
// / PatchOperationFindMod — its own id so RecordPatch can attribute each edge to
// a distinct bucket and to its real source mod.
//
// Why: it is deliberately free of any RimWorld dependency so the recursion /
// cycle-guard / label-construction logic can be unit-tested offline. The thin
// RimWorld-specific part (reflecting PatchOperation fields into child pairs)
// lives in ProvenanceRecorder and only supplies the child-enumeration callback.

using System;
using System.Collections.Generic;

namespace Gagarin
{
    // A generic depth-first id assigner for arbitrary trees/DAGs of objects.
    // Given a root, an id for that root, and a callback that yields a node's
    // (label, child) pairs, it walks the structure and writes "{rootId}{path}"
    // ids into a dictionary, where path is built from the labels (e.g.
    // ".operations[2]", ".match"). A node already present in the dictionary is
    // skipped (first visit wins), which both prevents infinite recursion on
    // cycles/shared children and keeps ids stable.
    //
    // The walker imposes no ordering of its own: id stability depends entirely on
    // the caller yielding child pairs in a deterministic order.
    public static class PatchIdWalker
    {
        // Assigns ids for root and everything reachable through getChildren into
        // ids. root must not be null. rootId is used verbatim for the root; nested
        // ids are rootId followed by each label segment along the path. getChildren
        // returns the (label, child) pairs for a node, or null/empty for a leaf;
        // null children in the returned pairs are skipped.
        public static void AssignIds<T>(
            T root,
            string rootId,
            Func<T, IEnumerable<(string label, T child)>> getChildren,
            IDictionary<T, string> ids)
            where T : class
        {
            if (root == null || ids == null || getChildren == null)
                return;

            // Iterative DFS with an explicit stack so deep patch trees can't blow
            // the call stack. We push the root with its full id, then push each
            // child with the parent id plus the child's label segment.
            var stack = new Stack<(T node, string id)>();

            // Guard the root the same way as any other node so a root that also
            // appears as a child elsewhere isn't reassigned (first visit wins).
            if (!ids.ContainsKey(root))
            {
                ids[root] = rootId;
                stack.Push((root, rootId));
            }

            while (stack.Count > 0)
            {
                (T node, string id) = stack.Pop();

                IEnumerable<(string label, T child)> children = getChildren(node);
                if (children == null)
                    continue;

                foreach ((string label, T child) in children)
                {
                    if (child == null)
                        continue;
                    // First visit wins: this is the cycle guard and what makes a
                    // shared child resolve to a single, stable id.
                    if (ids.ContainsKey(child))
                        continue;

                    string childId = id + label;
                    ids[child] = childId;
                    stack.Push((child, childId));
                }
            }
        }
    }
}
