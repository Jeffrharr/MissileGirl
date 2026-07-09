# Edge-cases loop progress

Plan: `/home/deck/.claude/plans/i-d-like-to-design-inherited-wilkes.md`

## Current focus

Phase 1: mining the 21 unattributed-def leads from the confounded 101-mod archive
(`manualrun-20260626-153744-101mods-*`) — ThingSetMakerDef/Carpet_Mindbend*/etc — via
targeted `run_test.sh --modlist=` reproductions. That archive's own gate FAIL is not
itself a usable signal (baseline was a stale minimal-harness cache, not a clean cold
baseline — confounded by design), but the unattributed-changed list is a real capture gap
regardless of the confound (verified Core/DLC content with no captured op explaining the
diff).

## Consecutive-clean counter (Phase 2 exit condition: 50)

**0** (reset — see issue below)

## Log

| date | phase | modlist/case | issue found (Y/N) | root cause | PR | status |
|---|---|---|---|---|---|---|
| 2026-07-09 | 2 | random 20-mod real subset (seed 42, sorted via `scripts/sort_modlist.py`), default `run_test.sh --modlist=` | N | — | — | PASS: gate nonDirtyMismatches=0, recompute recomputeMismatches=0, dirtyCount=6/13553 |
| 2026-07-09 | 2 | random 40-mod real subset (seed 7, sorted) | N | — | — | PASS: gate nonDirtyMismatches=0, recompute recomputeMismatches=0, dirtyCount=6/14232 |
| 2026-07-09 | 2 (retest, post-fix) | same 80-mod repro after the `run_test.sh` fix | N (harness now correctly reports) | — | — | Gate PASS nonDirtyMismatches=0; recompute gate **fallback=True, recomputeMismatches=0, pass=True** — a legitimate safe-fallback (a changed mod owns a container op, `SubDocExpander` declines per design), not a bug. Overall harness verdict shows FAIL only because default mode's strict criteria requires `fallback=False`; this is really an `--expect-fallback`-shaped case, not a new defect. Not counted against the clean streak. |
| 2026-07-09 | 2 | random 80-mod real subset (seed 99, sorted); repro file `/tmp/candidate_80_sorted.txt` | **Y** | OPEN — not yet root-caused. Two (or more) mods in this sample define duplicate `Name=` abstract defs (`PlatformBase`, `WaterShallow`, `Plant_Bush` — real mod-authoring conflict, not ours). That trips Gagarin's own duplicate-def CRITICAL detector, which logs "Removed cache to recover from error!" and redoes the *entire* load as a fresh cold pass — observed **3 separate** "Cache created!/Provenance captured" cycles within one process launch (Player.log lines ~3597, ~4085, one gate PASS logged mid-sequence at 3674). `run_test.sh`'s `wait_for_marker` does a single substring `grep` for "Recompute gate" and kills RimWorld on first match, so it kills the process **mid-retry** — before the *final* settled pass's `GateReport.json` is written, producing `FAIL: GateReport.json not found`. Two candidate root causes to disentangle: (a) is Gagarin's incremental diagnostic even *correct* to run per-retry-pass instead of only once the reload has fully settled (could itself explain unattributed/gate-mismatch noise on real large modlists that happen to have duplicate-def conflicts, which is common at ~800-mod scale); (b) `run_test.sh`'s marker-wait is racy against multiple emissions in one launch and should wait for the *last* occurrence / a more specific "settled" marker instead of first match. | — | fixed in `TestMods/run_test.sh` (harness-only; not a Gagarin correctness bug — `ModsConfig.Reset()` is vanilla RimWorld's own data-load recovery, patched only to keep the cache folder writable). Root cause (a) ruled out: the diagnostic re-running per retry pass is correct/expected, RimWorld itself redoes the whole load. Root cause (b) confirmed and fixed: `wait_for_marker` matched the *first* "Recompute gate" occurrence even when a later `ModsConfig.Reset()` retry superseded it; now polls for `GateReport.json` up to 300s, and if a retry was detected mid-load, waits for the marker *count* to increase (not just re-match) before re-polling for the report file. Re-running the same 80-mod repro to confirm before deciding whether this needs its own PR (harness-only change, no `Source/Gagarin` code touched — may just land as part of loop infra rather than a separate numbered issue). |
