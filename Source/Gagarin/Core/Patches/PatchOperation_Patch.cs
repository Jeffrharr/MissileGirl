// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// PatchOperation_Patch.cs (Piece A — provenance capture)
//
// Contains: the Harmony patch wrapping PatchOperation.Apply, plus low-level
// hooks on XmlNode.SelectNodes/SelectSingleNode that observe each operation's
// own XPath selection and report the matched/modified nodes to ProvenanceRecorder.
//
// Used for: capturing per-PatchOperation match provenance during a cold load,
// including children of PatchOperationSequence which drive Apply recursively.
//
// Why: PatchOperations match by XPath, not identity, so an early mod's wildcard
// patch can match a later, changed mod's defs. We must record exactly which
// nodes each operation matched and modified to compute the dirty set correctly.
//
// How (and why it is cheap): rather than re-running SelectNodes ourselves — which
// doubled the patch phase's XPath cost, the dominant load expense — we piggyback
// on RimWorld's own selection. The Apply wrapper pushes a per-operation "sink";
// the Select* hooks append whatever RimWorld selects into the active sink. So we
// pay only for recording references, never a second document scan. The Select*
// hooks are installed only when capture is enabled (dev-only), so the shipped fast
// path is never touched.

using System.Collections.Generic;
using System.Xml;
using HarmonyLib;
using MissileGirl;
using Verse;

namespace Gagarin
{
    public static class PatchOperation_Patch
    {
        // xpath lives on PatchOperationPathed (protected). Read reflectively so we
        // do not depend on publicisation exposing the field and so non-pathed
        // operations are simply skipped.
        private static readonly AccessTools.FieldRef<object, string> XPathRef =
            AccessTools.FieldRefAccess<string>(typeof(PatchOperationPathed), "xpath");

        // The matched-node accumulator for the PatchOperation currently applying.
        // A stack scopes nested operations: a PatchOperationSequence drives its
        // children's Apply recursively, and each child's selection must land in its
        // own sink rather than the parent's. A sink is present only between an
        // operation's Apply Prefix and Postfix, so the Select* hooks are inert at
        // all other times (including the entire load when capture is off).
        private static readonly Stack<List<XmlNode>> sinks =
            new Stack<List<XmlNode>>();

        // Append one selection result to the operation currently applying. Cheap and
        // inert when no operation is active (sinks empty).
        internal static void Capture(XmlNode node)
        {
            if (node == null || sinks.Count == 0)
                return;
            sinks.Peek().Add(node);
        }

        internal static void Capture(XmlNodeList nodes)
        {
            if (nodes == null || sinks.Count == 0)
                return;
            List<XmlNode> sink = sinks.Peek();
            foreach (XmlNode node in nodes)
                sink.Add(node);
        }

        // Installs the low-level XmlNode selection hooks. Only wired up when capture
        // is enabled, so normal loads never pay Harmony's per-call wrapper cost on
        // these very hot BCL methods. Runs at initialization, before the patch phase.
        [Main.OnInitialization]
        public static void InstallSelectionHooks()
        {
            if (!GagarinPrefs.CaptureProvenance)
                return;

            Finder.Harmony.Patch(
                AccessTools.Method(typeof(XmlNode), "SelectNodes", new[] { typeof(string) }),
                postfix: new HarmonyMethod(typeof(XmlSelect_Patch), nameof(XmlSelect_Patch.SelectNodes_Postfix)));
            Finder.Harmony.Patch(
                AccessTools.Method(typeof(XmlNode), "SelectSingleNode", new[] { typeof(string) }),
                postfix: new HarmonyMethod(typeof(XmlSelect_Patch), nameof(XmlSelect_Patch.SelectSingleNode_Postfix)));
        }

        // Postfix bodies for the BCL selection methods. Kept as a separate type so
        // the manual Finder.Harmony.Patch above targets exactly the (string) overload.
        public static class XmlSelect_Patch
        {
            public static void SelectNodes_Postfix(XmlNodeList __result) => Capture(__result);

            public static void SelectSingleNode_Postfix(XmlNode __result) => Capture(__result);
        }

        // Captures per-PatchOperation match provenance. Wraps PatchOperation.Apply so
        // we observe every operation (sequence children included, since they go
        // through the base Apply). Inert unless ProvenanceRecorder.Active.
        [GagarinPatch(typeof(PatchOperation), nameof(PatchOperation.Apply))]
        public static class Apply_Patch
        {
            public static void Prefix(PatchOperation __instance)
            {
                if (!ProvenanceRecorder.Active)
                    return;
                // Open a sink for this operation; the Select* hooks fill it with
                // whatever RimWorld selects while Apply runs.
                sinks.Push(new List<XmlNode>());
            }

            public static void Postfix(PatchOperation __instance, bool __result)
            {
                if (!ProvenanceRecorder.Active)
                    return;

                List<XmlNode> matched = sinks.Count > 0 ? sinks.Pop() : null;
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
