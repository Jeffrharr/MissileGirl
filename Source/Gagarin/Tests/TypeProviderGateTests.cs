// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// TypeProviderGateTests.cs (Piece D — recompute fidelity for custom Def-type providers, issue #86)
//
// Contains: the offline correctness gate for TypeProviderGate.Passes, plus
// DependencyGraphModel.BuildTypeProviderByNodeId's inversion of the captured typeProviders index.

using System.Collections.Generic;
using System.Linq;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class TypeProviderGateTests
    {
        private static System.Func<IEnumerable<string>, bool> AnyActive(params string[] active)
        {
            var set = new HashSet<string>(active.Select(s => s.ToLower()));
            return ids => ids.Any(id => set.Contains(id.ToLower()));
        }

        [Test]
        public void NoProvider_AlwaysPasses()
        {
            // null providerPackageId means the Type came from a base-game/vanilla assembly --
            // no gate applies regardless of what's active.
            Assert.That(TypeProviderGate.Passes(null, AnyActive()), Is.True);
        }

        [Test]
        public void ProviderActive_Passes()
        {
            Assert.That(TypeProviderGate.Passes("nals.facialanimation",
                AnyActive("nals.facialanimation")), Is.True);
        }

        [Test]
        public void ProviderRemoved_Drops()
        {
            Assert.That(TypeProviderGate.Passes("nals.facialanimation",
                AnyActive("some.othermod")), Is.False);
        }

        [Test]
        public void IsCaseInsensitive()
        {
            // ProvenanceRecorder/AssemblyOwnerLookup treat packageIds via ModLister's own
            // comparisons; the real ModLister lowercases too.
            Assert.That(TypeProviderGate.Passes("Nals.FacialAnimation",
                AnyActive("nals.facialanimation")), Is.True);
        }
    }

    [TestFixture]
    public class DependencyGraphModelTypeProviderTests
    {
        [Test]
        public void BuildTypeProviderByNodeId_InvertsPackageIdToNodeIds()
        {
            var graph = new DependencyGraphData();
            graph.TypeProviderIndex["nals.facialanimation"] = new List<string>
            {
                "FacialAnimation.EyeballColorDef/Eyes_Red",
                "FacialAnimation.EyeballColorDef/Eyes_Gray",
            };
            graph.TypeProviderIndex["joof.testharness.typeprovider"] = new List<string>
            {
                "JoofTest.GadgetDef/TC_Gadget_A",
            };

            Dictionary<string, string> byNodeId = graph.BuildTypeProviderByNodeId();

            Assert.That(byNodeId["FacialAnimation.EyeballColorDef/Eyes_Red"],
                Is.EqualTo("nals.facialanimation"));
            Assert.That(byNodeId["FacialAnimation.EyeballColorDef/Eyes_Gray"],
                Is.EqualTo("nals.facialanimation"));
            Assert.That(byNodeId["JoofTest.GadgetDef/TC_Gadget_A"],
                Is.EqualTo("joof.testharness.typeprovider"));
            Assert.That(byNodeId.Count, Is.EqualTo(3));
        }

        [Test]
        public void BuildTypeProviderByNodeId_EmptyIndex_ReturnsEmpty()
        {
            var graph = new DependencyGraphData();
            Assert.That(graph.BuildTypeProviderByNodeId(), Is.Empty);
        }
    }
}
