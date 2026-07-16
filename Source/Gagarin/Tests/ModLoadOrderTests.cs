// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using Gagarin;
using NUnit.Framework;

namespace Gagarin.Tests
{
    [TestFixture]
    public class ModLoadOrderTests
    {
        private static HashSet<string> Set(params string[] ids) => new HashSet<string>(ids);
        private static List<string> Order(params string[] ids) => new List<string>(ids);

        [Test]
        public void Sort_ReordersToMatchRealOrder_NotInputOrder()
        {
            // The input set's own enumeration order is irrelevant -- only `order` decides output.
            List<string> result = ModLoadOrder.Sort(
                Set("mod.c", "mod.a", "mod.b"), Order("mod.a", "mod.b", "mod.c"));

            Assert.That(result, Is.EqualTo(new[] { "mod.a", "mod.b", "mod.c" }));
        }

        [Test]
        public void Sort_IdAbsentFromOrder_Dropped()
        {
            List<string> result = ModLoadOrder.Sort(
                Set("mod.a", "mod.notrunning"), Order("mod.a", "mod.b"));

            Assert.That(result, Is.EqualTo(new[] { "mod.a" }));
        }

        [Test]
        public void Sort_CaseInsensitiveMembership()
        {
            List<string> result = ModLoadOrder.Sort(
                Set("Mod.A"), Order("mod.a", "mod.b"));

            Assert.That(result, Is.EqualTo(new[] { "mod.a" }));
        }

        [Test]
        public void Sort_NullOrEmptyPackageIds_ReturnsEmpty()
        {
            Assert.That(ModLoadOrder.Sort(null, Order("mod.a")), Is.Empty);
            Assert.That(ModLoadOrder.Sort(Set(), Order("mod.a")), Is.Empty);
        }

        [Test]
        public void Sort_NullOrder_ReturnsEmpty()
        {
            Assert.That(ModLoadOrder.Sort(Set("mod.a"), null), Is.Empty);
        }
    }
}
