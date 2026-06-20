// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using System.Linq;
using System.Xml;
using HarmonyLib;
using Verse;

namespace Gagarin
{
    // Captures per-PatchOperation match provenance during a cold load. This wraps
    // PatchOperation.Apply so we observe every operation (including children of a
    // PatchOperationSequence, which drive Apply recursively). The hook is inert
    // unless ProvenanceRecorder.Active — i.e. the dev flag is on and we are not
    // loading from cache — so it never touches the shipped fast path.
    //
    // Why here: PatchOperations match by XPath, not identity. An early mod's
    // wildcard patch can match a later mod's defs, so we must record exactly which
    // nodes each operation MATCHED and which it MODIFIED, keyed to the operation.
    public static class PatchOperation_Patch
    {
        // xpath lives on PatchOperationPathed (protected). Read reflectively so we
        // do not depend on publicisation exposing the field and so non-pathed
        // operations are simply skipped.
        private static readonly AccessTools.FieldRef<object, string> XPathRef =
            AccessTools.FieldRefAccess<string>(typeof(PatchOperationPathed), "xpath");

        [GagarinPatch(typeof(PatchOperation), nameof(PatchOperation.Apply))]
        public static class Apply_Patch
        {
            // matchedSnapshot carries the pre-mutation XPath selection from Prefix
            // to Postfix. A stack supports the recursive Apply calls a sequence
            // makes without one operation's snapshot clobbering another's.
            private static readonly Stack<List<XmlNode>> matchedSnapshot =
                new Stack<List<XmlNode>>();

            public static void Prefix(PatchOperation __instance, XmlDocument xml)
            {
                if (!ProvenanceRecorder.Active)
                    return;

                List<XmlNode> matched = null;
                if (__instance is PatchOperationPathed)
                {
                    string xpath = XPathRef(__instance);
                    if (!string.IsNullOrEmpty(xpath))
                    {
                        try
                        {
                            matched = xml.SelectNodes(xpath)?.Cast<XmlNode>().ToList();
                        }
                        catch
                        {
                            // Malformed/unsupported XPath: record an empty match
                            // rather than disturbing the patch run.
                            matched = null;
                        }
                    }
                }
                matchedSnapshot.Push(matched);
            }

            public static void Postfix(PatchOperation __instance, bool __result)
            {
                if (!ProvenanceRecorder.Active)
                    return;

                List<XmlNode> matched = matchedSnapshot.Count > 0 ? matchedSnapshot.Pop() : null;
                if (!(__instance is PatchOperationPathed))
                    return;

                string xpath = XPathRef(__instance);
                // The matched nodes are also the modified ones when the operation
                // succeeded: pathed operations act on (or relative to) their
                // selection. A failed match modifies nothing.
                IEnumerable<XmlNode> modified = __result ? matched : null;
                ProvenanceRecorder.RecordPatch(__instance, xpath, matched, modified);
            }
        }
    }
}
