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
            // null providerPackageIds means the Type came from a base-game/vanilla assembly --
            // no gate applies regardless of what's active.
            Assert.That(TypeProviderGate.Passes(null, AnyActive()), Is.True);
        }

        [Test]
        public void EmptyProviderList_AlwaysPasses()
        {
            Assert.That(TypeProviderGate.Passes(new List<string>(), AnyActive()), Is.True);
        }

        [Test]
        public void ProviderActive_Passes()
        {
            Assert.That(TypeProviderGate.Passes(new[] { "nals.facialanimation" },
                AnyActive("nals.facialanimation")), Is.True);
        }

        [Test]
        public void ProviderRemoved_Drops()
        {
            Assert.That(TypeProviderGate.Passes(new[] { "nals.facialanimation" },
                AnyActive("some.othermod")), Is.False);
        }

        [Test]
        public void IsCaseInsensitive()
        {
            // ProvenanceRecorder/AssemblyOwnerLookup treat packageIds via ModLister's own
            // comparisons; the real ModLister lowercases too.
            Assert.That(TypeProviderGate.Passes(new[] { "Nals.FacialAnimation" },
                AnyActive("nals.facialanimation")), Is.True);
        }

        [Test]
        public void OneOfMultipleProvidersStillActive_Passes()
        {
            // Two mods can share an identity-matching assembly (.NET/Mono's Assembly.LoadFrom
            // binding). Removing whichever one was captured as "the" provider must not drop the
            // def while another recorded candidate is still active.
            Assert.That(TypeProviderGate.Passes(new[] { "mod.a", "mod.b" },
                AnyActive("mod.b")), Is.True);
        }

        [Test]
        public void AllProvidersRemoved_Drops()
        {
            Assert.That(TypeProviderGate.Passes(new[] { "mod.a", "mod.b" },
                AnyActive("some.othermod")), Is.False);
        }

        [Test]
        public void ResolveProviderIndex_FlagOff_ReturnsEmpty_RegardlessOfGraphContent()
        {
            // Pins DefRecompute's entire pre-live-validation safety argument: with the flag
            // off, the resolved index must be empty no matter what the graph contains, so the
            // gate is unreachable-by-construction (TryGetValue can never succeed).
            var graph = new DependencyGraphData();
            graph.TypeProviderIndex["nals.facialanimation"] = new List<string>
            {
                "FacialAnimation.EyeballColorDef/Eyes_Red",
            };

            Dictionary<string, List<string>> result = TypeProviderGate.ResolveProviderIndex(false, graph);

            Assert.That(result, Is.Empty);
        }

        [Test]
        public void ResolveProviderIndex_FlagOn_ReturnsGraphsProviderIndex()
        {
            var graph = new DependencyGraphData();
            graph.TypeProviderIndex["nals.facialanimation"] = new List<string>
            {
                "FacialAnimation.EyeballColorDef/Eyes_Red",
            };

            Dictionary<string, List<string>> result = TypeProviderGate.ResolveProviderIndex(true, graph);

            Assert.That(result["FacialAnimation.EyeballColorDef/Eyes_Red"],
                Is.EquivalentTo(new[] { "nals.facialanimation" }));
        }

        [Test]
        public void ResolveProviderIndex_FlagOn_NullGraph_ReturnsEmpty()
        {
            Assert.That(TypeProviderGate.ResolveProviderIndex(true, null), Is.Empty);
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

            Dictionary<string, List<string>> byNodeId = graph.BuildTypeProviderByNodeId();

            Assert.That(byNodeId["FacialAnimation.EyeballColorDef/Eyes_Red"],
                Is.EquivalentTo(new[] { "nals.facialanimation" }));
            Assert.That(byNodeId["FacialAnimation.EyeballColorDef/Eyes_Gray"],
                Is.EquivalentTo(new[] { "nals.facialanimation" }));
            Assert.That(byNodeId["JoofTest.GadgetDef/TC_Gadget_A"],
                Is.EquivalentTo(new[] { "joof.testharness.typeprovider" }));
            Assert.That(byNodeId.Count, Is.EqualTo(3));
        }

        [Test]
        public void BuildTypeProviderByNodeId_EmptyIndex_ReturnsEmpty()
        {
            var graph = new DependencyGraphData();
            Assert.That(graph.BuildTypeProviderByNodeId(), Is.Empty);
        }

        [Test]
        public void BuildTypeProviderByNodeId_MultipleProvidersForSameNode_CollectsAll()
        {
            // Mirrors AssemblyOwnerLookup recording every candidate mod that shares an
            // identity-matching assembly for one node (see ProvenanceRecorder.RegisterNode).
            var graph = new DependencyGraphData();
            graph.TypeProviderIndex["mod.a"] = new List<string> { "JoofTest.GadgetDef/TC_Shared" };
            graph.TypeProviderIndex["mod.b"] = new List<string> { "JoofTest.GadgetDef/TC_Shared" };

            Dictionary<string, List<string>> byNodeId = graph.BuildTypeProviderByNodeId();

            Assert.That(byNodeId["JoofTest.GadgetDef/TC_Shared"],
                Is.EquivalentTo(new[] { "mod.a", "mod.b" }));
        }
    }
}
