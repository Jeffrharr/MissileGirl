# Live test harness — incremental-cache dirty-set + recompute gates

`run_test.sh` drives the **real RimWorld engine** twice to prove the incremental-cache
pipeline (Piece D: M1 dirty-set, M2a wildcard re-test, M2b-2b sub-doc recompute) end-to-end,
not just on offline fixtures. It is the in-game counterpart to the offline NUnit suite
(`Source/Gagarin/Tests`).

It proves two gates on a single changed load:

1. **Dirty-set gate** — every def NOT in the dirty set is byte-identical between the prior
   cache and a full rebuild (`GateReport.json`, `nonDirtyMismatches == 0`). I.e. the dirty set
   is a true superset; nothing changed silently outside it.
2. **Recompute gate** — recomputing the dirty defs over the **dirty + context** sub-doc
   (sibling expansion) and splicing onto the prior cache byte-matches a full rebuild over
   **every** def (`RecomputeReport.json`, `pass && recomputeMismatches == 0 && !fallback`).

## TL;DR

```bash
# 1. Build the mod (the runner deploys this exact DLL):
cd /home/deck/Developer/RimWorldMods/MissileGirl
FrameworkPathOverride=/usr/lib/mono/4.8-api \
  /home/deck/.dotnet/dotnet build Source/Gagarin/Gagarin.csproj -c Release

# 2. Run the harness (~8-15 min; launches RimWorld twice):
bash TestMods/run_test.sh
```

A green `LIVE TEST HARNESS: PASS` banner means both gates passed. Artifacts are archived to
`../MissileGirl-metrics/livetest-runB-<timestamp>-*.json`.

## What it actually does

| Step | Action |
|---|---|
| 0  | Symlinks the three test mods into `RimWorld/Mods/` (`joof-testharness-{defs,static,change}`). |
| 0b | Backs up the **workshop** `Gagarin.dll` and deploys the freshly-built dev DLL over it. |
| 1  | Adds the three test packageIds to `ModsConfig.xml` (after `vr.missilegirl`); backs it up. |
| 2  | Clears the MissileGirl cache (forces a cold rebuild) and sets `Change.xml` = the Run A file (`Change_RunA.xml`, or `Change_RunA_Fallback.xml` with `--expect-fallback`). |
| 3  | **Run A** (cold): launches RimWorld, waits for `Provenance captured` → writes `DependencyGraph.json`. |
| 4  | Sets `Change.xml` = the Run B file (`Change_RunB.xml`, or `Change_RunB_Fallback.xml`) — the change that triggers the cache miss. Does **not** clear cache. |
| 5  | **Run B**: launches RimWorld, waits for the `Recompute gate` log line (both gates have run by then). |
| 6  | Parses `GateReport.json` and `RecomputeReport.json`; prints the verdict. Both must pass. |
| 7  | Archives the reports to `../MissileGirl-metrics/`. |
| exit | Teardown (always, via trap): restores `ModsConfig.xml` + the workshop `Gagarin.dll`, removes the symlinks. `--no-teardown` skips this. |

## Why a workshop deploy?

The game loads `vr.missilegirl` from the **workshop** folder
(`steamapps/workshop/content/294100/3712928623`), *not* from `Mods/` or the dev tree. So the
runner copies the dev build (`MissileGirl/1.6/Plugins/Stable/Gagarin.dll`) over the workshop
copy for the duration of the run and restores the original on teardown. **Build the dev DLL
first** — the runner deploys whatever is on disk there, it does not build for you.

## Flags — enabled at launch via env vars

The four incremental-cache diagnostics are compile-time defaults, all **OFF** in source
(`GagarinPrefs.cs`). The runner enables them at launch by exporting environment variables; a
dev-only static ctor in `GagarinPrefs` reads them on first access. **No bespoke flag-edited
build is needed.**

| Env var | Flag | Effect |
|---|---|---|
| `GAGARIN_CAPTURE_PROVENANCE=1`  | `CaptureProvenance`  | Run A writes `DependencyGraph.json`. |
| `GAGARIN_DIRTYSET_DIAGNOSTIC=1` | `DirtySetDiagnostic` | Run B computes the dirty set → `DirtySet.json`, publishes `LastDirtySet`/`LastChangedMods`. |
| `GAGARIN_DIRTYSET_GATE=1`       | `DirtySetGate`       | Run B runs the dirty-set gate → `GateReport.json`. |
| `GAGARIN_DIRTYSET_RECOMPUTE=1`  | `DirtySetRecompute`  | Run B runs the recompute gate → `RecomputeReport.json`. |

Truthy values: `1` or `true`. Unset = the shipped default (OFF), so production is unaffected.
To enable manually outside the runner: `export GAGARIN_DIRTYSET_RECOMPUTE=1` before launching.

## The test mods (the change matrix)

`TestMod_Defs` provides eight `ThingDef`s. `TestMod_Static` is an **unchanged** mod that owns
the container ops. `TestMod_Change` is the **change vehicle** (its `Change.xml` swaps between
runs). In the **default** run it deliberately owns **only leaf ops** — so the recompute gate's
changed-mod fallback does *not* fire and the real sub-doc recompute is exercised. The
**`--expect-fallback`** run instead swaps in `Change_Run{A,B}_Fallback.xml`, which give the
change vehicle its own `PatchOperationSequence` so the fallback *does* fire (see
"The fallback case" below).

| Case | What it exercises | Mechanism proven |
|---|---|---|
| 1 | `TC_Identity`: Add→Replace on an exact defName xpath | plain patch-file edit → `seedDefs` |
| 2 | `TC_Wildcard_A` narrow → `@ParentName="TC_WildcardBase"` wide | M2a wildcard flip seeds `TC_Wildcard_B`/`_C` |
| 3 | `TC_SeqTarget` dirtied; `TestMod_Static`'s **sequence** also touches `TC_SeqSibling` | **sub-doc sibling expansion** — `TC_SeqSibling` must be context so the sequence doesn't abort |
| 4 | `TC_WildcardBase` (abstract) ancestor of the newly-matched concretes | inheritance fan-out through the closure |
| 5 | `TC_Conditional`: `TestMod_Static`'s **conditional** flips nomatch→match when Run B adds `conditionalTrigger` | unchanged conditional re-evaluated over a dirty def |

CASE 3 and CASE 5 are precisely the cases the earlier dirty-**only** sub-doc could not satisfy
(the sequence aborted on the absent `TC_SeqSibling`, giving `recomputeMismatches=12`). The
sibling expansion fixes them.

> To exercise the **fallback** path (changed mod owns a container op → forced full rebuild),
> run with `--expect-fallback` (see below). It swaps in the `*_Fallback` change files, in which
> `joof.testharness.change` owns a `PatchOperationSequence`, and flips the recompute-report
> assertion to require `fallback==true`.

## The fallback case — `--expect-fallback`

```bash
bash TestMods/run_test.sh --expect-fallback
```

This proves the OTHER branch of `SubDocExpander.Expand`: when the **changed** mod itself owns a
container op, the baseline execution path for that op is stale, so the gate declines to recompute
and lets the authoritative full rebuild stand. It is the complement of the default run (which
proves a real sub-doc recompute byte-matches the rebuild).

How it is armed:

| Run | File copied to `Change.xml` | Effect |
|---|---|---|
| A (cold) | `Change_RunA_Fallback.xml` | `joof.testharness.change` owns a `PatchOperationSequence` (children patch `TC_SeqTarget`, `TC_Identity`). Provenance capture records its children as `joof.testharness.change#0.operations[N]` edges in `DependencyGraph.json`. |
| B | `Change_RunB_Fallback.xml` | Same sequence, but one child's value is edited `run-a`→`run-b`. The patch-file hash changes → `joof.testharness.change` lands in `LastChangedMods`. |

On Run B both fallback conditions then hold: `changedModIds` contains `joof.testharness.change`
**and** the baseline graph has a `…operations[N]` edge owned by it. `SubDocExpander.Expand`
returns `needsFullRebuild=true`, and the recompute gate logs:

```
GAGARIN: Recompute gate FALLBACK — changed mod joof.testharness.change has container op (PatchOperationSequence) at joof.testharness.change#0.operations[0]
```

No real recompute runs (the full rebuild is authoritative), so `RecomputeReport.json` is:

```json
{"pass":true,"fallback":true,
 "fallbackReason":"changed mod joof.testharness.change has container op (PatchOperationSequence) at joof.testharness.change#0.operations[0]",
 "recomputeMismatches":0,"contextCount":0,...}
```

The dirty-set gate (`GateReport.json`) is unaffected — it still asserts `nonDirtyMismatches==0`.

## Pass criteria

Default run (`bash TestMods/run_test.sh`):

- `GateReport.json`:      `pass==true && nonDirtyMismatches==0`
- `RecomputeReport.json`: `pass==true && recomputeMismatches==0 && fallback==false`

Fallback run (`bash TestMods/run_test.sh --expect-fallback`):

- `GateReport.json`:      `pass==true && nonDirtyMismatches==0`
- `RecomputeReport.json`: `pass==true && fallback==true && recomputeMismatches==0`

A representative PASS (test-mod modlist, 2026-06-21):

```json
GateReport:      {"pass":true,"nonDirtyMismatches":0,"dirtyCount":6,"rebuildDefs":27581,...}
RecomputeReport: {"pass":true,"fallback":false,"recomputeMismatches":0,
                  "dirtyCount":6,"contextCount":1,"subDocSize":7,"recomputed":6,...}
```

## Known flake — early-startup SIGSEGV

RimWorld crashes during native init (Prepatcher's prestarter GUI, a Boehm-GC issue) roughly
**1 in 4 launches**, even with `--no-sandbox`. The signature is `Caught fatal signal - signo:11`
in `Player.log` **before** any `GAGARIN:` line (the process died before the mod loaded). The
runner detects this and **retries up to 5 times**. A crash *after* `GAGARIN:` appears is a real
bug and is surfaced as a hard fail, never retried.

(The native `GC_mark_from` backtrace prints on **stderr**, which the runner sends to
`/dev/null`; do not rely on it as a Player.log signature — `Caught fatal signal` is the one
that lands in the log.)

## Paths / timing

- Cache: `~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/MissileGirl/Cache`
- Player log: `~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Player.log`
- Each cold load ≈ 4 min; total ≈ 8–10 min plus any startup-crash retries (~1 min each).
- Live progress: `tail -f` the file you redirect the runner's stdout to (the runner also logs
  every step with a `[run_test]` prefix).
