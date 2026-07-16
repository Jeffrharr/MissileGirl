// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// UnifiedCacheDiffTests.cs (Piece D — Milestone 2b: real-engine zero-diff gate)
//
// Contains: offline coverage for the unified-cache diff — id indexing of the DefXmlStorage
// schema and the non-dirty mismatch detection that is M2b's correctness gate (a non-dirty def
// differing between prior cache and rebuild is a subset error; a dirty def differing is fine;
// an added/removed non-dirty def is a mismatch).

using System.Collections.Generic;
using System.Xml;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class UnifiedCacheDiffTests
    {
        // Build a DefXmlStorage document from (defType, defName, innerXml) items, parsed the
        // same way the gate parses the real cache (whitespace-insensitive).
        private static XmlDocument Storage(params (string type, string name, string body)[] items)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<DefXmlStorage>");
            foreach (var (type, name, body) in items)
                sb.Append($"<Item path=\"x.xml\"><{type}><defName>{name}</defName>{body}</{type}></Item>");
            sb.Append("</DefXmlStorage>");

            var doc = new XmlDocument();
            using var reader = XmlReader.Create(new System.IO.StringReader(sb.ToString()),
                new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
            doc.Load(reader);
            return doc;
        }

        private static HashSet<string> Dirty(params string[] ids) => new HashSet<string>(ids);

        [Test]
        public void IndexById_KeysConcreteDefsByTypeAndName()
        {
            var doc = Storage(("ThingDef", "Steel", "<mass>1</mass>"), ("ThingDef", "Gold", ""));
            var map = UnifiedCacheDiff.IndexById(doc);
            Assert.That(map.Keys, Is.EquivalentTo(new[] { "ThingDef/Steel", "ThingDef/Gold" }));
            Assert.That(map["ThingDef/Steel"], Does.Contain("<mass>1</mass>"));
        }

        // issue #72: TrialExecution folds a patch-injected new def into context, and the gate's
        // recompute needs its source path from the ground-truth rebuild (Unified.xml already
        // carries one for every def, patch-injected or not, via RimWorld's real loadingAsset).
        [Test]
        public void IndexPathsById_KeysByIdMirroringIndexById()
        {
            var doc = new XmlDocument();
            doc.LoadXml(
                "<DefXmlStorage>" +
                "<Item path=\"Mods/Core/Defs/Steel.xml\"><ThingDef><defName>Steel</defName></ThingDef></Item>" +
                "<Item path=\"\"><ThingDef><defName>PatchInjected</defName></ThingDef></Item>" +
                "</DefXmlStorage>");

            var map = UnifiedCacheDiff.IndexPathsById(doc);

            Assert.That(map["ThingDef/Steel"], Is.EqualTo("Mods/Core/Defs/Steel.xml"));
            Assert.That(map["ThingDef/PatchInjected"], Is.EqualTo(string.Empty));
        }

        [Test]
        public void NoChange_NoMismatches()
        {
            var a = Storage(("ThingDef", "Steel", "<mass>1</mass>"), ("ThingDef", "Gold", "<mass>2</mass>"));
            var b = Storage(("ThingDef", "Steel", "<mass>1</mass>"), ("ThingDef", "Gold", "<mass>2</mass>"));
            var m = UnifiedCacheDiff.NonDirtyMismatches(
                UnifiedCacheDiff.IndexById(a), UnifiedCacheDiff.IndexById(b), Dirty());
            Assert.That(m, Is.Empty);
        }

        // The whole point: a def that changed but was NOT flagged dirty is a subset error.
        [Test]
        public void NonDirtyDefChanged_IsMismatch()
        {
            var baseline = Storage(("ThingDef", "Steel", "<mass>1</mass>"));
            var rebuild = Storage(("ThingDef", "Steel", "<mass>9</mass>"));
            var m = UnifiedCacheDiff.NonDirtyMismatches(
                UnifiedCacheDiff.IndexById(baseline), UnifiedCacheDiff.IndexById(rebuild), Dirty());
            Assert.That(m, Is.EquivalentTo(new[] { "ThingDef/Steel" }));
        }

        // A def that changed AND was flagged dirty is expected and must not be reported.
        [Test]
        public void DirtyDefChanged_IsNotMismatch()
        {
            var baseline = Storage(("ThingDef", "Steel", "<mass>1</mass>"));
            var rebuild = Storage(("ThingDef", "Steel", "<mass>9</mass>"));
            var m = UnifiedCacheDiff.NonDirtyMismatches(
                UnifiedCacheDiff.IndexById(baseline), UnifiedCacheDiff.IndexById(rebuild),
                Dirty("ThingDef/Steel"));
            Assert.That(m, Is.Empty);
        }

        // A def the rebuild added (or dropped) without it being dirty is also a subset error.
        [Test]
        public void NonDirtyDefAddedOrRemoved_IsMismatch()
        {
            var baseline = Storage(("ThingDef", "Steel", ""));
            var rebuild = Storage(("ThingDef", "Steel", ""), ("ThingDef", "New", ""));
            var added = UnifiedCacheDiff.NonDirtyMismatches(
                UnifiedCacheDiff.IndexById(baseline), UnifiedCacheDiff.IndexById(rebuild), Dirty());
            Assert.That(added, Is.EquivalentTo(new[] { "ThingDef/New" }));

            var removed = UnifiedCacheDiff.NonDirtyMismatches(
                UnifiedCacheDiff.IndexById(rebuild), UnifiedCacheDiff.IndexById(baseline), Dirty());
            Assert.That(removed, Is.EquivalentTo(new[] { "ThingDef/New" }));
        }
    }
}
