// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// MetricsLog.cs (Piece D — week-long real-world evidence base)
//
// Contains: an append-only JSON-Lines writer for the incremental-cache pipeline. Every call
// appends exactly ONE self-contained JSON object (one line) to a rolling log so the human can
// run the cache during normal play for a week and accumulate a real failure-evidence base —
// ERROR records being the priority signal — without a live harness.
//
// Where the log lives — and why it survives a cache clear: the per-load Cache directory
// (GagarinEnvironmentInfo.CacheFolderPath) is wiped recursively on a cold rebuild
// (ModsConfig.Reset -> ModsConfig_Patch, Directory.Delete(CacheFolderPath, recursive: true))
// and its key files are deleted whenever Context.IsUsingCache flips false. Writing the rolling
// log there would lose every prior load's records on the very rebuild we most want to study.
// So the log is written to the PARENT of the Cache folder — RocketEnvironmentInfo
// .CustomConfigFolderPath (".../Config/../MissileGirl/"), the same directory that holds the
// Cache subfolder and the Reports subfolder. That directory is created early
// (RocketMod / StartupHelper) and is never deleted by the cache lifecycle, so records from
// cold rebuilds, cache hits, and mod-list changes all accumulate in one file across the week.
//   Default path: <CustomConfigFolderPath>/incremental-metrics.jsonl
//
// Design constraints (all load-bearing):
//   - NEVER throws out of itself. Every public entry point swallows its own IO/serialization
//     errors (a metrics writer must not be able to break a load). It is therefore safe to call
//     before the file or directory exists — the first write creates the directory and file.
//   - Cheap: builds one line in a StringBuilder and does a single File.AppendAllText. No locks
//     beyond a process-local one (loads are effectively single-threaded here, but the lock keeps
//     two records from interleaving if a future caller is concurrent).
//   - Pure & offline-testable: this file has NO RimWorld / Verse dependency. The path is
//     injected (see ResolveLogPath, set once at startup by the pipeline) rather than read from
//     RocketEnvironmentInfo here, so the serialization, append semantics, and never-throw
//     guarantee are all unit-tested against System.IO temp paths without launching the game.
//
// Records share a common envelope (schema version, ISO-8601 UTC timestamp, load classification,
// running-mod count, and a cheap stable hash of the modlist so a week's worth of records from
// different modlists stay distinguishable) plus an "event" discriminator:
//   load_summary  — one per gated load: dirty count/total, seed breakdown, gate verdict,
//                   recompute verdict/fallback/subDocSize, and stage timings.
//   error         — an exception caught at a pipeline stage (TYPE name + message + stage). The
//                   priority signal; emitted whenever GAGARIN_METRICS is on and that stage runs.
//   inconsistency — the high-value anomalies: a gate FAIL (nonDirtyMismatches > 0 => the dirty
//                   set missed a real change, i.e. silent staleness) or a recompute FAIL
//                   (recomputeMismatches > 0 => a recompute fidelity gap).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Gagarin
{
    // The append-only writer. Pure (no Verse): callers pass already-resolved primitive values,
    // the path comes from ResolveLogPath. All public methods are no-throw.
    public static class MetricsLog
    {
        // Bump when the record shape changes so a week-spanning file can be parsed by version.
        // v2 adds seeds.addedDefs (the P2 added-defs channel) to the load_summary record; a file
        // spanning the bump stays parseable because the schema field tags every line's shape.
        public const int SchemaVersion = 2;

        // Default log file name under the (cache-clear-surviving) config folder.
        public const string LogFileName = "incremental-metrics.jsonl";

        // The pipeline sets this once at startup to RocketEnvironmentInfo.CustomConfigFolderPath
        // joined with LogFileName. Kept as an injectable resolver so the offline tests can point
        // it at a temp file without any RimWorld plumbing. Resolved lazily and never cached, so a
        // test can swap it between cases. If it returns null/empty or throws, writes are skipped.
        public static Func<string> ResolveLogPath = () => null;

        // Process-local guard so two records can't interleave mid-line if a caller is concurrent.
        private static readonly object s_lock = new object();

        // The shared envelope every record carries. Built once per load by the caller and reused
        // across that load's records so they correlate (same timestamp/classification/modlist).
        public struct Envelope
        {
            public string Timestamp;        // ISO-8601 UTC, e.g. 2026-06-21T14:03:11.482Z
            public string Load;             // "cold" (full rebuild) or "warm" (cache hit)
            public int ModCount;            // running-mod count this load
            public string ModListHash;      // cheap stable hash of the running modlist
        }

        // Capture an envelope from the current load classification + modlist. Pure helper so the
        // caller doesn't duplicate the timestamp/hash formatting. cold == full rebuild this load.
        public static Envelope NewEnvelope(bool cold, IEnumerable<string> packageIds)
        {
            var ids = new List<string>();
            if (packageIds != null)
                foreach (string id in packageIds)
                    ids.Add(id);
            return new Envelope
            {
                Timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                Load = cold ? "cold" : "warm",
                ModCount = ids.Count,
                ModListHash = ModListHash(ids)
            };
        }

        // A cheap, stable (order-sensitive) hash of the running modlist as an 8-hex-digit string.
        // Order-sensitive on purpose: a reorder changes what a load must recompute, so two loads
        // with the same mods in a different order should read as different modlists. FNV-1a 32-bit
        // over the '\n'-joined ids — deterministic across runs/processes (unlike string.GetHashCode,
        // which is randomized per-process on modern runtimes), so a week of records is comparable.
        public static string ModListHash(IEnumerable<string> packageIds)
        {
            const uint offset = 2166136261u;
            const uint prime = 16777619u;
            uint h = offset;
            if (packageIds != null)
            {
                bool first = true;
                foreach (string id in packageIds)
                {
                    if (!first) h = Step(h, (byte)'\n', prime);
                    first = false;
                    string s = id ?? string.Empty;
                    for (int i = 0; i < s.Length; i++)
                    {
                        char c = s[i];
                        h = Step(h, (byte)(c & 0xFF), prime);
                        h = Step(h, (byte)((c >> 8) & 0xFF), prime);
                    }
                }
            }
            return h.ToString("x8", CultureInfo.InvariantCulture);
        }

        private static uint Step(uint h, byte b, uint prime)
        {
            h ^= b;
            h *= prime;
            return h;
        }

        // ---- Record builders (pure: return the exact JSON line, no IO) --------------------

        // load_summary — the per-load roll-up. gatePass/recomputePass are nullable: null means
        // that stage did not run this load (e.g. cold first load with no prior cache, or the
        // corresponding diagnostic flag off), so a reader can tell "ran and passed" from "did
        // not run". recomputeFallback true => the recompute declined (changed-mod container op)
        // and the authoritative full rebuild stands.
        public static string BuildLoadSummary(
            Envelope env,
            int changedAssets, int changedMods,
            int dirtyCount, int totalNodes,
            int seedChangedDefs, int seedPatchModified, int seedReorder,
            int seedWildcardFlip, int seedAddedDefs, int inheritanceAdded,
            long computeMs,
            bool? gatePass, int nonDirtyMismatches, long gateMs,
            bool? recomputePass, bool recomputeFallback, int recomputeMismatches,
            int subDocSize, long recomputeMs)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            AppendEnvelope(sb, env, "load_summary");
            sb.Append(",\"changedAssets\":").Append(changedAssets);
            sb.Append(",\"changedMods\":").Append(changedMods);
            sb.Append(",\"dirtyCount\":").Append(dirtyCount);
            sb.Append(",\"totalNodes\":").Append(totalNodes);
            sb.Append(",\"seeds\":{");
            sb.Append("\"changedDefs\":").Append(seedChangedDefs);
            sb.Append(",\"patchModified\":").Append(seedPatchModified);
            sb.Append(",\"reorder\":").Append(seedReorder);
            sb.Append(",\"wildcardFlip\":").Append(seedWildcardFlip);
            sb.Append(",\"addedDefs\":").Append(seedAddedDefs);
            sb.Append(",\"inheritanceAdded\":").Append(inheritanceAdded);
            sb.Append('}');
            sb.Append(",\"computeMs\":").Append(computeMs);
            sb.Append(",\"gatePass\":").Append(Bool(gatePass));
            sb.Append(",\"nonDirtyMismatches\":").Append(nonDirtyMismatches);
            sb.Append(",\"gateMs\":").Append(gateMs);
            sb.Append(",\"recomputePass\":").Append(Bool(recomputePass));
            sb.Append(",\"recomputeFallback\":").Append(recomputeFallback ? "true" : "false");
            sb.Append(",\"recomputeMismatches\":").Append(recomputeMismatches);
            sb.Append(",\"subDocSize\":").Append(subDocSize);
            sb.Append(",\"recomputeMs\":").Append(recomputeMs);
            sb.Append('}');
            return sb.ToString();
        }

        // error — the priority signal. stage is the pipeline stage that caught it (e.g.
        // "diagnostic.prefix", "recompute"); exceptionType is the runtime TYPE name (so failures
        // can be counted/grouped by kind), message its Message text.
        public static string BuildError(Envelope env, string stage, string exceptionType, string message)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendEnvelope(sb, env, "error");
            sb.Append(",\"stage\":"); AppendQuoted(sb, stage);
            sb.Append(",\"exceptionType\":"); AppendQuoted(sb, exceptionType);
            sb.Append(",\"message\":"); AppendQuoted(sb, message);
            sb.Append('}');
            return sb.ToString();
        }

        // inconsistency — a proven anomaly: kind is "gate" (dirty set missed real changes =>
        // silent staleness) or "recompute" (recomputed defs differ from the full rebuild).
        // mismatchCount is the count of mismatching def ids; sampleIds is a small sample for
        // diagnosis (the full lists live in GateReport.json / RecomputeReport.json in the cache,
        // which may be wiped — the sample here survives in the rolling log).
        public static string BuildInconsistency(
            Envelope env, string kind, int mismatchCount, IEnumerable<string> sampleIds)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            AppendEnvelope(sb, env, "inconsistency");
            sb.Append(",\"kind\":"); AppendQuoted(sb, kind);
            sb.Append(",\"mismatchCount\":").Append(mismatchCount);
            sb.Append(",\"sampleIds\":[");
            if (sampleIds != null)
            {
                bool first = true;
                foreach (string id in sampleIds)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendQuoted(sb, id);
                }
            }
            sb.Append("]}");
            return sb.ToString();
        }

        // ---- Append (the only IO) ---------------------------------------------------------

        // Append one already-built JSON line to the resolved log path. NEVER throws: any failure
        // (null path, missing dir, IO error, serialization error in a caller's record) is
        // swallowed. Creates the directory and file on first write. A trailing '\n' makes the
        // file valid JSON-Lines (one object per line, easily greppable).
        public static void Append(string jsonLine)
        {
            try
            {
                if (string.IsNullOrEmpty(jsonLine))
                    return;
                string path = ResolveLogPath?.Invoke();
                if (string.IsNullOrEmpty(path))
                    return;
                lock (s_lock)
                {
                    string dir = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);
                    File.AppendAllText(path, jsonLine + "\n", Encoding.UTF8);
                }
            }
            catch
            {
                // A metrics writer must never be able to break a load. Swallow silently — there
                // is intentionally nowhere to escalate (logging here could itself fail/recurse).
            }
        }

        // ---- Shared serialization helpers -------------------------------------------------

        private static void AppendEnvelope(StringBuilder sb, Envelope env, string evt)
        {
            sb.Append("\"schema\":").Append(SchemaVersion);
            sb.Append(",\"event\":"); AppendQuoted(sb, evt);
            sb.Append(",\"ts\":"); AppendQuoted(sb, env.Timestamp);
            sb.Append(",\"load\":"); AppendQuoted(sb, env.Load);
            sb.Append(",\"modCount\":").Append(env.ModCount);
            sb.Append(",\"modListHash\":"); AppendQuoted(sb, env.ModListHash);
        }

        private static string Bool(bool? b) => b.HasValue ? (b.Value ? "true" : "false") : "null";

        // Minimal JSON string escaping matching the sibling writers (DirtySetGate etc.): quotes,
        // backslash, and the three control whitespace chars. A null value serializes as JSON null.
        private static void AppendQuoted(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else sb.Append(c);
            }
            sb.Append('"');
        }
    }
}
