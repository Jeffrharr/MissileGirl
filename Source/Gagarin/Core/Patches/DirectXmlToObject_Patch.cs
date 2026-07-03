// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DirectXmlToObject_Patch.cs (Piece A — provenance capture, issue #40 case 3)
//
// Contains: a Harmony patch on the closed generic
// Verse.DirectXmlToObject.ObjectFromXml<PatchOperation>, which stashes a
// just-constructed operation's own MayRequire/MayRequireAnyOf attribute against
// its object identity.
//
// Why: Verse.DirectXmlToObject.ListFromXml<T> tests MayRequire/MayRequireAnyOf on
// each <li> BEFORE calling ObjectFromXml<T> -- confirmed by decompile. So a
// PatchOperation wrapped as e.g. <li Class="PatchOperationRemove"
// MayRequire="some.mod"> is never even constructed while some.mod is inactive;
// there is no PatchOperation instance for PatchOperation.Apply's existing
// Prefix/Postfix hook (PatchOperation_Patch.cs) to see, so that hook alone cannot
// learn the gate. This is structurally earlier than every other capture hook in
// Gagarin. Hooking the generic ObjectFromXml<PatchOperation> directly (rather
// than the non-generic ListFromXml, which is used for every list type in the
// game, not just patch operations) keeps this cheap and scoped to exactly the
// case that matters.
//
// The stashed gate is drained by ProvenanceRecorder.IndexOperationGate, called
// from the ALREADY-EXISTING Apply postfix once the operation's own matched nodes
// are known -- feeding the same mayRequire index Seed 6 (MayRequire flips)
// already consumes. No DirtySetComputer change needed.

using MissileGirl;
using System.Xml;
using Verse;

namespace Gagarin
{
    [GagarinPatch(typeof(DirectXmlToObject), nameof(DirectXmlToObject.ObjectFromXml),
        generics: new[] { typeof(PatchOperation) })]
    public static class DirectXmlToObject_ObjectFromXml_PatchOperation_Patch
    {
        public static void Postfix(XmlNode xmlRoot, PatchOperation __result)
        {
            if (!ProvenanceRecorder.Active || __result == null || xmlRoot?.Attributes == null)
                return;

            string mayRequire = xmlRoot.Attributes["MayRequire"]?.Value;
            string mayRequireAnyOf = xmlRoot.Attributes["MayRequireAnyOf"]?.Value;
            ProvenanceRecorder.RecordOperationGate(__result, mayRequire, mayRequireAnyOf);
        }
    }
}
