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

using System;
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

        // order lives on PatchOperationAdd as a PRIVATE field of a PRIVATE nested enum
        // (Order { Append, Prepend }) -- we can't reference the enum type itself, so read
        // it as a plain FieldInfo and compare the boxed value's ToString() against the
        // literal case name instead of AccessTools.FieldRefAccess<T>, which needs T known.
        private static readonly System.Reflection.FieldInfo OrderField =
            AccessTools.Field(typeof(PatchOperationAdd), "order");

        // The matched-node accumulator for the PatchOperation currently applying.
        // A stack scopes nested operations: a PatchOperationSequence drives its
        // children's Apply recursively, and each child's selection must land in its
        // own sink rather than the parent's. A sink is present only between an
        // operation's Apply Prefix and Postfix, so the Select* hooks are inert at
        // all other times (including the entire load when capture is off).
        private static readonly Stack<List<string>> sinks =
            new Stack<List<string>>();

        // Parallel to `sinks`, holding the RAW XmlNode references for the same selection
        // (sinks only keeps their stable string ids). Needed by RecordAddedChildren, which
        // must inspect a target's live child elements right after Apply -- the string id
        // alone doesn't let us walk the document. Kept as a separate stack (rather than
        // folding into sinks) so the hot Capture() path pays no extra allocation when
        // Add-shaped-op post-processing isn't going to look at it.
        private static readonly Stack<List<XmlNode>> rawSinks =
            new Stack<List<XmlNode>>();

        // Parallel to `rawSinks`: each target's ChildNodes.Count AT SELECTION TIME, i.e.
        // before the operation appends anything. A broad target (e.g. an Add anchored on
        // the `Defs` root, as TestMod_GenOp's doc-path-fallback fixture does deliberately)
        // can already have thousands of pre-existing children; without this snapshot,
        // RecordAddedChildren would misattribute ALL of them to this op's mod instead of
        // only the ones it actually appended. Cheap (an int per target) so always captured
        // alongside rawSinks rather than gated to Add-shaped ops specifically.
        private static readonly Stack<List<int>> rawSinkChildCounts =
            new Stack<List<int>>();

        // Append one selection result to the operation currently applying. The node is
        // keyed to its stable id NOW, while still attached to the document, because a
        // Replace/Remove operation detaches it before our Apply postfix runs. Cheap and
        // inert when no operation is active (sinks empty).
        internal static void Capture(XmlNode node)
        {
            if (node == null || sinks.Count == 0)
                return;
            string id = ProvenanceRecorder.KeyMatched(node);
            if (!string.IsNullOrEmpty(id))
                sinks.Peek().Add(id);
            rawSinks.Peek().Add(node);
            rawSinkChildCounts.Peek().Add(node.ChildNodes.Count);
        }

        internal static void Capture(XmlNodeList nodes)
        {
            if (nodes == null || sinks.Count == 0)
                return;
            List<string> sink = sinks.Peek();
            List<XmlNode> rawSink = rawSinks.Peek();
            List<int> rawSinkCounts = rawSinkChildCounts.Peek();
            foreach (XmlNode node in nodes)
            {
                string id = ProvenanceRecorder.KeyMatched(node);
                if (!string.IsNullOrEmpty(id))
                    sink.Add(id);
                rawSink.Add(node);
                rawSinkCounts.Add(node.ChildNodes.Count);
            }
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

            // Register abstract / Name-based parent defs, which never become Def objects
            // and so are missed by the per-def RegisterNode hook. Without them, ParentName
            // references resolve to nothing and inheritance fan-out is broken.
            Finder.Harmony.Patch(
                AccessTools.Method(typeof(XmlInheritance), nameof(XmlInheritance.TryRegister)),
                postfix: new HarmonyMethod(typeof(XmlInheritance_Patch), nameof(XmlInheritance_Patch.TryRegister_Postfix)));
        }

        // Postfix bodies for the BCL selection methods. Kept as a separate type so
        // the manual Finder.Harmony.Patch above targets exactly the (string) overload.
        public static class XmlSelect_Patch
        {
            public static void SelectNodes_Postfix(XmlNodeList __result) => Capture(__result);

            public static void SelectSingleNode_Postfix(XmlNode __result) => Capture(__result);
        }

        // Postfix on XmlInheritance.TryRegister: registers abstract / Name-based parent
        // defs so inheritance edges can resolve their ParentName. Inert unless capturing.
        public static class XmlInheritance_Patch
        {
            public static void TryRegister_Postfix(XmlNode node, ModContentPack mod)
                => ProvenanceRecorder.RegisterAbstract(node, mod);
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
                // Open a sink for this operation; the Select* hooks fill it with the ids
                // of whatever RimWorld selects while Apply runs.
                sinks.Push(new List<string>());
                rawSinks.Push(new List<XmlNode>());
                rawSinkChildCounts.Push(new List<int>());
                // Push this op onto the apply-stack so nested/recursive applies know their enclosing
                // op — and so an op generated dynamically inside this one's ApplyWorker is attributed
                // to it rather than the "unindexed" bucket. Balanced by ExitApply in the Postfix.
                ProvenanceRecorder.EnterApply(__instance);
            }

            public static void Postfix(PatchOperation __instance, bool __result)
            {
                if (!ProvenanceRecorder.Active)
                    return;

                List<string> matched = sinks.Count > 0 ? sinks.Pop() : null;
                List<XmlNode> matchedRaw = rawSinks.Count > 0 ? rawSinks.Pop() : null;
                List<int> matchedRawChildCounts = rawSinkChildCounts.Count > 0 ? rawSinkChildCounts.Pop() : null;
                // Pop the apply-stack for EVERY op (balances EnterApply in the Prefix), BEFORE the
                // non-pathed early-return below — otherwise a non-pathed op (sequence, custom
                // container) would leak its push and corrupt attribution for everything after.
                // RecordPatch reads patchIds, not the stack, so popping here is safe.
                ProvenanceRecorder.ExitApply();

                // Drain this op's own MayRequire gate (issue #40 case 3), if
                // DirectXmlToObject_Patch stashed one for it, into the shared mayRequire
                // index — keyed by exactly the nodes this operation itself matched.
                ProvenanceRecorder.IndexOperationGate(__instance, matched);

                // Branch-shaped constructs (issue #40) are checked BEFORE the pathed-only
                // early return below: PatchOperationFindMod/Conditional never carry their
                // own xpath (they only route to match/nomatch), so the early return would
                // otherwise skip them entirely.
                if (__instance is PatchOperationFindMod findMod)
                {
                    ProvenanceRecorder.IndexFindMod(findMod);
                    ProvenanceRecorder.RecordFindModEdge(findMod);
                }
                else
                    ProvenanceRecorder.MaybeRecordUnresolvedGate(__instance);

                if (!(__instance is PatchOperationPathed))
                    return;

                string xpath = XPathRef(__instance);
                // The matched nodes are also the modified ones when the operation
                // succeeded: pathed operations act on (or relative to) their
                // selection. A failed match modifies nothing.
                IEnumerable<string> modified = __result ? matched : null;
                ProvenanceRecorder.RecordPatch(__instance, xpath, matched, modified);

                // PatchOperationAdd's target selection is the INSERTION ANCHOR, not the new
                // content it creates -- so `matched`/`modified` above can never carry the new
                // child's own id, and RegisterNode later sees a null asset for it (see
                // RecordAddedChildren's comment). Recover ownership now, while the target
                // nodes are still live XmlNode references with their newly-appended children
                // attached.
                //
                // The bare-name check below (not the fully-qualified type) is a pre-existing
                // gap shared with SafeLeafOps' old behaviour (see CASE 13/RogueOp) -- out of
                // scope here. The `is PatchOperationAdd realAdd` pattern match guards ONLY the
                // reflective `order` read: OrderField is bound to Verse.PatchOperationAdd, so
                // calling GetValue on a same-named-but-foreign class (a rogue mod's own
                // "PatchOperationAdd") would throw ArgumentException. A failed pattern match
                // just leaves prepend=false (the correct default for a non-Verse instance,
                // since it never actually ran Verse's Prepend branch).
                if (__result && __instance.GetType().Name == "PatchOperationAdd")
                {
                    bool prepend = __instance is PatchOperationAdd realAdd
                        && OrderField != null
                        && OrderField.GetValue(realAdd)?.ToString() == "Prepend";
                    ProvenanceRecorder.RecordAddedChildren(__instance, matchedRaw, matchedRawChildCounts, prepend);
                }
            }

            // Harmony runs this Finalizer after the Postfix on success, and INSTEAD of the
            // Postfix when PatchOperation.Apply (i.e. ApplyWorker) throws — Harmony skips
            // Postfixes on exception. RimWorld's LoadedModManager.ApplyPatches wraps each
            // top-level op in try/catch, so a throw propagates up through every enclosing
            // sequence to that catch; none of those ops' Postfixes run, and each would leak
            // the sink + apply-stack frame its Prefix pushed — corrupting attribution (and
            // growing both stacks) for everything captured afterwards.
            //
            // So on the THROW path only (__exception != null, meaning the Postfix did NOT
            // run) we discard this op's sink and pop its apply-stack frame, leaving both
            // stacks balanced at every depth. No RecordPatch — the op failed, so it
            // correctly contributes no edge. On the SUCCESS path the Postfix already
            // balanced them, so we must do nothing here or we would double-pop.
            //
            // The parameter is taken BY VALUE (not ref) and the method returns void, so it
            // is a transparent observer: the original exception keeps propagating unchanged
            // and ApplyPatches still logs-and-continues exactly as upstream.
            public static void Finalizer(PatchOperation __instance, Exception __exception)
            {
                if (!ProvenanceRecorder.Active || __exception == null)
                    return;
                if (sinks.Count > 0)
                    sinks.Pop();
                if (rawSinks.Count > 0)
                    rawSinks.Pop();
                if (rawSinkChildCounts.Count > 0)
                    rawSinkChildCounts.Pop();
                ProvenanceRecorder.ExitApply();
                // The op failed, so it matched/indexed nothing -- but if
                // DirectXmlToObject_Patch stashed a MayRequire gate for it (issue #40 case
                // 3), that stash would otherwise never be drained (only the Postfix, which
                // does not run on a throw, calls IndexOperationGate). Discard it here so it
                // does not leak for the life of the process.
                ProvenanceRecorder.DiscardOperationGate(__instance);
            }
        }
    }
}
