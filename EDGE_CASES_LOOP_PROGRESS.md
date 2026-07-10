# Edge-cases loop progress

Plan: `/home/deck/.claude/plans/i-d-like-to-design-inherited-wilkes.md`

PR #59 (draft): https://github.com/Jeffrharr/MissileGirl/pull/59 — `run_test.sh` retry-race fix + this loop's infra (`sort_modlist.py`, this file).

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

**5**

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

| date | phase | modlist/case | issue found (Y/N) | root cause | PR | status |
|---|---|---|---|---|---|---|
| 2026-07-09 | 2 | random 20-mod real subset (seed 42, sorted via `scripts/sort_modlist.py`), default `run_test.sh --modlist=` | N | — | — | PASS: gate nonDirtyMismatches=0, recompute recomputeMismatches=0, dirtyCount=6/13553 |
| 2026-07-09 | 2 | random 40-mod real subset (seed 7, sorted) | N | — | — | PASS: gate nonDirtyMismatches=0, recompute recomputeMismatches=0, dirtyCount=6/14232 |
| 2026-07-09 | 2 (retest, post-fix) | same 80-mod repro after the `run_test.sh` fix | N (harness now correctly reports) | — | — | Gate PASS nonDirtyMismatches=0; recompute gate **fallback=True, recomputeMismatches=0, pass=True** — a legitimate safe-fallback (a changed mod owns a container op, `SubDocExpander` declines per design), not a bug. Overall harness verdict shows FAIL only because default mode's strict criteria requires `fallback=False`; this is really an `--expect-fallback`-shaped case, not a new defect. Not counted against the clean streak. |
| 2026-07-09 | 2 | random 80-mod real subset (seed 99, sorted); repro file `/tmp/candidate_80_sorted.txt` | **Y** | OPEN — not yet root-caused. Two (or more) mods in this sample define duplicate `Name=` abstract defs (`PlatformBase`, `WaterShallow`, `Plant_Bush` — real mod-authoring conflict, not ours). That trips Gagarin's own duplicate-def CRITICAL detector, which logs "Removed cache to recover from error!" and redoes the *entire* load as a fresh cold pass — observed **3 separate** "Cache created!/Provenance captured" cycles within one process launch (Player.log lines ~3597, ~4085, one gate PASS logged mid-sequence at 3674). `run_test.sh`'s `wait_for_marker` does a single substring `grep` for "Recompute gate" and kills RimWorld on first match, so it kills the process **mid-retry** — before the *final* settled pass's `GateReport.json` is written, producing `FAIL: GateReport.json not found`. Two candidate root causes to disentangle: (a) is Gagarin's incremental diagnostic even *correct* to run per-retry-pass instead of only once the reload has fully settled (could itself explain unattributed/gate-mismatch noise on real large modlists that happen to have duplicate-def conflicts, which is common at ~800-mod scale); (b) `run_test.sh`'s marker-wait is racy against multiple emissions in one launch and should wait for the *last* occurrence / a more specific "settled" marker instead of first match. | — | fixed in `TestMods/run_test.sh` (harness-only; not a Gagarin correctness bug — `ModsConfig.Reset()` is vanilla RimWorld's own data-load recovery, patched only to keep the cache folder writable). Root cause (a) ruled out: the diagnostic re-running per retry pass is correct/expected, RimWorld itself redoes the whole load. Root cause (b) confirmed and fixed: `wait_for_marker` matched the *first* "Recompute gate" occurrence even when a later `ModsConfig.Reset()` retry superseded it; now polls for `GateReport.json` up to 300s, and if a retry was detected mid-load, waits for the marker *count* to increase (not just re-match) before re-polling for the report file. Re-running the same 80-mod repro to confirm before deciding whether this needs its own PR (harness-only change, no `Source/Gagarin` code touched — may just land as part of loop infra rather than a separate numbered issue). |
| 2026-07-09 | 2 | bulk real add/remove (30-mod base seed 555, `/tmp/base_555_sorted.txt`; REMOVE 6 mods incl. `DizzyEevee.PocketMapArchitectFix`, `PeteTimesSix.ResearchReinvented`; ADD 6 mods incl. `VPE.Deadlife.Sentinel`, `duz.almosttherefork`) via new `run_test.sh --remove=... --add=...` (mutual-exclusion guard removed — both flags now composable in one Run B) | N | — | — | PASS: dirty-set gate `nonDirtyMismatches=0` (dirtyCount=481/13677); recompute gate `fallback=True, recomputeMismatches=0` — legitimate safe fallback (`vanillaexpanded.vanomalyeinsanity#4` is `PatchOperationInsert`, not a proven-safe leaf op), not a defect. First bulk (multi-mod each direction) add/remove case, per user correction that single-mod toggles weren't representative. |
| 2026-07-09 | 2 | random 35-mod real subset from all 869 subscribed (seed 9001, sorted); REMOVE 6 (`Memegoddess.TDFindLib` etc), ADD 6 (`winggar.meaningfulparties` etc) | (not read back — superseded by curated-pool sampling switch below) | — | — | launched, result not inspected before pivoting to `bigmodlist.xml`-sourced sampling; not counted either way |
| 2026-07-09 | 2 | **first curated-pool** bulk add/remove: 35-mod base sampled from RimSort's `bigmodlist.xml` (seed 4001, `scripts/sample_candidates.py`), REMOVE 6 (`akri.pcannibal`, `vanillaexpanded.vfepower`, `owlchemist.midsaversaver`, `vanillaexpanded.vcookehaute`, `imranfish.xmlextensions`, `salvador143.shuttledock`), ADD 6 (`mlie.justputitoverthere`, `fluffytowels.warcaskethaulpatch`, `mlie.betterrecordstab`, `automatic.prisonerbedsetowner`, `als.anomalygravship`, `fuu.nudistsevasion`) | N | — | — | **Full clean PASS** (not just informational): dirty-set gate `pass=True nonDirtyMismatches=0` (dirtyCount=69/14074); recompute gate `pass=True fallback=False recomputeMismatches=0` (recomputed=15, removed=53, splicedDefs=14074==rebuildDefs). First run to clear both gates non-informationally under bulk add/remove. |
| 2026-07-09 | 2 | curated-pool bulk add/remove, seed 4002 (40-mod base), ADD included `ryan.voe.progressionminingpatch` (VOE Progression addon) without its base-mod dependency | **Y** (tooling bug, not Gagarin) | `scripts/sort_modlist.py`'s `_text_list()` parsed structured `<li><name>/<packageId>` dependency entries as empty strings (see "Known issues" above) — `sample_candidates.py` at the time had no dependency awareness at all, so an addon was added without its required base mod. Real load errors (`Could not find parent node named "OutpostBase"`, missing `VOE.OutpostExtension_Mining` type) — not a Gagarin/gate failure, run never reached the dirty-set gate. | — | Fixed `_text_list()` + added dependency-closure logic to `sample_candidates.py` (see "Known issues"). Not counted against the streak (harness/tooling bug, not an incremental-cache correctness issue) — same treatment as the `ModsConfig.Reset()` finding. |
| 2026-07-10 | 2 (retest, post-fix) | same 4002d 44-mod curated base, `--remove=V.Rooboid.Faun` (the `RBM_UnguligradeLegs` null-attribution repro) | **Y** (2 new bugs found closing the old one) | (1) `RecordAddedChildren`'s first implementation walked ALL children of an Add's match target, not just the ones it appended — over-broad for `TestMod_GenOp`'s deliberate `/Defs`-root-anchored Add fixture, so `patchInjectedOwners` pointed literally every def at `joof.testharness.genop`, clobbering the real fallback for the two `RBM_*` nodes. Fixed via a child-count snapshot taken at selection time (`rawSinkChildCounts`), only attributing newly-appended children. (2) Re-validating the pre-existing `DefRecompute` perf fix wasn't a regression surfaced a genuine pre-existing bug in `BuildTopLevelIdsToRun`: `id.IndexOf('.')` to strip a nested-op suffix breaks when the packageId itself contains a `.` (routine, e.g. `joof.testharness.static`), truncating to just `"joof"` and silently dropping an unchanged mod's relevant top-level op from the recompute replay set. Fixed by searching for the suffix's `.` only after the id's `#`. | — | Both fixed, 191/191 offline tests pass. Live-validated together: `--remove=V.Rooboid.Faun` on 4002d now gives gate `pass=True nonDirtyMismatches=0` + recompute `recomputeMismatches=0`; default-mode `run_test.sh` (which the `IndexOf` bug broke) now passes; `--expect-mayrequire` and `--expect-nested-in-sequence` re-run clean (no regression). Closes the open `RBM_UnguligradeLegs` gap from the two rows above — same case, now fully resolved, counted as this session's clean run. |
| 2026-07-09 | 2 | curated-pool bulk add/remove, seed 4002 rerun post-fix (40-mod base, dependency-closed via fixed `sample_candidates.py`); REMOVE 7 (`telardo.MultiFloors` etc), ADD 7 (`VanillaExpanded.VAERoy` etc) | N (inconclusive) | Run B (mods=57 after closure) got through `Provenance captured` then the process exited ~120s later with **no exception, no crash signature, no OOM in dmesg/coredumpctl** in `Player.log` — just a Unity shutdown memory-leak dump, before the dirty-set gate marker. Per CLAUDE.md, a death *after* a `GAGARIN:` line is not the known pre-capture Boehm-GC flake and should be surfaced rather than dismissed — flagging as an **unresolved anomaly**, not yet root-caused (no evidence pointing at our code specifically). | — | Not investigated further this run (no signal to chase); not counted against the streak. Retry with a fresh candidate; revisit if this pattern recurs. |
