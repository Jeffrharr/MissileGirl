// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DirtySetGate.cs (Piece D — Milestone 2b: real-engine zero-diff gate)
//
// Contains: the RimWorld-facing driver that proves, against the REAL engine, that the dirty
// set (M1 structural + M2a wildcard flips) is a true SUPERSET of what actually changed on a
// given load. It snapshots the prior Unified.xml before the load deletes it, lets Gagarin's
// normal full rebuild write the new Unified.xml, then diffs the two: every def NOT in the
// dirty set must be byte-identical between prior cache and rebuild. A non-dirty mismatch is a
// subset error — the silent-staleness failure the whole incremental effort guards against.
//
// Used for: closing the long-open correctness gate (the dirty-set algorithm was only ever
// proven on synthetic fixtures). This is the harness the M2b recompute will plug into: once it
// reports zero non-dirty mismatches we can trust the dirty set, then build the recompute and
// re-use the same diff to require the recomputed defs byte-match the rebuild too.
//
// Timing: the prior Unified.xml is deleted in the LoadModXML postfix (cache miss), so we copy
// it aside in a LoadModXML PREFIX. The new Unified.xml is written by CachedDefHelper.Save() in
// the ParseAndProcessXML postfix; Run() is called explicitly right after that Save so ordering
// is deterministic rather than relying on Harmony postfix priority. Gated behind
// GagarinPrefs.DirtySetGate; consumes DirtySetDiagnostic.LastDirtySet. Changes no cache
// behaviour.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Xml;
using MissileGirl;
using Verse;

namespace Gagarin
{
    [GagarinPatch(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML))]
    public static class DirtySetGate
    {
        private const string SnapshotFileName = "PriorUnified.snapshot.xml";
        private const string ReportFileName = "GateReport.json";

        private static string SnapshotPath =>
            Path.Combine(GagarinEnvironmentInfo.CacheFolderPath, SnapshotFileName);

        // Copy the prior Unified.xml aside before the load deletes it (cache miss) or overwrites
        // it. Runs as a LoadModXML prefix, so it is guaranteed ahead of the deleting postfix.
        public static void Prefix()
        {
            if (!GagarinPrefs.DirtySetGate)
                return;
            try
            {
                string prior = GagarinEnvironmentInfo.UnifiedXmlFilePath;
                if (File.Exists(prior))
                    File.Copy(prior, SnapshotPath, overwrite: true);
                else if (File.Exists(SnapshotPath))
                    File.Delete(SnapshotPath); // no prior cache this run; clear a stale snapshot
            }
            catch (Exception e)
            {
                Logger.Debug("GAGARIN: dirty-set gate snapshot failed", exception: e);
            }
        }

        // Called from the ParseAndProcessXML postfix, immediately after CachedDefHelper.Save()
        // has written the rebuilt Unified.xml. Compares the prior snapshot to the rebuild and
        // reports the non-dirty mismatches (must be zero for a superset-safe dirty set).
        public static void Run()
        {
            if (!GagarinPrefs.DirtySetGate)
                return;

            string snapshot = SnapshotPath;
            string rebuilt = GagarinEnvironmentInfo.UnifiedXmlFilePath;
            if (!File.Exists(snapshot) || !File.Exists(rebuilt))
                return; // no prior cache to gate against (e.g. first cold load)

            HashSet<string> dirty = DirtySetDiagnostic.LastDirtySet;
            if (dirty == null)
            {
                Log.Warning("GAGARIN: <color=yellow>Dirty-set gate</color> skipped — no dirty set " +
                    "(enable DirtySetDiagnostic alongside DirtySetGate).");
                return;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                Dictionary<string, string> baseline = UnifiedCacheDiff.IndexById(LoadCache(snapshot));
                Dictionary<string, string> rebuild = UnifiedCacheDiff.IndexById(LoadCache(rebuilt));
                List<string> mismatches = UnifiedCacheDiff.NonDirtyMismatches(baseline, rebuild, dirty);
                sw.Stop();

                Emit(baseline.Count, rebuild.Count, dirty.Count, mismatches, sw.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                Logger.Debug("GAGARIN: dirty-set gate failed", exception: e);
            }
            finally
            {
                try { if (File.Exists(snapshot)) File.Delete(snapshot); } catch { /* best effort */ }
            }
        }

        // Loads a DefXmlStorage cache the same way CachedDefHelper does (whitespace-insensitive,
        // characters unchecked) so identical content yields identical OuterXml on both sides.
        private static XmlDocument LoadCache(string path)
        {
            var settings = new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreWhitespace = true,
                CheckCharacters = false
            };
            var doc = new XmlDocument();
            using StringReader input = new StringReader(File.ReadAllText(path));
            using XmlReader reader = XmlReader.Create(input, settings);
            doc.Load(reader);
            return doc;
        }

        private static void Emit(int baselineDefs, int rebuildDefs, int dirtyCount,
            List<string> mismatches, long gateMs)
        {
            string verdict = mismatches.Count == 0 ? "<color=green>PASS</color>" : "<color=red>FAIL</color>";
            Log.Warning($"GAGARIN: <color=white>Dirty-set gate</color> {verdict} " +
                $"nonDirtyMismatches={mismatches.Count} " +
                $"(baselineDefs={baselineDefs} rebuildDefs={rebuildDefs} dirty={dirtyCount}) gateMs={gateMs}");
            if (mismatches.Count > 0)
                Log.Warning("GAGARIN: gate mismatches (dirty set missed these): " +
                    string.Join(", ", mismatches.GetRange(0, Math.Min(20, mismatches.Count))));

            try
            {
                var sb = new StringBuilder();
                sb.Append('{');
                sb.Append($"\"pass\":{(mismatches.Count == 0 ? "true" : "false")},");
                sb.Append($"\"nonDirtyMismatches\":{mismatches.Count},");
                sb.Append($"\"baselineDefs\":{baselineDefs},");
                sb.Append($"\"rebuildDefs\":{rebuildDefs},");
                sb.Append($"\"dirtyCount\":{dirtyCount},");
                sb.Append($"\"gateMs\":{gateMs},");
                sb.Append("\"mismatchIds\":[");
                for (int i = 0; i < mismatches.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendQuoted(sb, mismatches[i]);
                }
                sb.Append("]}");
                File.WriteAllText(Path.Combine(GagarinEnvironmentInfo.CacheFolderPath, ReportFileName),
                    sb.ToString());
            }
            catch (Exception e)
            {
                Logger.Debug("GAGARIN: failed writing GateReport.json", exception: e);
            }
        }

        private static void AppendQuoted(StringBuilder sb, string s)
        {
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
