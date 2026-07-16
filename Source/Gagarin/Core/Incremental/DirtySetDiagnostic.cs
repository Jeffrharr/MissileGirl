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

        // The dirty set computed on this load, published for the M2b real-engine gate
        // (DirtySetGate runs later, after the rebuild writes the new Unified.xml). Null until
        // computed; reset each load so a stale set can't leak into a later gate.
        public static HashSet<string> LastDirtySet;

        // ADDED-def id -> owning source file, published for the M2b recompute splice (P2). When a
        // recomputed id is absent from the baseline cache, UnifiedCacheSplice appends it as a new
        // <Item path=...> and reads the path from this map; without it the added item's path would
        // be empty and would not byte-match a full rebuild. Only carries ids that genuinely got
        // ADDED this load (the same set as change.AddedNodeIds). Null until computed; reset each
        // load so a stale map can't leak into a later gate.
        public static Dictionary<string, string> LastNewPaths;

        // The packageIds of mods whose patch files changed this load, published alongside the
        // dirty set for the M2b-2b sub-doc gate. SubDocExpander uses it to fall back to a full
        // rebuild when a CHANGED mod owns a container op (its baseline execution path is stale).
        // Null until computed; reset each load so a stale set can't leak into a later gate.
        public static ICollection<string> LastChangedMods;

        // Mods newly present this load (in CurrentLoadOrder, absent from PriorLoadOrder) — the
        // PRIOR dependency graph has literally never captured them, so it carries zero PatchEdges
        // evidence for whether their ops are container ops, leaf ops, or anything else. Published
        // for SubDocExpander: without this, a brand-new mod that owns e.g. a
        // PatchOperationSequence is invisible to the "changed mod owns a container op" fallback
        // (it just never appears as a PatchEdge.SourceMod in the prior graph), so the incremental
        // path proceeds and silently produces wrong content (run 20260714-160405:
        // regrowth.botr.core's Plant_* retexture sequence). Null until computed; reset each load
        // so a stale set can't leak into a later gate.
        public static ICollection<string> LastNewMods;

        // The node ids whose own raw source FILE changed this load (Seed 1) — i.e. a non-patch change
        // channel. Published for PatchCoverageAudit (the invisible-op detector): a def that changed
        // between cache and rebuild is "explained" if it or an inheritance ancestor had its raw body
        // change, so the audit must exclude this channel before flagging a change as unattributed.
        // Null until computed; reset each load.
        public static ICollection<string> LastChangedNodeIds;

        // The diagnostic's per-load results, published for the metrics load_summary. The gate
        // (when enabled) emits one combined summary INCLUDING its own verdicts; when the gate is
        // off the diagnostic emits a summary with the gate/recompute fields null. Valid only when
        // LastDiagnosticValid is true (reset each load, set once the diagnostic completes).
        public static bool LastDiagnosticValid;
        public static int LastChangedAssets;
        public static int LastTotalNodes;
        public static long LastComputeMs;
        public static DirtyResult LastResult;

        public static void Prefix()
        {
            LastDirtySet = null; // clear last load's set before anything can read it
            LastNewPaths = null; // and the added-def path map (read by the recompute splice)
            LastChangedMods = null;
            LastNewMods = null;
            LastChangedNodeIds = null;
            LastDiagnosticValid = false; // reset so a stale summary can't leak into a later load
            LastResult = null;
            if (!GagarinPrefs.DirtySetDiagnostic)
                return;
            try
            {
                // Read priors from the sidecar when the master toggle is ON and a sidecar exists
                // (PriorStateSnapshot), else from the live cache files as before. The sidecar is
                // the only source that survives OnInitialization's modlist-change teardown — the
                // live ModList.xml has already been re-dumped to the CURRENT order by then, which
                // is why reading it here yields seedReorder=0 on a reorder/remove.
                string hashPath = PriorStateSnapshot.Available
                    ? PriorStateSnapshot.PriorHashPath : GagarinEnvironmentInfo.HashFilePath;
                string orderPath = PriorStateSnapshot.Available
                    ? PriorStateSnapshot.PriorModListPath : GagarinEnvironmentInfo.ModListFilePath;
                s_priorHashes = File.Exists(hashPath)
                    ? AssetHashingUtility.Load(hashPath)
                    : null;
                s_priorOrder = File.Exists(orderPath)
                    ? RunningModsSetUtility.Load(orderPath)
                    : null;
            }
            catch (Exception e)
            {
                s_priorHashes = null;
                s_priorOrder = null;
                Logger.Debug("GAGARIN: dirty-set diagnostic prefix failed", exception: e);
                if (GagarinPrefs.Metrics)
                    MetricsLog.Append(MetricsLog.BuildError(
                        CurrentEnvelope(), "diagnostic.prefix", e.GetType().Name, e.Message));
            }
        }

        // The shared metrics envelope for this load. cold == full rebuild (!Context.IsUsingCache):
        // a cache hit replays the prior Unified.xml, while a cache miss / mod-list change does the
        // full rebuild these diagnostics run alongside. Cheap to rebuild per record; the modlist
        // hash makes records from different modlists distinguishable across a week of play.
        private static MetricsLog.Envelope CurrentEnvelope()
        {
            return MetricsLog.NewEnvelope(
                cold: !Context.IsUsingCache,
                packageIds: LoadedModManager.RunningMods.Select(m => m.PackageId));
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

            // Prior dependency graph: from the sidecar when the toggle is ON (so we read the
            // PRIOR run's graph, not the one ProvenanceRecorder.Save() overwrites mid-load),
            // else the live cache copy as before.
            string graphPath = PriorStateSnapshot.Available
                ? PriorStateSnapshot.PriorGraphPath
                : Path.Combine(GagarinEnvironmentInfo.CacheFolderPath, GraphFileName);
            if (s_priorHashes == null || s_priorOrder == null || !File.Exists(graphPath))
                return; // no prior build to diff against

            try
            {
                var sw = Stopwatch.StartNew();
                DependencyGraphData graph = DependencyGraphData.Load(graphPath);

                HashSet<string> changedAssets = ChangedAssets(s_priorHashes);
                // BuildChange also detects the ADDED defs (P2): concrete defs present this load
                // but absent from the baseline graph, restricted to ids whose owning file changed.
                // It returns the added id -> file map so the recompute splice can give each new
                // <Item> its source path; we publish it for DirtySetGate to thread as newPaths.
                GraphChange change = BuildChange(graph, changedAssets, __result,
                    out Dictionary<string, string> newPaths);
                LastChangedMods = change.ChangedMods; // publish for the M2b-2b sub-doc gate
                LastNewMods = NewlyPresentMods(change); // publish for the new-mod fallback (SubDocExpander)
                LastChangedNodeIds = change.ChangedNodeIds; // publish for the invisible-op audit (Seed 1 channel)
                LastNewPaths = newPaths;              // publish for the M2b recompute splice (P2)

                // M2a — superset-safe wildcard re-test. A changed mod's patch predicate can
                // newly match otherwise-unchanged defs, which none of the structural seeds
                // reach. Re-evaluate changed mods' patch xpaths against the current raw def
                // bodies and seed any newly-matched def, then let the closure propagate them.
                HashSet<string> wildcardFlips = ComputeWildcardFlips(graph, change.ChangedMods, __result);

                // issue #43 add-direction: a mod added for the first time this load can
                // override an existing def; the baseline graph never saw it, so
                // DirtySetComputer's Seed 7 has nothing to XOR against. Re-test each newly
                // added mod's current def ids against the baseline's recorded owners.
                HashSet<string> defOverrideFlips = ComputeDefOverrideFlips(graph, change, __result);

                // issue #61 add-direction (patch-injected new defs, e.g. Big and Small's
                // PatchOp_AddBionics wrapping a PatchOperationAdd targeting xpath="Defs"): there
                // is no sound, cheap way to predict an arbitrary custom op's output at THIS point
                // (before the real rebuild runs) without actually replaying changed/added mods'
                // full patch lists against a scratch copy of the whole current def universe --
                // tried that; a real-world mod's custom op made it hang the load (see git
                // history for the reverted ComputeAddedContentFlips). Reconciled instead at GATE
                // time (DirtySetGate.Run), where the REAL rebuild has already executed these
                // patches for real and ProvenanceRecorder has already captured their output in
                // patchInjectedOwners -- no speculative re-execution needed.

                DirtyResult result = DirtySetComputer.Compute(graph, change, wildcardFlips, defOverrideFlips);
                LastDirtySet = result.Nodes; // publish for the M2b gate
                sw.Stop();

                Emit(graph, change, result, changedAssets.Count, sw.ElapsedMilliseconds);

                // Publish results for the metrics load_summary. When the gate is enabled it emits
                // one combined summary (with its verdicts); when it is off we emit here so a
                // diagnostic-only run still records a per-load row (gate/recompute fields null).
                LastChangedAssets = changedAssets.Count;
                LastTotalNodes = graph.Nodes.Count;
                LastComputeMs = sw.ElapsedMilliseconds;
                LastResult = result;
                LastDiagnosticValid = true;
                if (GagarinPrefs.Metrics && !GagarinPrefs.DirtySetGate)
                    MetricsLog.Append(MetricsLog.BuildLoadSummary(
                        CurrentEnvelope(), changedAssets.Count, change.ChangedMods.Count,
                        result.Nodes.Count, graph.Nodes.Count,
                        result.SeedChangedDefs, result.SeedPatchModified, result.SeedReorder,
                        result.SeedWildcardFlip, result.SeedAddedDefs, result.InheritanceAdded,
                        sw.ElapsedMilliseconds,
                        gatePass: null, nonDirtyMismatches: 0, gateMs: 0,
                        recomputePass: null, recomputeFallback: false, recomputeMismatches: 0,
                        subDocSize: 0, recomputeMs: 0));
            }
            catch (Exception e)
            {
                Logger.Debug("GAGARIN: dirty-set diagnostic failed", exception: e);
                if (GagarinPrefs.Metrics)
                    MetricsLog.Append(MetricsLog.BuildError(
                        CurrentEnvelope(), "diagnostic", e.GetType().Name, e.Message));
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

        private static GraphChange BuildChange(DependencyGraphData graph, HashSet<string> changedAssets,
            IEnumerable<LoadableXmlAsset> defAssets, out Dictionary<string, string> newPaths)
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

            // Changed Defs files (issue #50): every changed asset's owning mod, regardless of
            // whether it's a patch file. A mod already present in both prior and current load
            // order can still become a def's new real owner if it edits its own Defs file to
            // newly declare a defName another mod already owns — that's orthogonal to the
            // patch-scoped ChangedMods above, so it gets its own channel.
            foreach (var asset in changedAssets)
            {
                string mod = OwningMod(asset);
                if (mod != null)
                    change.ChangedDefFileMods.Add(mod);
            }

            // ADDED defs (P2). Walk the CURRENT concrete def bodies and admit any id that is BOTH
            // absent from the baseline graph (genuinely new — no baseline node, so the structural
            // seeds above never reach it) AND owned by a file that actually changed this load. The
            // changed-file filter is the precise discriminator: it lets through new-mod defs and
            // new defs in an edited file, while excluding uncaptured-def-TYPE defs (P1) whose files
            // did not change — keeping this orthogonal to P1 and preventing mass over-dirty on an
            // unrelated load. The map's value is the owning file, reused below as the splice's
            // newPaths so each appended <Item> carries its source path and byte-matches a rebuild.
            newPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            var baselineIds = new HashSet<string>(graph.Nodes.Select(n => n.Id));
            foreach (var kv in CurrentConcreteDefIndex(defAssets))
            {
                string id = kv.Key;
                string file = kv.Value;
                if (baselineIds.Contains(id))
                    continue;                       // already a baseline node — not "added"
                if (file == null || !changedAssets.Contains(file))
                    continue;                       // file unchanged => out of scope here (P1)
                if (change.AddedNodeIds.Add(id))
                    newPaths[id] = file;
            }
            return change;
        }

        // Package ids present in CurrentLoadOrder but absent from PriorLoadOrder — mods the
        // prior graph has never seen at all. Shares its "current minus prior" shape with
        // ComputeDefOverrideFlips' candidateMods construction below, but is not scoped down to
        // ChangedDefFileMods/patch-relevance: SubDocExpander needs the raw presence fact, not a
        // content-change signal.
        private static HashSet<string> NewlyPresentMods(GraphChange change)
        {
            var prior = new HashSet<string>(
                change.PriorLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var packageId in change.CurrentLoadOrder ?? new List<string>())
                if (packageId != null && !prior.Contains(packageId))
                    result.Add(packageId);
            return result;
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

        // Newly-detected def-overrides from a mod added for the FIRST time this load (issue
        // #43 add-direction), OR from a mod already present in both loads whose own Defs file
        // changed and, on this load, resolves to a def's new real owner (issue #50 -- a mod
        // that never flips load-list presence, so Seed 7's XOR can't see it). Both cases reduce
        // to "does this mod's current def ownership disagree with baseline for a mod we know
        // changed something this load" -- DefOverrideRematch.NewlyDetectedOverrides has no
        // logic tied to "added" specifically, it just filters by whatever candidate set it's
        // handed, so the two channels share one call.
        private static HashSet<string> ComputeDefOverrideFlips(DependencyGraphData graph,
            GraphChange change, IEnumerable<LoadableXmlAsset> defAssets)
        {
            var prior = new HashSet<string>(
                change.PriorLoadOrder ?? (IEnumerable<string>)System.Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var candidateMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var packageId in change.CurrentLoadOrder ?? new List<string>())
                if (packageId != null && !prior.Contains(packageId))
                    candidateMods.Add(packageId);
            foreach (var packageId in change.ChangedDefFileMods ?? new HashSet<string>())
                if (packageId != null)
                    candidateMods.Add(packageId);

            if (candidateMods.Count == 0)
                return new HashSet<string>();

            var currentIdOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in CurrentConcreteDefIndex(defAssets))
            {
                string mod = OwningMod(kv.Value);
                if (mod != null)
                    currentIdOwner[kv.Key] = mod;
            }

            HashSet<string> flips = DefOverrideRematch.NewlyDetectedOverrides(graph, currentIdOwner, candidateMods);

            Log.Warning($"GAGARIN: <color=white>DefOverride re-test</color> " +
                $"candidateMods={candidateMods.Count} currentIds={currentIdOwner.Count} flips={flips.Count}");
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

        // CONCRETE def id -> owning source file (asset.FullFilePath), over the loaded def assets.
        // Mirrors DefRecompute.BuildRawIndex's id logic exactly so the ids pair to the recompute /
        // splice scheme: a concrete def keys "{element.Name}/{defName}". Abstract bases ("@Name")
        // are skipped — they are never cache items, so they are never "added" cache entries; a
        // dirty concrete descendant pulls its abstract ancestors in via DefRecompute.AddAncestors.
        // The file is the asset's FullFilePath, the same key the asset-hash diff and the graph's
        // SourceFile use, so the added-def filter (file in changedAssets) joins without reconciling.
        // Keyed by id; last write wins for a duplicate id (the same superset-safe behaviour as the
        // raw index — an added id is dirtied at most once regardless).
        private static Dictionary<string, string> CurrentConcreteDefIndex(
            IEnumerable<LoadableXmlAsset> defAssets)
        {
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            if (defAssets == null)
                return index;
            foreach (var asset in defAssets)
            {
                var root = asset?.xmlDoc?.DocumentElement;
                if (root == null || root.Name != "Defs")
                    continue;
                foreach (XmlNode child in root.ChildNodes)
                {
                    if (!(child is XmlElement def))
                        continue;
                    string defName = def["defName"]?.InnerText;
                    if (string.IsNullOrEmpty(defName))
                        continue;                   // abstract (@Name) or malformed — not a cache item
                    index[def.Name + "/" + defName] = asset.FullFilePath;
                }
            }
            return index;
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
                $"seedAdded={result.SeedAddedDefs} seedMayRequire={result.SeedMayRequire} " +
                $"seedDefOverride={result.SeedDefOverride} seedDefOverrideRematch={result.SeedDefOverrideRematch} " +
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
                sb.Append($"\"addedDefs\":{result.SeedAddedDefs},");
                sb.Append($"\"mayRequire\":{result.SeedMayRequire},");
                sb.Append($"\"defOverride\":{result.SeedDefOverride},");
                sb.Append($"\"defOverrideRematch\":{result.SeedDefOverrideRematch},");
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
                if (GagarinPrefs.Metrics)
                    MetricsLog.Append(MetricsLog.BuildError(
                        CurrentEnvelope(), "diagnostic.write", e.GetType().Name, e.Message));
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
