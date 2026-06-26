// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// PatchCoverageAuditTests.cs
//
// Offline gate for PatchCoverageAudit — the "no invisible op" detector. Asserts that a changed def is
// explained when a patch edge or a raw-file change touches it OR an inheritance ancestor, and is
// flagged "unattributed" only when nothing captured accounts for it (the invisible-op signature).

using System.Collections.Generic;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class PatchCoverageAuditTests
    {
        private static HashSet<string> Set(params string[] ids) => new HashSet<string>(ids);

        private static Dictionary<string, string> Parents(params (string child, string parent)[] edges)
        {
            var d = new Dictionary<string, string>();
            foreach (var (c, p) in edges) d[c] = p;
            return d;
        }

        // A changed def directly touched by a recorded patch edge -> explained -> not flagged.
        [Test]
        public void ChangedDef_ModifiedByEdge_IsExplained()
        {
            var un = PatchCoverageAudit.Unattributed(
                Set("ThingDef/A"), Set("ThingDef/A"), Parents(), Set());
            Assert.That(un, Is.Empty);
        }

        // A changed def whose own raw file changed (Seed 1) -> explained, even with no patch edge.
        [Test]
        public void ChangedDef_RawFileChanged_IsExplained()
        {
            var un = PatchCoverageAudit.Unattributed(
                Set("ThingDef/A"), Set(), Parents(), Set("ThingDef/A"));
            Assert.That(un, Is.Empty);
        }

        // A changed concrete def whose ANCESTOR was patched -> explained via the inheritance walk.
        [Test]
        public void ChangedDescendant_AncestorModifiedByEdge_IsExplained()
        {
            var un = PatchCoverageAudit.Unattributed(
                Set("ThingDef/Child"),
                Set("ThingDef@Base"),
                Parents(("ThingDef/Child", "ThingDef@Base")),
                Set());
            Assert.That(un, Is.Empty);
        }

        // A changed descendant whose ANCESTOR's raw file changed -> explained (raw change propagates).
        [Test]
        public void ChangedDescendant_AncestorRawChanged_IsExplained()
        {
            var un = PatchCoverageAudit.Unattributed(
                Set("ThingDef/Child"),
                Set(),
                Parents(("ThingDef/Child", "ThingDef@Base")),
                Set("ThingDef@Base"));
            Assert.That(un, Is.Empty);
        }

        // The smoking gun: a def changed, but neither it nor any ancestor was touched by a recorded
        // edge or a raw change -> UNATTRIBUTED (an op invisible to capture changed it).
        [Test]
        public void ChangedDef_NoVisibleCause_IsUnattributed()
        {
            var un = PatchCoverageAudit.Unattributed(
                Set("ThingDef/Ghost"),
                Set("ThingDef/Other"),
                Parents(("ThingDef/Ghost", "ThingDef@Base")),
                Set("ThingDef/Unrelated"));
            Assert.That(un, Is.EqualTo(new[] { "ThingDef/Ghost" }));
        }

        // Mixed: explained and unattributed defs together; only the unexplained ones are returned,
        // sorted.
        [Test]
        public void Mixed_ReturnsOnlyUnattributed_Sorted()
        {
            var un = PatchCoverageAudit.Unattributed(
                Set("ThingDef/Z_Ghost", "ThingDef/A_Patched", "ThingDef/M_Ghost", "ThingDef/R_Raw"),
                Set("ThingDef/A_Patched"),
                Parents(),
                Set("ThingDef/R_Raw"));
            Assert.That(un, Is.EqualTo(new[] { "ThingDef/M_Ghost", "ThingDef/Z_Ghost" }));
        }

        // Empty changed-set -> nothing to audit.
        [Test]
        public void NoChanges_Empty()
        {
            Assert.That(
                PatchCoverageAudit.Unattributed(Set(), Set("ThingDef/A"), Parents(), Set()),
                Is.Empty);
        }

        // A cyclic ParentName chain must not loop forever; an unexplained def in a cycle is still
        // flagged.
        [Test]
        public void CyclicInheritance_Terminates()
        {
            var un = PatchCoverageAudit.Unattributed(
                Set("ThingDef/A"),
                Set(),
                Parents(("ThingDef/A", "ThingDef/B"), ("ThingDef/B", "ThingDef/A")),
                Set());
            Assert.That(un, Is.EqualTo(new[] { "ThingDef/A" }));
        }
    }
}
