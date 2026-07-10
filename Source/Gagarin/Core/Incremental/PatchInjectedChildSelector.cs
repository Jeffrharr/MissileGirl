// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// PatchInjectedChildSelector.cs (check-my-vibe PR #62 interview — CodeRabbit-flagged gap)
//
// Contains: pure index math for ProvenanceRecorder.RecordAddedChildren — given a
// PatchOperationAdd target's child count BEFORE the op ran (priorCount) and whether the
// op's <order> is Append (default) or Prepend, picks out exactly the elements the op just
// added from the target's current (post-Apply) children.
//
// Why this needs an order split at all: PatchOperationAdd's default behaviour appends new
// nodes after any pre-existing children (new nodes at indices [priorCount, currentCount)).
// <order>Prepend</order> inserts them BEFORE the pre-existing children instead, pushing the
// old ones to the tail (new nodes at indices [0, currentCount - priorCount)). A single
// "everything past priorCount is new" rule (the original, pre-this-fix logic) is only
// correct for Append; under Prepend it inverts the selection — skipping the real new nodes
// and misattributing the pre-existing ones to this op's mod. Split into two named selectors
// (rather than one branchy method) so each half's index math states its own invariant and is
// independently testable.

using System;
using System.Collections.Generic;
using System.Xml;

namespace Gagarin
{
    public static class PatchInjectedChildSelector
    {
        // Default order: new children land AFTER any pre-existing ones.
        public static IEnumerable<XmlElement> SelectAppended(XmlNode target, int priorCount)
        {
            if (target == null)
                yield break;
            int idx = 0;
            for (XmlNode child = target.FirstChild; child != null; child = child.NextSibling, idx++)
            {
                if (idx < priorCount)
                    continue;
                if (child is XmlElement el)
                    yield return el;
            }
        }

        // <order>Prepend</order>: new children land BEFORE the pre-existing ones, which are
        // pushed to the tail. newCount is clamped at 0 so a target that somehow ended up with
        // FEWER children than its prior snapshot (should never happen for a successful Add,
        // but this must never throw into the patch phase) yields nothing rather than
        // underflowing into a negative bound.
        public static IEnumerable<XmlElement> SelectPrepended(XmlNode target, int priorCount)
        {
            if (target == null)
                yield break;
            int newCount = Math.Max(0, target.ChildNodes.Count - priorCount);
            int idx = 0;
            for (XmlNode child = target.FirstChild; child != null && idx < newCount; child = child.NextSibling, idx++)
            {
                if (child is XmlElement el)
                    yield return el;
            }
        }

        public static IEnumerable<XmlElement> SelectNewlyAdded(XmlNode target, int priorCount, bool prepend) =>
            prepend ? SelectPrepended(target, priorCount) : SelectAppended(target, priorCount);
    }
}
