// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DirectXmlToObject_Patch.cs (Piece A — provenance capture, issue #40 case 3)
//
// Contains: Harmony patches that stash a just-constructed PatchOperation's own
// MayRequire/MayRequireAnyOf attribute against its object identity, so
// ProvenanceRecorder.IndexOperationGate can later index it (feeding Seed 6,
// MayRequire flips in unchanged mods).
//
// Why not patch DirectXmlToObject.ObjectFromXml<PatchOperation> directly (the
// original approach): live-testing against a real ~150-mod list hung RimWorld
// during play-data load with an uncaught
// "ArgumentException: Object of type 'Verse.PatchOperation' cannot be
// converted to type 'Verse.XmlContainer'" thrown from deep inside vanilla XML
// parsing of an UNRELATED real mod's ordinary field. Root cause: on Mono,
// Harmony-patching one closed instantiation of a reference-type generic
// method can corrupt the shared native code for OTHER closed instantiations
// of the same generic method (e.g. ObjectFromXml<XmlContainer>,
// ObjectFromXml<string>, ...) because Mono canonicalizes/shares JIT code
// across reference-type generic instantiations. Disabling that patch (and
// only that patch) made the identical 150-mod live-test pass cleanly, which
// is as close to proof as we're going to get without a Mono source dive.
// Rather than patch any closed instantiation of a generic method, this file
// hooks two ordinary (non-generic) methods instead:
//
//   1. ModContentPack.LoadPatches -- the concrete, non-generic method that
//      calls ObjectFromXml<PatchOperation> for each root-level <Operation> in
//      a Patch XML file (Verse.ModContentPack, decompiled). Its Postfix
//      re-walks the same "Patches/" xml assets in the same order LoadPatches
//      itself just did, zipping each <Operation> node against the resulting
//      mod.Patches list positionally (LoadPatches never skips or reorders --
//      it Adds exactly one PatchOperation per <Operation> it accepts).
//
//   2. DirectXmlToObject.GetObjectFromXmlMethod(Type) -- the existing
//      NON-generic, per-type-cached reflection dispatcher vanilla itself uses
//      to construct PatchOperation-typed FIELDS (e.g. PatchOperationConditional
//      .match/.nomatch) and List<PatchOperation>-typed FIELDS (e.g.
//      PatchOperationSequence.operations) reflectively. Wrapping the returned
//      delegate here needs no MakeGenericMethod of our own and never touches
//      the generic method's own compiled body -- we only decorate the
//      delegate GetObjectFromXmlMethod already builds and caches.
//
// For the List<PatchOperation> field case, ListFromXml<T> (vanilla) tests
// MayRequire/MayRequireAnyOf on each <li> BEFORE constructing it and skips
// construction entirely when the gate fails -- so a gated-out <li> has no
// corresponding entry in the resulting list at all. We replicate that same
// test (MayRequireGate.Passes, already used for recompute fidelity) while
// walking the XML so the positional zip only advances past <li> nodes that
// vanilla actually constructed.
//
// The stashed gate is drained by ProvenanceRecorder.IndexOperationGate, called
// from the ALREADY-EXISTING Apply postfix (PatchOperation_Patch.cs) once the
// operation's own matched nodes are known -- feeding the same mayRequire
// index Seed 6 (MayRequire flips) already consumes. No DirtySetComputer
// change needed.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using MissileGirl;
using Verse;

namespace Gagarin
{
    [GagarinPatch(typeof(ModContentPack), nameof(ModContentPack.LoadPatches))]
    public static class ModContentPack_LoadPatches_GateCapture_Patch
    {
        public static void Postfix(ModContentPack __instance)
        {
            if (!ProvenanceRecorder.Active)
                return;

            List<PatchOperation> patches = __instance.Patches?.ToList();
            if (patches.NullOrEmpty())
                return;

            try
            {
                int opIndex = 0;
                foreach (LoadableXmlAsset asset in DirectXmlLoader.XmlAssetsInModFolder(__instance, "Patches/"))
                {
                    XmlElement root = asset.xmlDoc.DocumentElement;
                    if (root == null || root.Name != "Patch")
                        continue;

                    foreach (XmlNode child in root.ChildNodes)
                    {
                        if (!(child is XmlElement) || child.Name != "Operation")
                            continue;
                        if (opIndex >= patches.Count)
                            return;

                        DirectXmlToObject_GateCapture.RecordGate(child, patches[opIndex]);
                        opIndex++;
                    }
                }
            }
            catch (Exception er)
            {
                // Best-effort provenance only -- never let a capture-side issue break loading.
                Logger.Debug("GAGARIN: MayRequire gate capture (LoadPatches) failed", er);
            }
        }
    }

    [GagarinPatch(typeof(DirectXmlToObject), nameof(DirectXmlToObject.GetObjectFromXmlMethod))]
    public static class DirectXmlToObject_GetObjectFromXmlMethod_GateCapture_Patch
    {
        // GetObjectFromXmlMethod caches one delegate per Type and returns the SAME cached
        // delegate on every subsequent call for that type -- so this postfix would otherwise
        // re-wrap an already-wrapped delegate on every later call. Track the delegates we've
        // produced so a cache-hit call is a no-op.
        private static readonly HashSet<Func<XmlNode, bool, object>> wrapped = new HashSet<Func<XmlNode, bool, object>>();

        public static void Postfix(Type type, ref Func<XmlNode, bool, object> __result)
        {
            bool isSingular = typeof(PatchOperation).IsAssignableFrom(type);
            bool isList = !isSingular && IsPatchOperationList(type);
            if (!isSingular && !isList)
                return;
            if (wrapped.Contains(__result))
                return;

            Func<XmlNode, bool, object> inner = __result;
            Func<XmlNode, bool, object> outer = (xmlNode, doPostLoad) =>
            {
                object obj = inner(xmlNode, doPostLoad);
                if (ProvenanceRecorder.Active && xmlNode != null)
                {
                    if (isSingular)
                    {
                        if (obj is PatchOperation op)
                            DirectXmlToObject_GateCapture.RecordGate(xmlNode, op);
                    }
                    else
                    {
                        DirectXmlToObject_GateCapture.RecordListGates(xmlNode, obj as IList);
                    }
                }
                return obj;
            };
            wrapped.Add(outer);
            __result = outer;
        }

        private static bool IsPatchOperationList(Type type) =>
            type.IsGenericType
            && type.GetGenericTypeDefinition() == typeof(List<>)
            && typeof(PatchOperation).IsAssignableFrom(type.GetGenericArguments()[0]);
    }

    internal static class DirectXmlToObject_GateCapture
    {
        public static void RecordGate(XmlNode xmlNode, PatchOperation op)
        {
            if (op == null || xmlNode?.Attributes == null)
                return;

            string mayRequire = xmlNode.Attributes["MayRequire"]?.Value;
            string mayRequireAnyOf = xmlNode.Attributes["MayRequireAnyOf"]?.Value;
            ProvenanceRecorder.RecordOperationGate(op, mayRequire, mayRequireAnyOf);
        }

        // Mirrors ListFromXml<T>'s own MayRequire/MayRequireAnyOf pre-construction test so the
        // positional zip against the (shorter, when something was gated out) resulting list only
        // advances past <li> nodes vanilla actually constructed.
        public static void RecordListGates(XmlNode listRoot, IList items)
        {
            if (items == null)
                return;

            int i = 0;
            foreach (XmlNode child in listRoot.ChildNodes)
            {
                if (!(child is XmlElement))
                    continue;

                string mayRequire = child.Attributes?["MayRequire"]?.Value;
                string mayRequireAnyOf = child.Attributes?["MayRequireAnyOf"]?.Value;
                bool wouldConstruct = MayRequireGate.Passes(mayRequire, mayRequireAnyOf,
                    ModLister.AllModsActiveNoSuffix, ModLister.AnyModActiveNoSuffix);
                if (!wouldConstruct)
                    continue;
                if (i >= items.Count)
                    break;

                if (items[i] is PatchOperation op)
                    RecordGate(child, op);
                i++;
            }
        }
    }
}
