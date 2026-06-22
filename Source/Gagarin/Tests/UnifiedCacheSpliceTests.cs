// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// UnifiedCacheSpliceTests.cs (Piece D — Milestone 2b: recompute + splice)
//
// Contains: offline coverage for the unified-cache splice — replace / remove / add of <Item>s by
// def id, verbatim reuse of untouched items, and the load-bearing round-trip property: splicing
// the recomputed dirty defs onto the prior cache reproduces a full rebuild byte-for-byte (checked
// via UnifiedCacheDiff, which is the real M2b gate).

using System.Collections.Generic;
using System.Xml;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class UnifiedCacheSpliceTests
    {
        private static XmlDocument Storage(params (string type, string name, string body)[] items)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("<DefXmlStorage>");
            foreach (var (type, name, body) in items)
                sb.Append($"<Item path=\"{name}.xml\"><{type}><defName>{name}</defName>{body}</{type}></Item>");
            sb.Append("</DefXmlStorage>");
            var doc = new XmlDocument();
            using var reader = XmlReader.Create(new System.IO.StringReader(sb.ToString()),
                new XmlReaderSettings { IgnoreWhitespace = true, IgnoreComments = true });
            doc.Load(reader);
            return doc;
        }

        private static string Def(string type, string name, string body)
            => $"<{type}><defName>{name}</defName>{body}</{type}>";

        [Test]
        public void Replace_SwapsInnerDef_KeepsOthersVerbatim()
        {
            var baseline = Storage(("ThingDef", "Steel", "<mass>1</mass>"), ("ThingDef", "Gold", "<mass>2</mass>"));
            var recomputed = new Dictionary<string, string> { { "ThingDef/Steel", Def("ThingDef", "Steel", "<mass>9</mass>") } };

            var spliced = UnifiedCacheSplice.Splice(baseline, recomputed, null);
            var map = UnifiedCacheDiff.IndexById(spliced);

            Assert.That(map["ThingDef/Steel"], Does.Contain("<mass>9</mass>"));
            Assert.That(map["ThingDef/Gold"], Is.EqualTo(UnifiedCacheDiff.IndexById(baseline)["ThingDef/Gold"]));
        }

        [Test]
        public void Remove_DropsItem()
        {
            var baseline = Storage(("ThingDef", "Steel", ""), ("ThingDef", "Gold", ""));
            var spliced = UnifiedCacheSplice.Splice(baseline, null, new[] { "ThingDef/Gold" });
            var map = UnifiedCacheDiff.IndexById(spliced);
            Assert.That(map.Keys, Is.EquivalentTo(new[] { "ThingDef/Steel" }));
        }

        [Test]
        public void Add_AppendsNewItem()
        {
            var baseline = Storage(("ThingDef", "Steel", ""));
            var recomputed = new Dictionary<string, string> { { "ThingDef/New", Def("ThingDef", "New", "<mass>3</mass>") } };
            var spliced = UnifiedCacheSplice.Splice(baseline, recomputed, null);
            var map = UnifiedCacheDiff.IndexById(spliced);
            Assert.That(map.Keys, Is.EquivalentTo(new[] { "ThingDef/Steel", "ThingDef/New" }));
            Assert.That(map["ThingDef/New"], Does.Contain("<mass>3</mass>"));
        }

        // The property M2b ultimately requires: given the prior cache and the full rebuild, if we
        // feed the splice exactly the recomputed values for the dirty defs (and remove dirty defs
        // the rebuild dropped), the spliced doc must equal the rebuild over EVERY def. This is the
        // same equality the in-game gate enforces.
        [Test]
        public void Splice_OfDirtyDefs_ReproducesFullRebuild()
        {
            var baseline = Storage(
                ("ThingDef", "Steel", "<mass>1</mass>"),
                ("ThingDef", "Gold", "<mass>2</mass>"),
                ("ThingDef", "Wood", "<mass>3</mass>"),
                ("ThingDef", "Gone", "<mass>4</mass>"));
            var rebuild = Storage(
                ("ThingDef", "Steel", "<mass>1</mass>"),          // unchanged
                ("ThingDef", "Gold", "<mass>20</mass>"),          // changed (dirty)
                ("ThingDef", "Wood", "<mass>3</mass>"),           // unchanged
                ("ThingDef", "New", "<mass>5</mass>"));           // added (dirty); "Gone" removed (dirty)

            // Recompute = the rebuild's values for the dirty defs that still exist; removed = the
            // dirty def the rebuild dropped.
            var rebuildMap = UnifiedCacheDiff.IndexById(rebuild);
            var recomputed = new Dictionary<string, string>
            {
                { "ThingDef/Gold", rebuildMap["ThingDef/Gold"] },
                { "ThingDef/New", rebuildMap["ThingDef/New"] },
            };
            var removed = new[] { "ThingDef/Gone" };

            var spliced = UnifiedCacheSplice.Splice(baseline, recomputed, removed);

            // Zero diff vs the full rebuild over ALL defs (dirty set empty => every def compared).
            var mismatches = UnifiedCacheDiff.NonDirtyMismatches(
                UnifiedCacheDiff.IndexById(spliced), rebuildMap, new HashSet<string>());
            Assert.That(mismatches, Is.Empty);
        }
    }
}
