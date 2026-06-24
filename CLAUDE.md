# MissileGirl (Gagarin fork) — project map

Fork of **Missile Girl / Gagarin** (EPL-2.0), a RimWorld load-time XML-cache mod by
ViralReaction/vr.missilegirl. Upstream serializes the fully-patched def database to a single
`Unified.xml` and replays it on a warm load (all-or-nothing). **Our work** makes that cache
**incremental**: a persisted dependency graph lets a changed load recompute only the affected
defs instead of forcing a full rebuild.

General RimWorld modding info (install paths, the decompiler, the `net481` build-framework
override, `About.xml` load-order rules) lives in the parent `../CLAUDE.md` — don't duplicate it
here. **Deep history / decisions: `DESIGN.md`** (living working notes). This file is just the
structural map so a fresh session can navigate without re-exploring.

## Projects (`Source/*/`)

| Project | Role |
|---|---|
| `Gagarin/Gagarin.csproj` | The mod. Upstream cache + **all our incremental code** (`Core/Incremental/`). Build target. |
| `Gagarin/Tests/Gagarin.Tests.csproj` | **The offline test project** (NUnit, `net8.0`, links the real source). 82+ tests. This is where pure-logic tests go. |
| `IncrementalCache/` | Standalone offline analysis/POC tool. **Does not compile in this env** (mono ref-assembly quirk) — do not rely on it; put new tests in `Gagarin/Tests`. |
| `Cosmodrome/`, `Proton/`, `Soyuz/` | Upstream sub-assemblies (UI, keyed resources, etc.). Rarely touched. |

## Incremental pipeline — `Source/Gagarin/Core/Incremental/`

Two halves: **capture** (cold load writes the graph) and **incremental** (changed load diffs →
dirty set → gate → recompute → splice). See `docs/architecture/incremental-pipeline.svg` (+ the
RimWorld-native `rimworld-pipeline.svg`); `.d2` sources regenerate via `nix-shell -p d2`.

| File | Role |
|---|---|
| `ProvenanceRecorder.cs` | Capture instrumentation: `RegisterNode` (def→node), `RegisterAbstract` (Name bases), `IndexPatches` (stable patch ids), `RecordPatch` (edges), `Save()`→`DependencyGraph.json`. Gated by `CaptureProvenance && !IsUsingCache`. |
| `PatchIdWalker.cs` | Pure: assigns stable hierarchical ids to nested patch ops (`mod#3.operations[2]`). |
| `ProvenanceGraph.cs` | Write side: in-memory graph + JSON serialize + `KeyForNode` (id = `{element.Name}/{defName}`). |
| `DependencyGraphModel.cs` | Read side: parses `DependencyGraph.json` (incl. a `MiniJson` parser — no `System.Text.Json` on `net481`). `GraphNode`/`GraphPatchEdge`/`GraphInheritanceEdge`. |
| `DirtySetComputer.cs` | **Pure** dirty-set algorithm: Seeds (1 changed defs, 2 patch-modified, 3 reorder, 4 wildcard flips, 5 added defs) → inheritance closure. RimWorld-free, offline-tested. |
| `WildcardRematch.cs` | Pure: re-tests changed mods' current xpaths vs current def bodies → newly-matched ids (Seed 4 / M2a). |
| `SubDocExpander.cs` | Pure: dirty set → +sequence-sibling **context**; flags `needsFullRebuild` when a changed mod owns a container op. |
| `DefRecompute.cs` | **RimWorld-coupled**: real `PatchOperation.Apply` + inheritance over a tiny `<Defs>` sub-doc → resolved dirty defs. Validated only in-game via the gate. |
| `UnifiedCacheSplice.cs` | Pure: splice recomputed defs into prior `Unified.xml` (replace / drop / **append new via `newPaths`** / reuse rest verbatim). |
| `UnifiedCacheDiff.cs` | Pure: prior↔new `Unified.xml` diff (the gate's comparator). |
| `DirtySetDiagnostic.cs` | RimWorld-facing driver. `LoadModXML` prefix snapshots prior state; postfix builds `GraphChange` (incl. added-defs detection), runs the computer, publishes `LastDirtySet`/`LastChangedMods`/`LastNewPaths`. |
| `DirtySetGate.cs` | Real-engine gate (after rebuild's `Save`): proves dirty set ⊇ real change (`nonDirtyMismatches`); `RunRecompute` → expand → recompute → splice → diff (`RecomputeReport.json`). |
| `MetricsLog.cs` | Append-only JSONL (`incremental-metrics.jsonl`) surviving cache clears. `load_summary` / `error` / `inconsistency`. Schema v2. |

**Capture hook sites** (`Source/Gagarin/Core/Patches/`): `DirectXmlLoader_Patch.cs`
(`DefFromNodeNew` postfix → `RegisterNode`) and `PatchOperation_Patch.cs` (`Apply` pre/postfix +
`SelectNodes`/`SelectSingleNode` hooks + `XmlInheritance.TryRegister` postfix). Cache lifecycle:
`Core/Context.cs` (`IsUsingCache`), `Core/CachedDefHelper.cs` (`Save`→`Unified.xml`),
`Core/StartupHelper.cs` (prior-state sidecar + master toggle), `Core/Data/GagarinPrefs.cs` (flags).

## Build & test

```bash
# Build the mod (from Source/Gagarin or the solution dir)
FrameworkPathOverride=/usr/lib/mono/4.8-api /home/deck/.dotnet/dotnet build -c Release
# Offline tests (the real test project; net8.0)
FrameworkPathOverride=/usr/lib/mono/4.8-api /home/deck/.dotnet/dotnet test Source/Gagarin/Tests/Gagarin.Tests.csproj
```
Note: building updates the tracked artifact `1.6/Plugins/Stable/Gagarin.dll` — it shows up in diffs.

## Flags (env-overridable via `GagarinPrefs` static ctor; all default OFF)

`GAGARIN_INCREMENTAL_CACHE` (master toggle — must no-op when OFF, restoring upstream's cache
byte-for-byte), `GAGARIN_CAPTURE_PROVENANCE`, `GAGARIN_DIRTYSET_DIAGNOSTIC`, `GAGARIN_DIRTYSET_GATE`,
`GAGARIN_DIRTYSET_RECOMPUTE`, `GAGARIN_METRICS`.

## Runtime artifacts

Cache folder = `RocketEnvironmentInfo.CustomConfigFolderPath` (`.../Config/../MissileGirl/`).
`Cache/` subfolder holds `DependencyGraph.json`, `Unified.xml`, `DirtySet.json`, `GateReport.json`,
`RecomputeReport.json` (wiped on cold rebuild). `incremental-metrics.jsonl` lives in the **parent**
(survives clears). Sidecar snapshots prior `ModList.xml`/`Unified.xml`/`AssetsHash` before teardown.
Run-artifact archive: `../MissileGirl-metrics/` (12 MB; the evidence base — preserve it).

## Live test harness

`TestMods/run_test.sh` (+ `TestMods/README.md`). Deploys the dev DLL over the workshop
`vr.missilegirl` `Gagarin.dll` (backup/restore), runs two cold loads, asserts both gates. Cases via
`--expect-fallback`, `--expect-added`. Known flake: early-startup SIGSEGV ~1/4 launches → it retries.

## Roadmap — incremental correctness on add/remove

The incremental path is **not yet trustworthy on add/remove** (coverage runs: CLEAN=0/15; gate &
recompute still FAIL in many cases). Workstreams:
- **P1** capture **all** def types — **mostly DONE**. Root cause was NOT a capture gap: the defs
  *were* captured, but `RegisterNode` keyed them by `def.GetType().Name` (simple name) while every
  consumer keys by the XML element name, so namespaced custom def elements
  (`<VFEProps.PropDef>`) and `Class`-attributed defs were filed under a key nobody looks up.
  `RegisterNode` now keys by the element name (matching `RegisterAbstract`/`KeyForNode`); this
  cleared 40 of 45 gate misses on the vmemese-removal run. **Residual (separate cause):** 3
  XML-authored `ThingStyleDef` + 2 `FactionDef` whose element name already matched their type
  name — these are reference-propagation / recompute-fidelity misses, not keying, tracked under
  "Recompute fidelity" below.
- **P2** added-defs channel (Seed 5) — **DONE** (PR #13, `feat/added-defs-channel`).
- **P4** MayRequire / `MayRequireAnyOf` / conditional patch flips in *unchanged* mods — OPEN.
- Recompute fidelity / broaden the safe full-rebuild fallback — OPEN.

## Gotchas (verified)

- **Node-id scheme** (FIXED): `RegisterNode` used to key by `def.GetType().Name` while everything
  else (`KeyForNode`, `RegisterAbstract`, `DefRecompute`, splice, gate) keys by the **XML element
  name**. They agreed for normal defs but diverged for `Class`-attributed and fully-namespaced
  custom def elements, filing those nodes under an unmatchable key. `RegisterNode` now keys by the
  element name, so all paths agree (see P1 in the roadmap).
- **Graph self-heals on a changed load**: a mod-list change sets `IsUsingCache=false` → full rebuild
  → capture runs → `DependencyGraph.json` is rewritten with the new defs. The diagnostic reads the
  **prior** graph/state via the sidecar before that teardown — so the dirty set only needs to cover
  new defs for the current load.
- **Squash-merge hazard**: `main` carries squashed history, so branches off old feature branches
  conflict. Base new work on `origin/main`; cherry-pick only the new delta.
