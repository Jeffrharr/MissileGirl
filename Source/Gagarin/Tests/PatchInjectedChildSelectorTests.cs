// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// PatchInjectedChildSelectorTests.cs (check-my-vibe PR #62 interview — CodeRabbit-flagged
// Prepend-order gap)
//
// Contains: offline correctness gate for PatchInjectedChildSelector -- given a target node's
// child count BEFORE a PatchOperationAdd ran, and its current (post-Apply) children, does the
// append selector correctly pick the tail and the prepend selector correctly pick the head.

using System.Collections.Generic;
using System.Linq;
using System.Xml;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class PatchInjectedChildSelectorTests
    {
        private static XmlNode Target(string innerXml)
        {
            var doc = new XmlDocument();
            doc.LoadXml($"<Defs>{innerXml}</Defs>");
            return doc.DocumentElement;
        }

        private static IEnumerable<string> Names(IEnumerable<XmlElement> elements) =>
            elements.Select(e => e.Name);

        [Test]
        public void SelectAppended_NewChildrenAfterPrior_ReturnsOnlyNew()
        {
            var target = Target("<Old/><Old/><New1/><New2/>");

            var result = PatchInjectedChildSelector.SelectAppended(target, priorCount: 2);

            Assert.That(Names(result), Is.EquivalentTo(new[] { "New1", "New2" }));
        }

        [Test]
        public void SelectAppended_PriorCountZero_ReturnsAllChildren()
        {
            var target = Target("<New1/><New2/>");

            var result = PatchInjectedChildSelector.SelectAppended(target, priorCount: 0);

            Assert.That(Names(result), Is.EquivalentTo(new[] { "New1", "New2" }));
        }

        [Test]
        public void SelectPrepended_NewChildrenBeforePrior_ReturnsOnlyNew()
        {
            // <order>Prepend</order>: new nodes land at the front, prior ones pushed to the tail.
            var target = Target("<New1/><New2/><Old/><Old/>");

            var result = PatchInjectedChildSelector.SelectPrepended(target, priorCount: 2);

            Assert.That(Names(result), Is.EquivalentTo(new[] { "New1", "New2" }));
        }

        [Test]
        public void SelectPrepended_PriorCountZero_ReturnsAllChildren()
        {
            var target = Target("<New1/><New2/>");

            var result = PatchInjectedChildSelector.SelectPrepended(target, priorCount: 0);

            Assert.That(Names(result), Is.EquivalentTo(new[] { "New1", "New2" }));
        }

        [Test]
        public void SelectPrepended_CurrentCountBelowPriorCount_ReturnsEmptyNotThrow()
        {
            // Should never happen for a successful Add, but must never underflow/throw.
            var target = Target("<Old/>");

            var result = PatchInjectedChildSelector.SelectPrepended(target, priorCount: 5);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void SelectAppended_NonElementChildren_Skipped()
        {
            var target = Target("<Old/><!-- comment --><New1/>");

            var result = PatchInjectedChildSelector.SelectAppended(target, priorCount: 1);

            Assert.That(Names(result), Is.EquivalentTo(new[] { "New1" }));
        }

        [Test]
        public void SelectNewlyAdded_DispatchesToAppendByDefault()
        {
            var target = Target("<Old/><New1/>");

            var result = PatchInjectedChildSelector.SelectNewlyAdded(target, priorCount: 1, prepend: false);

            Assert.That(Names(result), Is.EquivalentTo(new[] { "New1" }));
        }

        [Test]
        public void SelectNewlyAdded_DispatchesToPrependWhenRequested()
        {
            var target = Target("<New1/><Old/>");

            var result = PatchInjectedChildSelector.SelectNewlyAdded(target, priorCount: 1, prepend: true);

            Assert.That(Names(result), Is.EquivalentTo(new[] { "New1" }));
        }

        [Test]
        public void SelectAppended_NullTarget_ReturnsEmptyNotThrow()
        {
            Assert.That(PatchInjectedChildSelector.SelectAppended(null, 0), Is.Empty);
        }

        [Test]
        public void SelectPrepended_NullTarget_ReturnsEmptyNotThrow()
        {
            Assert.That(PatchInjectedChildSelector.SelectPrepended(null, 0), Is.Empty);
        }
    }
}
