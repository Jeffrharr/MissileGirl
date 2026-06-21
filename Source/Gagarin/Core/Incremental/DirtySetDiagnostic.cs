// // Copyright (c) 2026 ViralReaction
// //
// // This program and the accompanying materials are made available under the
// // terms of the Eclipse Public License 2.0 which is available at
// // http://www.eclipse.org/legal/epl-2.0.
// //
// // SPDX-License-Identifier: EPL-2.0

// DirtySetDiagnostic.cs (Piece D — Milestone 1)
//
// Contains: the RimWorld-facing driver for the dirty-set diagnostic. Snapshots the prior
// build's cache state, diffs the per-file asset hashes to learn what changed, runs the pure
// DirtySetComputer, and writes DirtySet.json + a one-line summary.
//
// Used for: measuring, on a real changed load, how many defs WOULD need recomputing — the
// dirty/total ratio that sizes the incremental prize and validates the algorithm on real
// data. Gated behind GagarinPrefs.DirtySetDiagnostic (dev-only, default OFF); it changes no
// cache behaviour and runs alongside Gagarin's normal rebuild.
//
// Timing: the prior ModList.xml is deleted, and AssetsHash.xml overwritten, as the load
// proceeds. So we snapshot the prior hashes + load order in the LoadModXML PREFIX (before
// the body mutates them) and compute the dirty set in the POSTFIX, once current per-asset
// hashes are populated in Context. The graph + hash files use FullFilePath as their key,
// which is exactly the graph node's SourceFile, so the join needs no reconciliation.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using MissileGirl;
using Verse;

namespace Gagarin
{
    [GagarinPatch(typeof(LoadedModManager), nameof(LoadedModManager.LoadModXML))]
    public static class DirtySetDiagnostic
    {
        private const string GraphFileName = "DependencyGraph.json";
        private const string OutputFileName = "DirtySet.json";

        // Snapshotted in the prefix before the load mutates the cache files.
        private static Dictionary<string, string> s_priorHashes;
        private static List<string> s_priorOrder;

        public static void Prefix()
        {
            if (!GagarinPrefs.DirtySetDiagnostic)
                return;
            try
            {
                s_priorHashes = File.Exists(GagarinEnvironmentInfo.HashFilePath)
                    ? AssetHashingUtility.Load(GagarinEnvironmentInfo.HashFilePath)
                    : null;
                s_priorOrder = File.Exists(GagarinEnvironmentInfo.ModListFilePath)
                    ? RunningModsSetUtility.Load(GagarinEnvironmentInfo.ModListFilePath)
                    : null;
            }
            catch (Exception e)
            {
                s_priorHashes = null;
                s_priorOrder = null;
                Logger.Debug("GAGARIN: dirty-set diagnostic prefix failed", exception: e);
            }
        }

        public static void Postfix()
        {
            if (!GagarinPrefs.DirtySetDiagnostic)
                return;

            string graphPath = Path.Combine(GagarinEnvironmentInfo.CacheFolderPath, GraphFileName);
            if (s_priorHashes == null || s_priorOrder == null || !File.Exists(graphPath))
                return; // no prior build to diff against

            try
            {
                var sw = Stopwatch.StartNew();
                DependencyGraphData graph = DependencyGraphData.Load(graphPath);

                HashSet<string> changedAssets = ChangedAssets(s_priorHashes);
                GraphChange change = BuildChange(graph, changedAssets);
                DirtyResult result = DirtySetComputer.Compute(graph, change);
                sw.Stop();

                Emit(graph, change, result, changedAssets.Count, sw.ElapsedMilliseconds);
            }
            catch (Exception e)
            {
                Logger.Debug("GAGARIN: dirty-set diagnostic failed", exception: e);
            }
            finally
            {
                s_priorHashes = null;
                s_priorOrder = null;
            }
        }

        // Asset ids (FullFilePath) whose content hash changed, plus added/removed. Current
        // hashes come from Context: Context.Assets is the set loaded this run, and
        // Context.AssetsHashes holds their freshly-computed hashes.
        private static HashSet<string> ChangedAssets(Dictionary<string, string> prior)
        {
            var current = Context.AssetsHashes;
            var present = Context.Assets;
            var changed = new HashSet<string>();

            foreach (var kv in prior)
            {
                if (!present.Contains(kv.Key))
                    changed.Add(kv.Key); // removed
                else if (!current.TryGetValue(kv.Key, out var cur) || cur != kv.Value)
                    changed.Add(kv.Key); // content changed
            }
            foreach (var id in present)
                if (!prior.ContainsKey(id))
                    changed.Add(id); // added
            return changed;
        }

        private static GraphChange BuildChange(DependencyGraphData graph, HashSet<string> changedAssets)
        {
            var change = new GraphChange
            {
                PriorLoadOrder = s_priorOrder,
                CurrentLoadOrder = LoadedModManager.RunningMods.Select(m => m.PackageId).ToList()
            };

            // Changed def bodies: graph nodes whose source file changed.
            foreach (var n in graph.Nodes)
                if (n.SourceFile != null && changedAssets.Contains(n.SourceFile))
                    change.ChangedNodeIds.Add(n.Id);

            // Changed patches: a changed file under a Patches folder marks its mod's patches
            // suspect (mod-granular — the graph has patch sourceMod but not sourceFile).
            foreach (var asset in changedAssets)
            {
                if (!LooksLikePatchFile(asset))
                    continue;
                string mod = OwningMod(asset);
                if (mod != null)
                    change.ChangedMods.Add(mod);
            }
            return change;
        }

        private static bool LooksLikePatchFile(string path)
        {
            if (path == null) return false;
            string p = path.Replace('\\', '/');
            return p.IndexOf("/Patches/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string OwningMod(string assetPath)
        {
            string p = assetPath.Replace('\\', '/');
            foreach (var m in LoadedModManager.RunningMods)
            {
                string root = m?.RootDir;
                if (string.IsNullOrEmpty(root))
                    continue;
                if (p.StartsWith(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    return m.PackageId;
            }
            return null;
        }

        private static void Emit(DependencyGraphData graph, GraphChange change,
            DirtyResult result, int changedAssets, long computeMs)
        {
            int total = graph.Nodes.Count;
            float pct = total > 0 ? 100f * result.Nodes.Count / total : 0f;

            Log.Warning($"GAGARIN: <color=white>Dirty-set diagnostic</color> " +
                $"changedAssets={changedAssets} changedMods={change.ChangedMods.Count} " +
                $"dirty={result.Nodes.Count}/{total} ({pct:F2}%) " +
                $"[seedDefs={result.SeedChangedDefs} seedPatch={result.SeedPatchModified} " +
                $"seedReorder={result.SeedReorder} inh={result.InheritanceAdded}] computeMs={computeMs}");

            try
            {
                var sb = new StringBuilder();
                sb.Append('{');
                sb.Append($"\"changedAssets\":{changedAssets},");
                sb.Append($"\"changedMods\":{change.ChangedMods.Count},");
                sb.Append($"\"dirtyCount\":{result.Nodes.Count},");
                sb.Append($"\"totalNodes\":{total},");
                sb.Append($"\"ratioPct\":{pct.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)},");
                sb.Append("\"seeds\":{");
                sb.Append($"\"changedDefs\":{result.SeedChangedDefs},");
                sb.Append($"\"patchModified\":{result.SeedPatchModified},");
                sb.Append($"\"reorder\":{result.SeedReorder},");
                sb.Append($"\"inheritanceAdded\":{result.InheritanceAdded}}},");
                sb.Append($"\"computeMs\":{computeMs},");
                sb.Append("\"dirtyNodeIds\":[");
                bool first = true;
                foreach (var id in result.Nodes)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendQuoted(sb, id);
                }
                sb.Append("]}");

                File.WriteAllText(
                    Path.Combine(GagarinEnvironmentInfo.CacheFolderPath, OutputFileName),
                    sb.ToString());
            }
            catch (Exception e)
            {
                Logger.Debug("GAGARIN: failed writing DirtySet.json", exception: e);
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
