# Edge-cases loop progress

Plan: `/home/deck/.claude/plans/i-d-like-to-design-inherited-wilkes.md`

PR #59 (draft): https://github.com/Jeffrharr/MissileGirl/pull/59 — `run_test.sh` retry-race fix + this loop's infra (`sort_modlist.py`, this file).
PR #65: main-menu marker + `run_test.sh` bad-modlist/gagarin-bug auto-classification.

## Operating rule

Do not end a turn while a concrete next action is known — take it in the same turn. Only stop to
hand control back when genuinely blocked on user input/permission, or when a live `run_test.sh` run
is progressing in the background (use `ScheduleWakeup` to resume once it completes, not silence).
This loop stalls when a session ends with unexplored ideas still on the table; the fix is to keep
going, not to summarize and wait.

Every live run gets one line appended to `EDGE_CASES_LOOP_LOG.jsonl` (see below) *before* the turn
ends — don't let a run's result live only in scrollback.

## Current focus

Phase 2: bulk real add/remove sweep, candidates now sampled from RimSort's curated
`bigmodlist.xml` (799 mods, 753 present in the current 869-mod subscribed pool) via
`scripts/sample_candidates.py`, rather than pure-random sampling of all subscribed mods —
avoids most of the accidental def-collision crashes (e.g. the seed-99 80-mod
`ModsConfig.Reset()`-triggering combo) since that list is already a real, previously-used
working set. `scripts/sort_modlist.py` still handles load-order; RimSort itself has no
headless sort/check CLI (only `build-db`), and its `communityRules.json` `incompatibleWith`
tags are too sparse (2 of 614 rule entries) to use as a filter — `bigmodlist.xml` curation
is doing the real work of avoiding known-bad combos.

Phase 1 (mining the 21 unattributed-def leads from the confounded 101-mod archive) is
parked, not abandoned — resume if Phase 2 stalls or surfaces the same defs.

## Consecutive-clean counter (Phase 2 exit condition: 50)

Mechanically derived from `EDGE_CASES_LOOP_LOG.jsonl` — do not hand-maintain a number here (that's
exactly the bookkeeping that let an unresolved issue go uncounted-but-unflagged before). Run:

```bash
python3 scripts/compute_streak.py
```

Rule: `clean`/`issue_fixed_same_run` rows count up; `issue_open` resets to 0; `bad_modlist`/
`harness_bug`/`inconclusive` rows are skipped (neither counted nor reset) — see the script's
docstring and `scripts/edge_cases_log.schema.json` for the full category contract.

## Open issue — DefRecompute perf blowup (FIXED, live-validated) + new dirty-set gap found by that fix

`DefRecompute.Recompute` was replaying **every** `PatchOperation` from **every** running mod
against the recompute sub-doc unconditionally (no filtering against which nodes the sub-doc
actually contains). On synthetic fixtures (a handful of patches) this was instant; on a real
57-mod list it meant tens of thousands of `patch.Apply(subDoc)` calls, most doomed to fail an
xpath lookup against a doc with only a few nodes — this is what made candidate 4002d's rerun
sit at ~55-60% CPU with zero `GAGARIN:` log progress for 13+ minutes (live process 1389130,
killed manually 2026-07-09). Root-caused via `DirtySetGate.Run()`'s call chain: it runs
synchronously inside `ParseAndProcessXML`'s postfix, before RimWorld can reach the main menu —
"finished loading, at main menu" only meant it eventually returned past the harness's ~120s
wait, not that it was actually hung/deadlocked. **Fixed**: `DefRecompute.Recompute` now takes
the prior `DependencyGraphData` + `changedMods`, and for any **unchanged** mod skips a
top-level patch whose whole subtree never touched a node in the sub-doc's `needed` set (per
the prior graph's patch edges) — changed mods are never filtered (not faithfully represented
by the prior graph, which is why `RecomputeAllowlist` vets them separately). Offline: 184/184
tests pass. Live-validated on the same 4002d modlist (7 remove / 7 add on a 44-mod curated
base): gate now completes in `gateMs=982` instead of stalling — no live PR yet, held pending
the new gap below.

That live rerun surfaced a **separate, genuine** dirty-set gap (not counted toward the
perf-fix validation, not a new streak entry either — the streak stays at 4):
`GateReport.json` shows `nonDirtyMismatches=2` (`GeneDef/RBM_UnguligradeLegs`,
`FurDef/RBM_UnguligradeLegs`), `unattributedChangedCount=0` (so *some* captured cause exists,
just not enough to mark them dirty). Traced via the archived
`livetest-runA/runB-20260709-185942-DependencyGraph.json`: both `RBM_*` node ids exist in the
PRIOR graph with `sourceMod=null, sourceFile=null` (an existing capture-attribution gap — the
def's `LoadableXmlAsset.mod` couldn't be resolved), but are **entirely absent** from the
CURRENT (Run B) graph's node list, despite the defs still existing in both `Unified.xml`
snapshots (that's why they show up as a value mismatch, not an add/remove). All `RBM_`-prefixed
defs vanished from Run B's graph (0 nodes, vs 2 in Run A).

Ruled out the duplicate-defName/`defOverrides` (issue #43) theory: `RBM_UnguligradeLegs` is
defined by exactly one mod in the whole workshop pool that's actually loaded in this run —
`V.Rooboid.Faun` (`Defs/GeneDefs/RBSF_GeneDefs.xml`, `Defs/Misc/FurDefs.xml`), which IS in Run
B's `--remove=` list. Two other mods define a def with the same name
(`tug.Satyr`/`RBSF_GeneDefs.xml`, `tug.Minotaur`/`RBM_GeneDefs_Minotaur.xml` +
`FurDefs.xml`) but grepping `/tmp/cand_4002d_sorted.txt` (the actual 44-mod candidate list)
confirms **neither is present** — so no second owner mod exists to explain "last-write-wins"
survival. `Wara.toomanymods` (a compat patch, not a definer) is also absent from the candidate
list. This is therefore not a collision between two defining mods — it's the **sole owner mod
being removed entirely** and its defs still surviving the splice.

Root cause, part 1 (FIXED, offline-tested, but insufficient alone — see below):
`UnifiedCacheSplice` reuses any node NOT in the dirty set verbatim from the prior
`Unified.xml`. `DirtySetComputer` had Seed 5 for **added** defs but no symmetric seed for
**removed** defs whose sole owning mod left `CurrentLoadOrder`; Seed 7 (`DefOverrides`,
issue #43) only fires when the baseline recorded a *second* registrant for the same defName,
so a single-owner def was unreachable. Added **Seed 8** (`DirtySetComputer.cs`): dirties every
node whose `GraphNode.SourceMod` is present in `PriorLoadOrder` XOR `CurrentLoadOrder`.
Offline-tested (3 new cases: sole-owner removed, mod present in both/reordered = no flip, mod
added = no flip since Seed 5 already owns that direction). Also hardened
`ProvenanceGraph.AddNode` so a second `RegisterNode` call with `sourceMod == null` can never
clobber a previously-recorded valid attribution (defensive; didn't end up being the actual
cause here, but is a real latent bug independently offline-tested).

Root cause, part 2 (STILL OPEN — this is why the gate still fails): live-reran twice after
Seed 8 landed (`livetest-run{A,B}-20260709-191940-*` and `-193005-*`, both rebuilding a FRESH
`DependencyGraph.json` with the fixed DLL) and in BOTH, `GeneDef/RBM_UnguligradeLegs` and
`FurDef/RBM_UnguligradeLegs` still capture as `sourceMod=null, sourceFile=null` in Run A's
graph — proving my "second call clobbers the first" theory was **wrong**. There is no
clobber: `RegisterNode` apparently receives `asset` (or `asset.mod`) as null on the *sole*
registration for these two nodes specifically, while every other defName in the exact same
raw file (`RBSF_GeneDefs.xml`, e.g. `RBSF_DeerEars`, `RBSF_Mood_Reclusive`) captures correctly
as `v.rooboid.faun`. Seed 8 is therefore structurally correct but starved — it can't dirty a
node whose `SourceMod` was never attributed in the first place. New lead, not yet chased:
`V.Rooboid.Faun/Patches/RBSF_Patches.xml` *also* references `RBM_UnguligradeLegs` (a
`PatchOperation`, not a second raw def) — worth checking whether that patch causes
`DirectXmlToObjectNew.DefFromNodeNew` to run a second time over a merged/synthetic node with
no `LoadableXmlAsset`, or whether inheritance/cross-ref resolution reprocesses this specific
node through a code path RegisterNode's postfix doesn't see an asset for. GateReport still
shows the identical `mismatchIds:["GeneDef/RBM_UnguligradeLegs","FurDef/RBM_UnguligradeLegs"]`
on both post-Seed-8 runs (`nonDirtyMismatches:2`), so this is confirmed the SAME gap, not a
new one. **Not yet root-caused to a fix.** Note: one live attempt in between
(`run_rbm_fix2.log`) hit an unrelated late-stage Boehm-GC SIGSEGV during an unrelated mod's
Harmony init (well after the first `GAGARIN:` line but before any DirtySetGate/incremental
code ran) — treated as harness/environment flake per the known-flake note, not counted, simply
retried.
Archived reports: `../MissileGirl-metrics/livetest-run{A,B}-20260709-{185942,191940,193005}-*`.

**RESOLVED, live-validated (2026-07-10).** The null-attribution root cause was
`RBSF_Patches.xml`'s `PatchOperationAdd` splicing `RBM_UnguligradeLegs` in fresh —
`LoadedModManager.ParseAndProcessXML`'s `assetlookup` is keyed by original-file top-level node
identity, so a node a patch creates was never one of those imports and `loadingAsset` (hence
`asset`/`asset.mod`) comes back null at `RegisterNode`. Added a dedicated capture-time index,
`patchInjectedOwners` (nodeId -> sourceMod), populated by a new `ProvenanceRecorder.
RecordAddedChildren` hook wired into `PatchOperation_Patch.cs`'s `Apply_Patch.Postfix` for
`PatchOperationAdd`; `DirtySetComputer`'s Seed 8 falls back to it only when a node's own
`SourceMod` is empty.

First implementation had a real bug, caught by re-running the exact repro rather than trusting
offline tests: it walked ALL of a match target's children, not just the ones the op itself
appended. Harmless for a narrow target, but `TestMod_GenOp`'s own `Patches/GenPatch.xml`
deliberately includes a `PatchOperationAdd` anchored on the `Defs` DOCUMENT ROOT (its
doc-path-fallback fixture) — walking "all children of the target" there meant "all ~15,900
defs in the whole database", so `patchInjectedOwners` pointed literally every node at
`joof.testharness.genop`, clobbering the real fallback for any node (like the two `RBM_*`
ones) whose own `SourceMod` was empty. Fixed by snapshotting each target's `ChildNodes.Count`
at SELECTION time (a new `rawSinkChildCounts` stack, parallel to the existing raw-node stack)
and only attributing children appended after that count.

Live-validated: `run_test.sh --modlist=/tmp/cand_4002d_sorted.txt --remove=V.Rooboid.Faun` now
gives `GateReport.json: pass=true, nonDirtyMismatches=0` (was 2) and
`RecomputeReport.json: recomputeMismatches=0`.

**Second bug found while re-validating the perf fix wasn't a regression**: default-mode
`run_test.sh` (no flags) started failing recompute (`recomputeMismatches=2`, `TC_SeqTarget`/
`TC_Conditional` missing content an unchanged mod's nested `PatchOperationSequence`/
`PatchOperationConditional` child adds). Bisected via `git stash` (isolated to *before* this
session's `patchInjectedOwners` work, i.e. present in the already-uncommitted `DefRecompute`
perf fix) — a genuine pre-existing bug, not a regression from the `patchInjectedOwners` work.
Root cause: `BuildTopLevelIdsToRun`'s `id.IndexOf('.')` to strip a nested-op suffix
(`"mod#3.operations[2]"` -> `"mod#3"`) breaks when the packageId itself contains a `.`
(routine — reverse-DNS style ids like `joof.testharness.static`), truncating to `"joof"`
instead of `"joof.testharness.static#0"`; the real top-level id then never lands in
`topLevelIdsToRun` and its patch op gets skipped by the unchanged-mod filter. Fixed by finding
the suffix's `.` only after the `#` that separates sourceMod from index. Live-validated:
default-mode `run_test.sh` now passes (`recomputeMismatches=0`); `--expect-mayrequire` and
`--expect-nested-in-sequence` re-run clean too (no regression from the `IndexOf` fix).

All four fixes (perf fix, `AddNode` null-clobber guard, Seed 8, `patchInjectedOwners`, plus
the two bugs found validating: over-broad target walk, `IndexOf('.')` packageId-dot bug) are
now live-confirmed together. 191/191 offline tests pass. **Ready to open PRs** (likely 2-3,
split by root cause per the original plan) and resume the Phase 2 sweep.

## Known issues (not counted against the streak — avoid these modlists, don't reset for them)

- **`ModsConfig.Reset()` wipes the active modlist to vanilla+DLC** (confirmed via decompile:
  `Verse.ModsConfig.Reset()` clears `activeMods` to Core+official-DLCs-only and `Save()`s to
  disk). It's RimWorld's own generic fatal-data-load-error recovery, and `ModsConfig_Patch.cs`
  doesn't snapshot/restore the intended modlist around it — worse, `Prefix()`'s
  `return !RocketEnvironmentInfo.IsDevEnv` means the real `Reset()` runs through unmodified in
  a **non-dev environment too**, so this can silently wipe a real player's modlist, not just
  our test harness. Root-caused live via seed-101 (25-mod real subset,
  `/tmp/cand_101_sorted.txt`, archived modlist at
  `../MissileGirl-metrics/livetest-20260709-175947-modlist.xml` confirms the full list *was*
  written correctly pre-launch) — RimWorld loaded with zero non-DLC mods (confirmed by user
  watching the launch), matching `Reset()`'s effect. **Not yet fixed** — needs its own
  dedicated fix + PR (out of scope for the counter loop itself). Any candidate modlist that
  trips this should just be swapped for a different one; it doesn't count as a loop "issue
  found" against the streak, and doesn't reset the counter (per-modlist bad luck, not a
  regression we're chasing).

- **`sort_modlist.py` mis-parsed structured `modDependencies`/`loadAfter`/`loadBefore` `<li>` entries**
  (fixed 2026-07-09, `scripts/sort_modlist.py` `_text_list()`): when a `<li>` has child elements
  (`<name>`/`<packageId>`/`<steamWorkshopUrl>`, common for hard dependency declarations) rather than
  direct text, `li.text` for pretty-printed XML is whitespace (truthy), so the old `if li.text:`
  branch fired and appended `li.text.strip()` — an **empty string** — instead of falling through to
  read `<packageId>`. Concretely: `OskarPotocki.VFE.Tribals`'s `modDependencies` (VFE Core, Harmony,
  Ideology, all declared via structured `<li>`) parsed as `['', '', '']`. Root-caused live: a curated-
  pool candidate (`--modlist=` built without dependency awareness) added an addon patch mod
  (`ryan.voe.progressionminingpatch`) whose base mod dependency wasn't pulled in, producing real
  load errors (`Could not find parent node named "OutpostBase"`, `Could not find type named
  VOE.OutpostExtension_Mining`) — this is what the user was observing as "missing core dependencies,"
  distinct from the separate `ModsConfig.Reset()` full-wipe issue above. Fixed by checking
  `li.text and li.text.strip()` before falling back to `<packageId>`. New `scripts/sample_candidates.py`
  also added: samples bulk add/remove sets from RimSort's curated `bigmodlist.xml` (799 real, already-
  working mods) intersected with the subscribed pool, and closes every base/add list over
  `modDependencies` (pulling in missing hard deps from the full 869-mod subscribed catalog, or
  dropping the requiring mod if the dependency isn't subscribed at all) and refuses to REMOVE a mod
  still depended on by another active mod.

## Log

The per-run table used to live here as hand-maintained markdown; it's been migrated to
`EDGE_CASES_LOOP_LOG.jsonl` (one JSON object per run, schema at
`scripts/edge_cases_log.schema.json`) so the streak in the section above is mechanically derived
instead of hand-counted prose. **Append one line per live run there, not a table row here** — keep
root-cause narrative detail in this file's prose sections / commit messages / PRs as before; the
JSONL only carries the structured fields (`date`, `phase`, `modlist_desc`, `gate_pass`,
`recompute_pass`, `category`, `pr`, `notes`) needed to compute the streak and to answer "was this
counted, and why."
