// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// Program.cs (Piece B — patch classification)
//
// Contains: the console entry point and CLI argument handling for the classifier
// tool, with two modes — `classify <DependencyGraph.json> [-o out]` and
// `--selftest`.
//
// Used for: driving the tool from the command line; it parses the input graph,
// builds a ClassificationReport, writes/prints PatchClassification.json, and
// reports the headline identity:wildcard ratio to stderr.
//
// Why: Piece B is a standalone, offline CLI (no RimWorld, no live PatchOperation
// objects) so it can be run by hand over a captured graph and its output fed to
// Piece C without a game install.

using System;
using System.IO;
using Gagarin.IncrementalCache.Classification;
using Gagarin.IncrementalCache.Json;

namespace Gagarin.IncrementalCache
{
    // Piece B entry point. Two modes:
    //   classify <DependencyGraph.json> [-o PatchClassification.json]
    //   --selftest        run the offline classification tests against the bundled fixture
    internal static class Program
    {
        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
                {
                    PrintUsage();
                    return 0;
                }

                if (args[0] == "--selftest")
                    return SelfTests.RunAll() ? 0 : 1;

                if (args[0] == "classify")
                    return RunClassify(args);

                Console.Error.WriteLine($"Unknown command: {args[0]}");
                PrintUsage();
                return 2;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"error: {ex.Message}");
                return 1;
            }
        }

        private static int RunClassify(string[] args)
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("classify requires a path to DependencyGraph.json");
                return 2;
            }

            string inputPath = args[1];
            string outputPath = null;
            for (int i = 2; i < args.Length - 1; i++)
                if (args[i] == "-o" || args[i] == "--out")
                    outputPath = args[i + 1];

            if (!File.Exists(inputPath))
            {
                Console.Error.WriteLine($"input not found: {inputPath}");
                return 1;
            }

            JsonValue graph = JsonValue.Parse(File.ReadAllText(inputPath));
            ClassificationReport report = ClassificationReport.FromGraph(graph);
            string json = report.ToJson().ToString();

            if (outputPath != null)
            {
                File.WriteAllText(outputPath, json);
                Console.WriteLine($"wrote {outputPath}");
            }
            else
            {
                Console.WriteLine(json);
            }

            // Headline metric to stderr so it's visible even when stdout is redirected to a file.
            int total = report.IdentityCount + report.WildcardCount;
            Console.Error.WriteLine(
                $"classified {total} patches: identity={report.IdentityCount} " +
                $"wildcard={report.WildcardCount} ratio={report.IdentityRatio:P1} identity");
            return 0;
        }

        private static void PrintUsage()
        {
            Console.WriteLine("Gagarin incremental-cache patch classifier (Piece B)");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  classify <DependencyGraph.json> [-o <PatchClassification.json>]");
            Console.WriteLine("  --selftest    run offline classification tests on the bundled fixture");
        }
    }
}
