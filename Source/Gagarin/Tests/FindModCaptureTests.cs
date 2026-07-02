// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// FindModCaptureTests.cs (issue #40)
//
// Covers the pure logic factored out of ProvenanceRecorder.IndexFindMod /
// MaybeRecordUnresolvedGate (both RimWorld-coupled, so untestable offline): the
// display-name -> packageId resolution, and the "does this unrecognized type need the
// generic branch fallback" decision.

using System.Collections.Generic;
using Gagarin;
using NUnit.Framework;

namespace Gagarin.Tests
{
    [TestFixture]
    public class FindModCaptureTests
    {
        [Test]
        public void ResolvePackageIds_ResolvesEachNameViaLookup()
        {
            var names = new List<string> { "Facial Animation", "Facial Animation -Experimental-" };
            var lookup = new Dictionary<string, string>
            {
                ["Facial Animation"] = "nals.facialanimation",
                ["Facial Animation -Experimental-"] = "nals.facialanimationexperimentals",
            };

            List<string> result = FindModCapture.ResolvePackageIds(names, n =>
                lookup.TryGetValue(n, out string pkg) ? pkg : null);

            Assert.That(result, Is.EqualTo(new[]
            {
                "nals.facialanimation", "nals.facialanimationexperimentals"
            }));
        }

        [Test]
        public void ResolvePackageIds_DropsUnresolvedNames()
        {
            // A name that resolves to no installed mod (never in the running list) must be
            // silently dropped, not surfaced as e.g. a null packageId.
            var names = new List<string> { "Facial Animation", "Some Uninstalled Mod" };

            List<string> result = FindModCapture.ResolvePackageIds(names,
                n => n == "Facial Animation" ? "nals.facialanimation" : null);

            Assert.That(result, Is.EqualTo(new[] { "nals.facialanimation" }));
        }

        [Test]
        public void ResolvePackageIds_DedupesSamePackageId()
        {
            // Two display names (e.g. an alias) resolving to the same packageId must only
            // appear once -- AddMayRequire is idempotent per pair anyway, but a clean list
            // keeps the capture-side loop O(names) rather than O(names * duplicates).
            var names = new List<string> { "Facial Animation", "Facial Animation (alias)" };

            List<string> result = FindModCapture.ResolvePackageIds(names, _ => "nals.facialanimation");

            Assert.That(result, Is.EqualTo(new[] { "nals.facialanimation" }));
        }

        [Test]
        public void ResolvePackageIds_NullInputs_ReturnEmpty()
        {
            Assert.That(FindModCapture.ResolvePackageIds(null, _ => "x"), Is.Empty);
            Assert.That(FindModCapture.ResolvePackageIds(new List<string> { "a" }, null), Is.Empty);
        }

        [Test]
        public void NeedsGenericFallback_KnownTypes_NeverFallBack()
        {
            // Even though PatchOperationFindMod/Conditional DO carry match/nomatch fields,
            // they have dedicated readers and must not also feed the generic bucket.
            Assert.That(FindModCapture.NeedsGenericFallback("PatchOperationFindMod", true), Is.False);
            Assert.That(FindModCapture.NeedsGenericFallback("PatchOperationConditional", true), Is.False);
        }

        [Test]
        public void NeedsGenericFallback_UnknownTypeWithoutBranchFields_NoFallback()
        {
            // A plain Add/Remove/Replace op has no match/nomatch fields -- not a branch
            // construct at all, so it must not be flagged.
            Assert.That(FindModCapture.NeedsGenericFallback("PatchOperationAdd", false), Is.False);
        }

        [Test]
        public void NeedsGenericFallback_UnknownTypeWithBranchFields_FallsBack()
        {
            // A third-party FindMod-alike sharing the match/nomatch convention but with no
            // dedicated reader IS the case this mechanism exists to flag.
            Assert.That(FindModCapture.NeedsGenericFallback("SomeThirdParty_PatchOperationFindModAlike", true),
                Is.True);
        }
    }
}
