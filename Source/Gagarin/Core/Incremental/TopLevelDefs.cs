// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// TopLevelDefs.cs (check-my-vibe PR #62 interview)
//
// Contains: pure traversal of a <Defs>-rooted element's direct children, yielding each
// concrete top-level def's "{element.Name}/{defName}" id alongside its element. Both
// PatchInjectedAdds.NewTopLevelDefIds (issue #61, comparing against a known-id set) and
// DefRecompute's pending-id promotion step (CASE 14, looking a specific id up after real
// patch replay) need exactly this walk; sharing it means the "what counts as a top-level
// def" rule -- must be an XmlElement, must carry a non-empty <defName> child, abstract
// (@Name-only) nodes are never cache items -- is defined once.

using System.Collections.Generic;
using System.Xml;

namespace Gagarin
{
    public static class TopLevelDefs
    {
        public static IEnumerable<(string Id, XmlElement Element)> Enumerate(XmlElement defsRoot)
        {
            if (defsRoot == null)
                yield break;

            foreach (XmlNode child in defsRoot.ChildNodes)
            {
                if (child is XmlElement el)
                {
                    string defName = el["defName"]?.InnerText;
                    if (!string.IsNullOrEmpty(defName))
                        yield return (el.Name + "/" + defName, el);
                }
            }
        }
    }
}
