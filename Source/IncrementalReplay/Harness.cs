// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Xml;
using Microsoft.XmlDiffPatch;

namespace Gagarin.IncrementalReplay
{
    // Orchestrates one incremental-replay experiment:
    //   baseline unified doc + (graph, classification) + a single-mod mutation
    //     -> dirty set -> recompute only dirty nodes -> splice into baseline
    //     -> diff against a full rebuild of the mutated fixture (zero diff == correct)
    //     -> report timings.

    internal sealed class ReplayOutcome
    {
        public string Name;
        public int DirtyCount;
        public int TotalNodes;
        public int Iterations;
        public bool ZeroDiff;
        public string DiffGram;            // populated only on failure, for debugging
        public double FullRebuildMs;
        public double IncrementalMs;       // dirty-set + recompute + splice
        public double Speedup => IncrementalMs > 0 ? FullRebuildMs / IncrementalMs : 0;
    }

    internal static class Harness
    {
        public static ReplayOutcome Run(string name, Fixture baseline, Fixture mutated, string changedMod)
        {
            var outcome = new ReplayOutcome { Name = name };

            // Inputs that, in production, come from Pieces A and B. We round-trip the
            // graph through JSON to prove the harness only relies on the documented
            // contract; the classification is built for completeness (its identity index
            // and predicates feed the dirty-set seeding) and likewise round-tripped.
            var graph = DependencyGraph.FromJson(FixtureBuilder.BuildGraph(baseline).ToJson());
            PatchClassification.FromJson(FixtureBuilder.BuildClassification(baseline).ToJson());

            // Baseline unified document (the cached Unified.xml we start from) plus the
            // loaded mutated indices (the cache load Gagarin would already have done).
            var baselineUnified = ApplyModel.FullRebuild(baseline);
            outcome.TotalNodes = baselineUnified.DocumentElement.ChildNodes.Count;
            var mutatedState = LoadedState.Build(mutated, graph);

            // --- FULL rebuild path (ground truth + timing baseline) ---
            var swFull = Stopwatch.StartNew();
            var fullUnified = ApplyModel.FullRebuild(mutated);
            swFull.Stop();
            outcome.FullRebuildMs = swFull.Elapsed.TotalMilliseconds;

            // --- INCREMENTAL path (the production-shaped work: dirty set + targeted
            //     recompute + splice; the verification diff below is NOT part of this) ---
            var swInc = Stopwatch.StartNew();
            var dirty = DirtySet.ComputeWithState(baseline, mutated, changedMod, graph, mutatedState);
            var recomputed = ApplyModel.RecomputeDirtyWithState(mutatedState, dirty.Nodes);
            var spliced = Splice(baselineUnified, recomputed, dirty.Nodes);
            swInc.Stop();
            outcome.IncrementalMs = swInc.Elapsed.TotalMilliseconds;
            outcome.DirtyCount = dirty.Nodes.Count;
            outcome.Iterations = dirty.Iterations;

            // --- Correctness oracle: canonical XML diff ---
            outcome.ZeroDiff = XmlEqual(spliced, fullUnified, out var diffgram);
            if (!outcome.ZeroDiff) outcome.DiffGram = diffgram;

            return outcome;
        }

        // Negative control: the UNSOUND "reuse unchanged prefix" strategy. It dirties
        // only the changed mod's own defs and ignores that an earlier mod's forward
        // wildcard (or cross-mod inheritance) can be affected. Returns whether its result
        // happens to match a full rebuild — which, on a real hazard case, it must NOT.
        // Used to prove the harness is genuinely detecting the cross-mod hazard rather
        // than passing trivially.
        public static bool RunNaive(Fixture baseline, Fixture mutated, string changedMod)
        {
            var graph = FixtureBuilder.BuildGraph(baseline);
            var baselineUnified = ApplyModel.FullRebuild(baseline);
            var fullUnified = ApplyModel.FullRebuild(mutated);
            var mutatedState = LoadedState.Build(mutated, graph);

            // Naive dirty set: just the changed mod's declared def ids.
            var naiveDirty = new HashSet<string>();
            foreach (var mod in mutated.Mods)
                if (mod.PackageId == changedMod)
                    foreach (var d in mod.Defs)
                        naiveDirty.Add(d.NodeId);

            var recomputed = ApplyModel.RecomputeDirtyWithState(mutatedState, naiveDirty);
            var spliced = Splice(baselineUnified, recomputed, naiveDirty);
            return XmlEqual(spliced, fullUnified, out _);
        }

        // Scaled benchmark: measures how the incremental path scales against a full
        // rebuild as the load order grows. Mirrors production: the graph + classification
        // + baseline Unified.xml are treated as ALREADY CACHED (built once, outside the
        // timed region), so the timed incremental work is just dirty-set + targeted
        // recompute + splice — exactly what happens when one mod changes on next launch.
        // Correctness is still verified once (zero-diff) before reporting timings.
        public static ReplayOutcome RunBenchmark(
            string name, Fixture baseline, Fixture mutated, string changedMod, int reps)
        {
            var outcome = new ReplayOutcome { Name = name };

            // --- Cached inputs (NOT timed): graph, baseline Unified.xml, and the loaded
            //     mutated indices. In production these are read from the cache on launch;
            //     building them here once models that one-time load cost, which the
            //     incremental path does NOT repay on every single-mod change. ---
            var graph = FixtureBuilder.BuildGraph(baseline);
            var baselineUnified = ApplyModel.FullRebuild(baseline);
            outcome.TotalNodes = baselineUnified.DocumentElement.ChildNodes.Count;
            var mutatedState = LoadedState.Build(mutated, graph);

            // --- Correctness check (once): spliced incremental == full rebuild ---
            var fullOnce = ApplyModel.FullRebuild(mutated);
            var dirtyOnce = DirtySet.ComputeWithState(baseline, mutated, changedMod, graph, mutatedState);
            outcome.DirtyCount = dirtyOnce.Nodes.Count;
            outcome.Iterations = dirtyOnce.Iterations;
            var splicedOnce = Splice(baselineUnified,
                ApplyModel.RecomputeDirtyWithState(mutatedState, dirtyOnce.Nodes), dirtyOnce.Nodes);
            outcome.ZeroDiff = XmlEqual(splicedOnce, fullOnce, out var dg);
            if (!outcome.ZeroDiff) outcome.DiffGram = dg;

            // --- Timing: best-of-N for both paths ---
            // Full path: from-scratch rebuild of the whole mutated load order.
            // Incremental path: reuse the loaded state -> dirty set + targeted recompute
            // + splice into a clone of the cached baseline Unified.xml.
            double bestFull = double.MaxValue, bestInc = double.MaxValue;
            for (int i = 0; i < reps; i++)
            {
                var sw = Stopwatch.StartNew();
                ApplyModel.FullRebuild(mutated);
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds < bestFull) bestFull = sw.Elapsed.TotalMilliseconds;

                sw.Restart();
                var dirty = DirtySet.ComputeWithState(baseline, mutated, changedMod, graph, mutatedState);
                var recomputed = ApplyModel.RecomputeDirtyWithState(mutatedState, dirty.Nodes);
                Splice(baselineUnified, recomputed, dirty.Nodes);
                sw.Stop();
                if (sw.Elapsed.TotalMilliseconds < bestInc) bestInc = sw.Elapsed.TotalMilliseconds;
            }
            outcome.FullRebuildMs = bestFull;
            outcome.IncrementalMs = bestInc;
            return outcome;
        }

        // Splice recomputed dirty nodes into a CLONE of the baseline unified document:
        // remove any baseline node whose id is dirty, then insert the recomputed version
        // (if it still exists after the mutation), keeping the document in stable
        // (defType, defName) order so the result is canonically comparable.
        private static XmlDocument Splice(
            XmlDocument baseline, Dictionary<string, ApplyModel.WorkingDef> recomputed,
            HashSet<string> dirty)
        {
            // Collect surviving (non-dirty) baseline nodes keyed by id.
            var nodes = new Dictionary<string, XmlElement>();
            foreach (XmlElement el in baseline.DocumentElement.ChildNodes)
            {
                string id = el.Name + "/" + el.GetAttribute("defName");
                if (!dirty.Contains(id))
                    nodes[id] = el;
            }

            // Add recomputed dirty nodes (dirty defs that were deleted simply never
            // reappear, which is correct).
            var recomputedDoc = ApplyModel.ToUnified(recomputed);
            foreach (XmlElement el in recomputedDoc.DocumentElement.ChildNodes)
            {
                string id = el.Name + "/" + el.GetAttribute("defName");
                nodes[id] = el;
            }

            // Re-emit in stable order.
            var keys = new List<string>(nodes.Keys);
            keys.Sort(System.StringComparer.Ordinal);
            var doc = new XmlDocument();
            var root = doc.CreateElement("Defs");
            doc.AppendChild(root);
            foreach (var k in keys)
                root.AppendChild(doc.ImportNode(nodes[k], true));
            return doc;
        }

        // Canonical XML equality via XMLDiffPatch (the same package the mod ships).
        // Order-insensitive so spliced vs full-rebuild compare structurally.
        private static bool XmlEqual(XmlDocument a, XmlDocument b, out string diffgram)
        {
            var diff = new XmlDiff(
                XmlDiffOptions.IgnoreChildOrder |
                XmlDiffOptions.IgnoreComments |
                XmlDiffOptions.IgnoreWhitespace |
                XmlDiffOptions.IgnoreXmlDecl);

            using var sw = new StringWriter();
            using (var xw = XmlWriter.Create(sw, new XmlWriterSettings { OmitXmlDeclaration = true }))
            {
                bool equal = diff.Compare(a, b, xw);
                xw.Flush();
                diffgram = equal ? null : sw.ToString();
                return equal;
            }
        }
    }
}
