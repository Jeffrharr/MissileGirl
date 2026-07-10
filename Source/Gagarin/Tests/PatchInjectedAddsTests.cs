// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// PatchInjectedAddsTests.cs (issue #61 — add-direction gap for patch-injected new defs)
//
// Contains: offline correctness gate for PatchInjectedAdds.NewTopLevelDefIds -- given an
// already-patched <Defs> document, does it correctly separate genuinely-new top-level ids
// from baseline ones, and correctly skip abstract (@Name-only) nodes.

using System.Collections.Generic;
using System.Xml;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class PatchInjectedAddsTests
    {
        private static XmlDocument Doc(string innerXml)
        {
            var doc = new XmlDocument();
            doc.LoadXml($"<Defs>{innerXml}</Defs>");
            return doc;
        }

        [Test]
        public void NewTopLevelDefIds_IdNotInKnownIds_IsReturned()
        {
            var doc = Doc("<HediffDef><defName>BS_BionicStriderLeg</defName></HediffDef>");
            var known = new HashSet<string> { "ThingDef/Steel" };

            var result = PatchInjectedAdds.NewTopLevelDefIds(doc, known);

            Assert.That(result, Contains.Item("HediffDef/BS_BionicStriderLeg"));
        }

        [Test]
        public void NewTopLevelDefIds_IdAlreadyKnown_NotReturned()
        {
            // A raw def re-imported into the candidate doc unpatched (already accounted for
            // by the baseline graph, or already caught by the ordinary added-defs scan) must
            // not be reported again as "new".
            var doc = Doc("<ThingDef><defName>Steel</defName></ThingDef>");
            var known = new HashSet<string> { "ThingDef/Steel" };

            var result = PatchInjectedAdds.NewTopLevelDefIds(doc, known);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void NewTopLevelDefIds_AbstractNode_Skipped()
        {
            // No <defName> -- an abstract (Name-only) base, never a cache item.
            var doc = Doc("<ThingDef Name=\"BuildingBase\" Abstract=\"True\"></ThingDef>");
            var known = new HashSet<string>();

            var result = PatchInjectedAdds.NewTopLevelDefIds(doc, known);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void NewTopLevelDefIds_MultipleInjectedDefs_AllReturned()
        {
            // Mirrors the real-world repro: Big and Small's PatchOp_AddBionics wraps a
            // PatchOperationAdd (xpath="Defs") that injects several sibling top-level defs
            // (HediffDef, ThingDef, RecipeDef) in one go.
            var doc = Doc(
                "<HediffDef><defName>BS_BionicStriderLeg</defName></HediffDef>" +
                "<ThingDef><defName>BS_BionicStriderLeg</defName></ThingDef>" +
                "<RecipeDef><defName>InstallBS_BionicStriderLeg</defName></RecipeDef>");
            var known = new HashSet<string>();

            var result = PatchInjectedAdds.NewTopLevelDefIds(doc, known);

            Assert.That(result, Is.EquivalentTo(new[]
            {
                "HediffDef/BS_BionicStriderLeg",
                "ThingDef/BS_BionicStriderLeg",
                "RecipeDef/InstallBS_BionicStriderLeg"
            }));
        }

        [Test]
        public void NewTopLevelDefIds_EmptyOrNullInputs_NoOpAndNoThrow()
        {
            Assert.That(PatchInjectedAdds.NewTopLevelDefIds(null, null), Is.Empty);
            Assert.That(PatchInjectedAdds.NewTopLevelDefIds(Doc(""), null), Is.Empty);
            Assert.That(PatchInjectedAdds.NewTopLevelDefIds(Doc(""), new HashSet<string>()), Is.Empty);
        }
    }
}
