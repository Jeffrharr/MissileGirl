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

**4**

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
| 2026-07-09 | 2 | curated-pool bulk add/remove, seed 4002 rerun post-fix (40-mod base, dependency-closed via fixed `sample_candidates.py`); REMOVE 7 (`telardo.MultiFloors` etc), ADD 7 (`VanillaExpanded.VAERoy` etc) | N (inconclusive) | Run B (mods=57 after closure) got through `Provenance captured` then the process exited ~120s later with **no exception, no crash signature, no OOM in dmesg/coredumpctl** in `Player.log` — just a Unity shutdown memory-leak dump, before the dirty-set gate marker. Per CLAUDE.md, a death *after* a `GAGARIN:` line is not the known pre-capture Boehm-GC flake and should be surfaced rather than dismissed — flagging as an **unresolved anomaly**, not yet root-caused (no evidence pointing at our code specifically). | — | Not investigated further this run (no signal to chase); not counted against the streak. Retry with a fresh candidate; revisit if this pattern recurs. |
