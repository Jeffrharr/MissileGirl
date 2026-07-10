// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// PatchInjectedAdds.cs (issue #61 — add-direction gap for patch-injected new defs)
//
// Contains: pure comparison of a post-patch candidate document's top-level def elements
// against a known-id set, returning ids that are genuinely new. This is the second half
// of the added-defs channel (Seed 5 / P2): P2 already catches new RAW def bodies (a
// brand-new mod's own <Defs> files, or new defs added to an edited file). It is blind to
// a mod that injects brand-new top-level defs via a PatchOperationAdd (or any custom op
// wrapping one, e.g. Big and Small's PatchOp_AddBionics) targeting xpath="Defs" — those
// defs have no raw source file at all, so P2's CurrentConcreteDefIndex (which only walks
// LoadableXmlAsset def files) never sees them, and they are silently never dirtied.
//
// Why this needs real patch execution, not structural xpath matching: unlike
// WildcardRematch (which only needs to know WHICH existing nodes a predicate now
// matches), a brand-new def has no existing node to match against — it must actually be
// created. There is no sound, generic way to predict the shape of an arbitrary custom
// PatchOperation's output without running it (the offending real-world case, Big and
// Small's PatchOp_AddBionics, is not even Verse.PatchOperationAdd itself — it is a
// wrapper class that internally invokes one). The RimWorld-facing driver
// (DirtySetDiagnostic.ComputeAddedContentFlips) is therefore the one running real
// PatchOperation.Apply calls for changed/added mods' patches against a scratch copy of
// the current candidate document; this file owns only the pure "what's new" comparison
// so that piece stays unit-testable offline.
//
// Why pure: given an already-patched document, finding newly-created top-level ids is
// just XML traversal + set membership — no RimWorld types needed.

using System.Collections.Generic;
using System.Xml;

namespace Gagarin
{
    public static class PatchInjectedAdds
    {
        // Top-level def ids present in patchedDoc's root that are not in knownIds.
        // patchedDoc is expected to be a <Defs>-rooted document (as built by
        // WildcardRematch.BuildCandidateDocument) after changed/added mods' patches have
        // been applied to it for real. Ids use the same "{element.Name}/{defName}"
        // scheme as DefRecompute.BuildRawIndex / DirtySetDiagnostic.CurrentConcreteDefIndex,
        // so they pair with the rest of the pipeline. Abstract (@Name-only, no defName)
        // top-level nodes are skipped -- never cache items.
        public static HashSet<string> NewTopLevelDefIds(XmlDocument patchedDoc, ISet<string> knownIds)
        {
            var added = new HashSet<string>();
            var root = patchedDoc?.DocumentElement;
            if (root == null)
                return added;

            foreach (XmlNode child in root.ChildNodes)
            {
                if (!(child is XmlElement def))
                    continue;
                string defName = def["defName"]?.InnerText;
                if (string.IsNullOrEmpty(defName))
                    continue;
                string id = def.Name + "/" + defName;
                if (knownIds == null || !knownIds.Contains(id))
                    added.Add(id);
            }
            return added;
        }
    }
}
