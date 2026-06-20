// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Gagarin.IncrementalCache.Json;

namespace Gagarin.IncrementalCache.Classification
{
    // Offline unit tests for the classifier. Run via `--selftest`; no RimWorld, no network,
    // no NuGet. Each Check* method asserts one behaviour; failures are collected and reported
    // together so a single run surfaces every regression.
    internal static class SelfTests
    {
        private static int passed;
        private static readonly List<string> failures = new List<string>();

        public static bool RunAll()
        {
            passed = 0;
            failures.Clear();

            DefNameAnchoredXpathIsIdentity();
            SingleQuotedDefNameIsIdentity();
            StructuralPredicateXpathIsWildcard();
            TextPredicateWildcardXpathIsWildcard();
            DescendantAxisDemotesAnchoredXpathToWildcard();
            MultiNodeMatchWithoutAnchorIsWildcard();
            SingleMatchedNodeFallbackIsIdentity();
            EmptyXpathFindModIsWildcard();
            MultiDefNameSequenceCapturesAllTargets();
            IdentityIndexMapsNodeToPatches();
            EndToEndSampleFixtureRatio();

            Console.WriteLine();
            Console.WriteLine($"selftest: {passed} passed, {failures.Count} failed");
            foreach (string f in failures)
                Console.WriteLine($"  FAIL: {f}");
            return failures.Count == 0;
        }

        // --- individual cases ---

        // A clean defName-anchored XPath must be identity and surface the target def.
        private static void DefNameAnchoredXpathIsIdentity()
        {
            var r = PatchClassifier.Classify(Edge("a", "mod", "Defs/ThingDef[defName=\"Steel\"]/statBases", "ThingDef/Steel"));
            Check("defName-anchored xpath -> identity", r.Classification == Classification.Identity);
            Check("identity target is Steel", r.TargetDefNames.Count == 1 && r.TargetDefNames[0] == "Steel");
            Check("identity predicate is null", r.Predicate == null);
        }

        private static void SingleQuotedDefNameIsIdentity()
        {
            var r = PatchClassifier.Classify(Edge("b", "mod", "Defs/ThingDef[defName = 'Plasteel']", "ThingDef/Plasteel"));
            Check("single-quoted defName -> identity", r.Classification == Classification.Identity);
            Check("single-quoted target parsed", r.TargetDefNames.Count == 1 && r.TargetDefNames[0] == "Plasteel");
        }

        // A structural attribute match (statBases present) has no defName anchor -> wildcard.
        private static void StructuralPredicateXpathIsWildcard()
        {
            var r = PatchClassifier.Classify(Edge("c", "mod", "Defs/ThingDef[statBases/Mass]/statBases/Mass", "ThingDef/Steel", "ThingDef/Plasteel"));
            Check("structural predicate -> wildcard", r.Classification == Classification.Wildcard);
            Check("wildcard keeps xpath predicate", r.Predicate != null && r.Predicate.Contains("statBases"));
            Check("wildcard has no identity targets", r.TargetDefNames.Count == 0);
        }

        private static void TextPredicateWildcardXpathIsWildcard()
        {
            var r = PatchClassifier.Classify(Edge("d", "mod", "//li[text()=\"OldTag\"]"));
            Check("text()/descendant predicate -> wildcard", r.Classification == Classification.Wildcard);
        }

        // defName present but combined with a descendant axis can fan out -> conservatively wildcard.
        private static void DescendantAxisDemotesAnchoredXpathToWildcard()
        {
            var r = PatchClassifier.Classify(Edge("e", "mod", "Defs/ThingDef[defName=\"Steel\"]//li[text()=\"Flammable\"]", "ThingDef/Steel"));
            Check("defName + descendant axis -> wildcard (conservative)", r.Classification == Classification.Wildcard);
        }

        // No anchor and several resolved nodes -> wildcard (can't reduce to one key).
        private static void MultiNodeMatchWithoutAnchorIsWildcard()
        {
            var r = PatchClassifier.Classify(Edge("f", "mod", "Defs/ThingDef[label=\"x\"]", "ThingDef/A", "ThingDef/B"));
            Check("multi-node match without anchor -> wildcard", r.Classification == Classification.Wildcard);
        }

        // No defName in xpath, but Piece A resolved exactly one node -> derive identity from it.
        private static void SingleMatchedNodeFallbackIsIdentity()
        {
            var r = PatchClassifier.Classify(Edge("g", "mod", "Defs/ThingDef[label=\"autopistol\"]/description", "ThingDef/Gun_Autopistol"));
            Check("single resolved node fallback -> identity", r.Classification == Classification.Identity);
            Check("fallback derives defName from nodeId", r.TargetDefNames.Count == 1 && r.TargetDefNames[0] == "Gun_Autopistol");
        }

        // Empty xpath (e.g. a FindMod wrapper) with no resolved nodes -> wildcard.
        private static void EmptyXpathFindModIsWildcard()
        {
            var r = PatchClassifier.Classify(Edge("h", "mod", ""));
            Check("empty xpath -> wildcard", r.Classification == Classification.Wildcard);
            Check("empty xpath predicate marked", r.Predicate == "(empty)");
        }

        // A union xpath naming two defs must capture both as identity targets.
        private static void MultiDefNameSequenceCapturesAllTargets()
        {
            var r = PatchClassifier.Classify(Edge("i", "mod",
                "Defs/ThingDef[defName=\"Steel\"]/statBases | Defs/ThingDef[defName=\"Plasteel\"]/statBases",
                "ThingDef/Steel", "ThingDef/Plasteel"));
            Check("union of two defNames -> identity", r.Classification == Classification.Identity);
            Check("both defNames captured",
                r.TargetDefNames.Count == 2 &&
                r.TargetDefNames.Contains("Steel") &&
                r.TargetDefNames.Contains("Plasteel"));
        }

        // The identity index must map each touched node to its patch ids.
        private static void IdentityIndexMapsNodeToPatches()
        {
            var graph = BuildGraph(
                Edge("p1", "mod", "Defs/ThingDef[defName=\"Steel\"]/x", "ThingDef/Steel"),
                Edge("p2", "mod", "Defs/ThingDef[defName=\"Steel\"]/y", "ThingDef/Steel"),
                Edge("p3", "mod", "Defs/ThingDef[statBases]/z", "ThingDef/Steel", "ThingDef/Plasteel"));
            var report = ClassificationReport.FromGraph(graph);

            Check("index has Steel entry", report.IdentityIndex.ContainsKey("ThingDef/Steel"));
            if (report.IdentityIndex.TryGetValue("ThingDef/Steel", out var ids))
            {
                Check("Steel indexed to both identity patches", ids.Count == 2 && ids.Contains("p1") && ids.Contains("p2"));
                Check("wildcard p3 not in index", !ids.Contains("p3"));
            }
            Check("counts: 2 identity / 1 wildcard", report.IdentityCount == 2 && report.WildcardCount == 1);
        }

        // End-to-end against the committed sample. Pins the headline ratio so changes to the
        // fixture or heuristic are caught.
        private static void EndToEndSampleFixtureRatio()
        {
            string fixture = LocateSampleFixture();
            if (fixture == null)
            {
                Check("sample fixture located", false);
                return;
            }

            JsonValue graph = JsonValue.Parse(File.ReadAllText(fixture));
            var report = ClassificationReport.FromGraph(graph);

            // 8 edges: identity = #1,#2 (clean defName), autopistol fallback, sequence union = 4.
            // wildcard = structural statBases, //li text(), defName+descendant, empty FindMod = 4.
            Check("sample: 4 identity", report.IdentityCount == 4);
            Check("sample: 4 wildcard", report.WildcardCount == 4);
            Check("sample: ratio 50%", Math.Abs(report.IdentityRatio - 0.5) < 1e-9);
            Check("sample: Steel indexed", report.IdentityIndex.ContainsKey("ThingDef/Steel"));
            Console.WriteLine($"  sample fixture ratio: {report.IdentityRatio:P1} identity " +
                              $"({report.IdentityCount} identity / {report.WildcardCount} wildcard)");
        }

        // --- helpers ---

        private static PatchEdge Edge(string id, string mod, string xpath, params string[] matched)
        {
            return new PatchEdge
            {
                PatchId = id,
                SourceMod = mod,
                OperationType = "PatchOperationReplace",
                Xpath = xpath,
                MatchedNodeIds = new List<string>(matched),
                ModifiedNodeIds = new List<string>(matched)
            };
        }

        private static JsonObject BuildGraph(params PatchEdge[] edges)
        {
            var root = new JsonObject();
            root.Add("version", new JsonNumber(1));
            var arr = new JsonArray();
            foreach (PatchEdge e in edges)
            {
                var o = new JsonObject();
                o.Add("patchId", new JsonString(e.PatchId));
                o.Add("sourceMod", new JsonString(e.SourceMod));
                o.Add("operationType", new JsonString(e.OperationType));
                o.Add("xpath", new JsonString(e.Xpath));
                var matched = new JsonArray();
                foreach (string m in e.MatchedNodeIds) matched.Add(new JsonString(m));
                o.Add("matchedNodeIds", matched);
                o.Add("modifiedNodeIds", matched);
                arr.Add(o);
            }
            root.Add("patchEdges", arr);
            return root;
        }

        // The fixture is copied next to the assembly at build time, but also exists in the
        // source tree. Probe both so the test runs from bin/ and from a source checkout.
        private static string LocateSampleFixture()
        {
            const string name = "DependencyGraph.sample.json";
            string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var candidates = new[]
            {
                Path.Combine(asmDir ?? ".", name),
                Path.Combine(asmDir ?? ".", "Fixtures", name),
                Path.Combine(Directory.GetCurrentDirectory(), "Fixtures", name),
                Path.Combine(Directory.GetCurrentDirectory(), name)
            };
            foreach (string c in candidates)
                if (File.Exists(c))
                    return c;
            return null;
        }

        private static void Check(string label, bool condition)
        {
            if (condition)
                passed++;
            else
                failures.Add(label);
        }
    }
}
