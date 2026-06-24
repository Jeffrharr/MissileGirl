// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// MetricsLogTests.cs (Piece D — week-long evidence base)
//
// Contains: the offline correctness gate for MetricsLog — the append-only JSON-Lines writer that
// persists the incremental cache's per-load metrics across a week of play. MetricsLog is pure
// (no RimWorld dependency), so it is compiled into this net8.0 NUnit project and driven against
// System.IO temp files.
//
// Why: this writer is the ONLY thing standing between a week of real play and the evidence base,
// so the load-bearing guarantees are asserted here: (1) each record is well-formed JSON carrying
// the expected envelope + fields (round-trips through System.Text.Json); (2) Append is truly
// append-only (a second write adds a line, never truncates) and creates the file/dir on first
// use; (3) Append NEVER throws, even on an unwritable path or a null resolver — a metrics writer
// must not be able to break a load. The modlist hash's determinism (it must be comparable across
// a week of separate processes) is asserted too.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Gagarin;

namespace Gagarin.Tests
{
    [TestFixture]
    public class MetricsLogTests
    {
        private string _dir;
        private string _path;
        private Func<string> _savedResolver;

        [SetUp]
        public void SetUp()
        {
            _savedResolver = MetricsLog.ResolveLogPath;
            _dir = Path.Combine(Path.GetTempPath(), "gagarin-metrics-" + Guid.NewGuid().ToString("N"));
            _path = Path.Combine(_dir, MetricsLog.LogFileName);
            MetricsLog.ResolveLogPath = () => _path;
        }

        [TearDown]
        public void TearDown()
        {
            MetricsLog.ResolveLogPath = _savedResolver; // don't leak the temp resolver to other tests
            try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
        }

        private static MetricsLog.Envelope Env() =>
            MetricsLog.NewEnvelope(cold: true, packageIds: new[] { "ludeon.rimworld", "a.b" });

        // Reads the log file back as one parsed JSON object per non-empty line.
        private List<JsonElement> ReadLines()
        {
            var result = new List<JsonElement>();
            foreach (string line in File.ReadAllLines(_path))
            {
                if (line.Length == 0) continue;
                using var doc = JsonDocument.Parse(line);
                result.Add(doc.RootElement.Clone());
            }
            return result;
        }

        [Test]
        public void LoadSummary_RoundTrips_WithEnvelopeAndFields()
        {
            string line = MetricsLog.BuildLoadSummary(
                Env(), changedAssets: 3, changedMods: 1, dirtyCount: 245, totalNodes: 28682,
                seedChangedDefs: 5, seedPatchModified: 7, seedReorder: 0, seedWildcardFlip: 2,
                seedAddedDefs: 4, inheritanceAdded: 231, computeMs: 12,
                gatePass: true, nonDirtyMismatches: 0, gateMs: 40,
                recomputePass: true, recomputeFallback: false, recomputeMismatches: 0,
                subDocSize: 337, recomputeMs: 88);
            MetricsLog.Append(line);

            var rows = ReadLines();
            Assert.That(rows, Has.Count.EqualTo(1));
            JsonElement r = rows[0];
            Assert.That(r.GetProperty("schema").GetInt32(), Is.EqualTo(MetricsLog.SchemaVersion));
            Assert.That(r.GetProperty("event").GetString(), Is.EqualTo("load_summary"));
            Assert.That(r.GetProperty("load").GetString(), Is.EqualTo("cold"));
            Assert.That(r.GetProperty("modCount").GetInt32(), Is.EqualTo(2));
            Assert.That(r.GetProperty("modListHash").GetString(), Is.Not.Empty);
            Assert.That(r.GetProperty("dirtyCount").GetInt32(), Is.EqualTo(245));
            Assert.That(r.GetProperty("totalNodes").GetInt32(), Is.EqualTo(28682));
            Assert.That(r.GetProperty("seeds").GetProperty("inheritanceAdded").GetInt32(), Is.EqualTo(231));
            // P2 — the added-defs seed count is carried in the seeds object (schema v2).
            Assert.That(r.GetProperty("seeds").GetProperty("addedDefs").GetInt32(), Is.EqualTo(4));
            Assert.That(r.GetProperty("gatePass").GetBoolean(), Is.True);
            Assert.That(r.GetProperty("recomputeFallback").GetBoolean(), Is.False);
            Assert.That(r.GetProperty("subDocSize").GetInt32(), Is.EqualTo(337));
            // ISO-8601 UTC timestamp parses.
            Assert.That(DateTime.TryParse(r.GetProperty("ts").GetString(), out _), Is.True);
        }

        [Test]
        public void NullableVerdicts_SerializeAsJsonNull()
        {
            string line = MetricsLog.BuildLoadSummary(
                Env(), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                gatePass: null, nonDirtyMismatches: 0, gateMs: 0,
                recomputePass: null, recomputeFallback: false, recomputeMismatches: 0,
                subDocSize: 0, recomputeMs: 0);
            MetricsLog.Append(line);

            JsonElement r = ReadLines()[0];
            Assert.That(r.GetProperty("gatePass").ValueKind, Is.EqualTo(JsonValueKind.Null));
            Assert.That(r.GetProperty("recomputePass").ValueKind, Is.EqualTo(JsonValueKind.Null));
        }

        [Test]
        public void Error_RecordsTypeMessageAndStage()
        {
            string line = MetricsLog.BuildError(Env(), "recompute", "NullReferenceException",
                "object reference \"x\" not set\n\twith control chars");
            MetricsLog.Append(line);

            JsonElement r = ReadLines()[0];
            Assert.That(r.GetProperty("event").GetString(), Is.EqualTo("error"));
            Assert.That(r.GetProperty("stage").GetString(), Is.EqualTo("recompute"));
            Assert.That(r.GetProperty("exceptionType").GetString(), Is.EqualTo("NullReferenceException"));
            // The control chars (quote / newline / tab) must round-trip via the escaping.
            Assert.That(r.GetProperty("message").GetString(),
                Is.EqualTo("object reference \"x\" not set\n\twith control chars"));
        }

        [Test]
        public void Inconsistency_RecordsKindCountAndSample()
        {
            string line = MetricsLog.BuildInconsistency(
                Env(), "gate", 12, new[] { "ThingDef/A", "ThingDef/B" });
            MetricsLog.Append(line);

            JsonElement r = ReadLines()[0];
            Assert.That(r.GetProperty("event").GetString(), Is.EqualTo("inconsistency"));
            Assert.That(r.GetProperty("kind").GetString(), Is.EqualTo("gate"));
            Assert.That(r.GetProperty("mismatchCount").GetInt32(), Is.EqualTo(12));
            var sample = r.GetProperty("sampleIds");
            Assert.That(sample.GetArrayLength(), Is.EqualTo(2));
            Assert.That(sample[0].GetString(), Is.EqualTo("ThingDef/A"));
        }

        [Test]
        public void Append_IsAppendOnly_SecondWriteAddsLineNotTruncates()
        {
            MetricsLog.Append(MetricsLog.BuildError(Env(), "s1", "E1", "m1"));
            MetricsLog.Append(MetricsLog.BuildError(Env(), "s2", "E2", "m2"));
            MetricsLog.Append(MetricsLog.BuildInconsistency(Env(), "recompute", 1, new[] { "X" }));

            var rows = ReadLines();
            Assert.That(rows, Has.Count.EqualTo(3));
            Assert.That(rows[0].GetProperty("stage").GetString(), Is.EqualTo("s1"));
            Assert.That(rows[1].GetProperty("stage").GetString(), Is.EqualTo("s2"));
            Assert.That(rows[2].GetProperty("event").GetString(), Is.EqualTo("inconsistency"));
        }

        [Test]
        public void Append_CreatesDirectoryAndFileOnFirstWrite()
        {
            Assert.That(Directory.Exists(_dir), Is.False, "precondition: dir must not exist yet");
            MetricsLog.Append(MetricsLog.BuildError(Env(), "s", "E", "m"));
            Assert.That(File.Exists(_path), Is.True);
        }

        [Test]
        public void Append_NeverThrows_OnNullResolver()
        {
            MetricsLog.ResolveLogPath = () => null;
            Assert.DoesNotThrow(() => MetricsLog.Append(MetricsLog.BuildError(Env(), "s", "E", "m")));
        }

        [Test]
        public void Append_NeverThrows_OnThrowingResolver()
        {
            MetricsLog.ResolveLogPath = () => throw new InvalidOperationException("boom");
            Assert.DoesNotThrow(() => MetricsLog.Append(MetricsLog.BuildError(Env(), "s", "E", "m")));
        }

        [Test]
        public void Append_NeverThrows_OnUnwritablePath()
        {
            // A path whose "directory" is actually an existing file — Directory.CreateDirectory and
            // the subsequent write both fail, and Append must swallow it.
            string fileAsDir = Path.Combine(Path.GetTempPath(), "gagarin-metrics-file-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(fileAsDir, "x");
            try
            {
                MetricsLog.ResolveLogPath = () => Path.Combine(fileAsDir, "nested", MetricsLog.LogFileName);
                Assert.DoesNotThrow(() => MetricsLog.Append(MetricsLog.BuildError(Env(), "s", "E", "m")));
            }
            finally { try { File.Delete(fileAsDir); } catch { } }
        }

        [Test]
        public void Append_NeverThrows_OnNullOrEmptyLine()
        {
            Assert.DoesNotThrow(() => MetricsLog.Append(null));
            Assert.DoesNotThrow(() => MetricsLog.Append(""));
            Assert.That(File.Exists(_path), Is.False, "an empty line must not create the file");
        }

        [Test]
        public void ModListHash_IsDeterministic_OrderSensitive_AndCheap()
        {
            string a = MetricsLog.ModListHash(new[] { "x", "y", "z" });
            string b = MetricsLog.ModListHash(new[] { "x", "y", "z" });
            string reordered = MetricsLog.ModListHash(new[] { "z", "y", "x" });
            Assert.That(a, Is.EqualTo(b), "same modlist must hash identically across calls/processes");
            Assert.That(a, Is.Not.EqualTo(reordered), "order change must change the hash");
            Assert.That(a, Has.Length.EqualTo(8), "8 hex digits");
        }
    }
}
