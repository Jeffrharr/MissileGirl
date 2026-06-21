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
using System.Reflection;
using System.Text;
using System.Xml;
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

        // __result is LoadModXML's return: every def LoadableXmlAsset loaded this run. We take
        // the candidate raw def bodies straight from it rather than Context.XmlAssets, because
        // Context.XmlAssets is filled by a SEPARATE LoadModXML postfix whose order relative to
        // ours is not pinned — reading it here can see an empty dict and silently zero out the
        // wildcard re-test.
        public static void Postfix(IEnumerable<LoadableXmlAsset> __result)
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

                // M2a — superset-safe wildcard re-test. A changed mod's patch predicate can
                // newly match otherwise-unchanged defs, which none of the structural seeds
                // reach. Re-evaluate changed mods' patch xpaths against the current raw def
                // bodies and seed any newly-matched def, then let the closure propagate them.
                HashSet<string> wildcardFlips = ComputeWildcardFlips(graph, change.ChangedMods, __result);

                DirtyResult result = DirtySetComputer.Compute(graph, change, wildcardFlips);
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

        // Newly-matched defs from the M2a wildcard re-test (see WildcardRematch). Re-tests the
        // changed mods' CURRENT patch predicates (so a widened predicate is actually seen)
        // against the raw def bodies in defAssets (LoadModXML's __result) — no disk read needed.
        // Empty when no patch-bearing mod changed.
        private static HashSet<string> ComputeWildcardFlips(DependencyGraphData graph,
            HashSet<string> changedMods, IEnumerable<LoadableXmlAsset> defAssets)
        {
            if (changedMods.Count == 0)
                return new HashSet<string>();
            Dictionary<string, string> currentXpaths = CurrentPredicates(changedMods);
            var defNodes = CurrentDefNodes(defAssets);
            XmlDocument candidateDoc = WildcardRematch.BuildCandidateDocument(defNodes);
            HashSet<string> flips = WildcardRematch.NewlyMatched(graph, currentXpaths, candidateDoc);

            // One-line diagnostic of the re-test inputs, so a zero result is attributable to a
            // specific empty input rather than guessed at.
            Log.Warning($"GAGARIN: <color=white>M2a wildcard re-test</color> " +
                $"changedMods={changedMods.Count} currentPredicates={currentXpaths.Count} " +
                $"defNodes={defNodes.Count} flips={flips.Count}");
            return flips;
        }

        // patchId -> current xpath, for every patch op (including nested container children)
        // declared by a changed mod. Ids use the SAME scheme as the capture
        // ("{sourceMod}#{index}" + hierarchical child labels via ProvenanceRecorder's walker),
        // so they pair to the baseline graph's edges by id. The xpath is reflected straight
        // from the loaded PatchOperation (PatchOperationPathed and subclasses expose an
        // "xpath" field), which gives the CURRENT predicate without running the patch. Ops with
        // no xpath field (containers, custom ops) contribute none — a documented, superset-safe
        // approximation: container/conditional scoping (FindMod, Test success) is not modelled,
        // so a contained op may over-match, never under-match.
        private static Dictionary<string, string> CurrentPredicates(HashSet<string> changedMods)
        {
            var ids = new Dictionary<PatchOperation, string>();
            foreach (ModContentPack mod in LoadedModManager.RunningMods)
            {
                if (mod?.Patches == null || mod.PackageId == null || !changedMods.Contains(mod.PackageId))
                    continue;
                int index = 0;
                foreach (PatchOperation patch in mod.Patches)
                {
                    if (patch != null)
                        PatchIdWalker.AssignIds(patch, $"{mod.PackageId}#{index}",
                            ProvenanceRecorder.GetChildPatches, ids);
                    index++;
                }
            }

            var map = new Dictionary<string, string>();
            foreach (var kv in ids)
            {
                string xpath = XpathOf(kv.Key);
                if (!string.IsNullOrEmpty(xpath))
                    map[kv.Value] = xpath; // first id wins; AssignIds already deduped by op
            }
            return map;
        }

        // Reflects the "xpath" string field declared somewhere in a PatchOperation's type
        // hierarchy (PatchOperationPathed.xpath), cached per concrete type. Null for ops that
        // have no such field.
        private static readonly Dictionary<Type, FieldInfo> s_xpathFields = new Dictionary<Type, FieldInfo>();
        private static string XpathOf(PatchOperation op)
        {
            Type t = op.GetType();
            if (!s_xpathFields.TryGetValue(t, out FieldInfo f))
            {
                for (Type x = t; x != null && x != typeof(object); x = x.BaseType)
                {
                    FieldInfo cand = x.GetField("xpath", BindingFlags.Public | BindingFlags.NonPublic
                        | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                    if (cand != null && cand.FieldType == typeof(string)) { f = cand; break; }
                }
                s_xpathFields[t] = f;
            }
            return f?.GetValue(op) as string;
        }

        // Every top-level def element among the loaded def assets. Def files have a <Defs>
        // root; anything else (a stray <Patch>) is skipped — we want the raw def bodies the
        // patches target.
        private static List<XmlNode> CurrentDefNodes(IEnumerable<LoadableXmlAsset> defAssets)
        {
            var nodes = new List<XmlNode>();
            if (defAssets == null)
                return nodes;
            foreach (var asset in defAssets)
            {
                var root = asset?.xmlDoc?.DocumentElement;
                if (root == null || root.Name != "Defs")
                    continue;
                foreach (XmlNode child in root.ChildNodes)
                    if (child is XmlElement)
                        nodes.Add(child);
            }
            return nodes;
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
                $"seedReorder={result.SeedReorder} seedWildcard={result.SeedWildcardFlip} " +
                $"inh={result.InheritanceAdded}] computeMs={computeMs}");

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
                sb.Append($"\"wildcardFlip\":{result.SeedWildcardFlip},");
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
