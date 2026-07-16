# Week-long metrics log (real-world evidence base)

`MetricsLog` persists the incremental-cache pipeline's per-load metrics to an **append-only
JSON-Lines** file that **survives cache clears**, so the cache can run during normal play for a
week and accumulate a real failure record without the live harness. **Error records are the
priority signal.**

## What to export for the week

Enable persistence **and** the diagnostic stages that produce the data, then play normally:

```bash
export GAGARIN_METRICS=1            # turn on persistence (governs the LOG, not the stages)
export GAGARIN_DIRTYSET_DIAGNOSTIC=1 # compute the dirty set (seed breakdown, dirty/total)
export GAGARIN_DIRTYSET_GATE=1       # prove the dirty set is a superset (gate verdict)
export GAGARIN_DIRTYSET_RECOMPUTE=1  # prove the recompute byte-matches the rebuild
```

`GAGARIN_METRICS` alone still captures **error** records from any stage that runs, but
`load_summary` / `inconsistency` rows only have data when the stages above are on. All flags
default OFF, so production is unaffected.

## Where the log lives

```
<RimWorld config>/../MissileGirl/incremental-metrics.jsonl
```

i.e. `RocketEnvironmentInfo.CustomConfigFolderPath` joined with `incremental-metrics.jsonl` —
the **parent** of the `Cache` folder. The `Cache` subfolder is deleted recursively on a cold
rebuild (`ModsConfig.Reset`), and its key files are dropped whenever the cache is disabled, so a
log inside `Cache` would lose every prior load's records on the rebuilds we most want to study.
The parent directory is never touched by the cache lifecycle, so cold/warm/mod-list-change
records all accumulate in one file. (On this dev box that resolves to
`~/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/MissileGirl/incremental-metrics.jsonl`.)

## Record shape

One JSON object per line. Common envelope on every record: `schema`, `event`, `ts` (ISO-8601
UTC), `load` (`cold` = full rebuild / `warm` = cache hit), `modCount`, `modListHash` (a
deterministic, order-sensitive FNV-1a hash so a week's records from different modlists stay
distinguishable). Event types:

- `load_summary` — per gated load: `dirtyCount`/`totalNodes`, `seeds{...}`, `gatePass` +
  `nonDirtyMismatches`, `recomputePass` + `recomputeFallback` + `recomputeMismatches`,
  `subDocSize`, and `computeMs`/`gateMs`/`recomputeMs`. Verdict fields are `null` when that
  stage did not run.
- `error` — `stage`, `exceptionType` (runtime type name — the key grouping axis), `message`.
- `inconsistency` — the high-value anomalies: `kind` (`gate` = dirty set missed a real change /
  silent staleness; `recompute` = recomputed defs diverged from the rebuild;
  `recompute-ancestor-divergence` = DefRecompute's current-raw-XML ancestor walk found an id
  CanRecompute's prior-graph ancestor walk never vetted — issue #75, log-only for now),
  `mismatchCount`, `sampleIds`.

## Summarize error records by type

```bash
LOG="$HOME/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/MissileGirl/incremental-metrics.jsonl"
jq -r 'select(.event=="error") | "\(.stage)\t\(.exceptionType)"' "$LOG" | sort | uniq -c | sort -rn
```

(no `jq`? `grep '"event":"error"' "$LOG" | grep -oP '"exceptionType":"\K[^"]+' | sort | uniq -c | sort -rn`)

Count the high-value anomalies the same way:

```bash
jq -r 'select(.event=="inconsistency") | .kind' "$LOG" | sort | uniq -c
```
