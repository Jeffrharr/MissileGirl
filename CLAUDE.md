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
`vr.missilegirl` `Gagarin.dll` (backup/restore), runs two cold loads (Run A cold-captures the graph;
Run B changes the patch/mod set → cache miss → gates), asserts the gates, archives reports +
**the exact modlist used** to `../MissileGirl-metrics/livetest-{runB-,}<ts>-*`, then restores DLL +
`ModsConfig.xml` + symlinks.

**Always a minimal, self-contained modlist.** Step 1 WRITES a fresh `ModsConfig` (it does NOT inherit
your active mods): Core + installed official DLCs + `brrainz.harmony` + `vr.missilegirl` + the test
mods (~13 total). This is deliberate — a large ambient list (observed: **815 mods**) SIGSEGVs
RimWorld's Boehm GC during unrelated mod init, before any `GAGARIN:` line, so the run crashes with
zero bearing on our code. Minimal = ~1–2 min/load, deterministic, and isolates the mechanism. Your
real `ModsConfig` is backed up and restored on teardown.

**Build the dev DLL first** (the harness deploys `1.6/Plugins/Stable/Gagarin.dll` as-is, so build the
branch under test):
```bash
FrameworkPathOverride=/usr/lib/mono/4.8-api /home/deck/.dotnet/dotnet build Source/Gagarin/Gagarin.csproj -c Release
cd TestMods && bash run_test.sh [flags]
```

**Flags** (`--expect-*` are mutually exclusive):
| Flag | What it exercises | Verdict criteria |
|---|---|---|
| *(none)* | Default: real sub-doc recompute (leaf-op change in the changed mod) | dirty-set gate + `fallback==false && recomputeMismatches==0` |
| `--expect-fallback` | Changed mod owns a container op (`PatchOperationSequence`) → SubDocExpander declines | dirty-set gate + `fallback==true && pass==true` |
| `--expect-added` | P2 added-defs channel: `joof.testharness.added` held out of Run A, inserted before Run B | dirty-set gate + recompute + `seeds.addedDefs > 0` |
| `--expect-mayrequire` | P4 MayRequire flip: `joof.testharness.gate` active for Run A, removed for Run B; gated content in the unchanged `joof.testharness.mayrequire` (root-gated def + patch-injected `<li MayRequire>`) | dirty-set gate + `seeds.mayRequire > 0`. **Recompute is informational** here (DefRecompute doesn't yet evaluate MayRequire over raw bodies → the gated def recomputes as present and mismatches; that's the separate recompute-fidelity workstream, not P4's superset claim) |
| `--expect-p1` | P1 node-id keying: `joof.testharness.p1` (C# assembly defining the namespaced `JoofTest.PropDef`); its def file's `p1Tag` is swapped Run A→B (changed def file, modlist unchanged) | dirty-set gate + recompute gate + the dirty set contains `JoofTest.PropDef/TC_P1_Prop` (element-name keyed, not legacy `PropDef/...`) |
| `--modlist=FILE` | Adds a captured problem set (one packageId per line, `#` comments) on top of the minimal base; hard-capped at 100 mods total | per the `--expect-*` mode chosen |
| `--no-teardown` | Leaves symlinks/ModsConfig/DLL deployed (combine with others for debugging) | — |

Dirty-set gate is `GateReport.json: nonDirtyMismatches==0` (the superset proof, always required).
Recompute gate is `RecomputeReport.json` (required except in `--expect-mayrequire`).

**Caveats / operational notes:**
- **Force-kills any running `RimWorldLinux`** (`pkill -9 -x RimWorldLinux`) on cleanup — close your
  game first; it will drop an active session.
- Known flake: early-startup Boehm-GC SIGSEGV (launched `--no-sandbox`); retries up to 5×. Any death
  **before** the first `GAGARIN:` line is treated as the flake (stderr → file so the `GC_mark_from`
  signature is visible); a death **after** `GAGARIN:` is a real crash and is surfaced.
- **Reproducing a problem set:** every run archives `livetest-<ts>-modlist.xml` (written before
  launch, so it survives a crash). To replay, strip it to ≤100 packageIds and pass via `--modlist=`.
- P1 and P4 both have live fixtures: `--expect-p1` (`TestMod_P1`, a C# assembly with the namespaced
  `JoofTest.PropDef`) and `--expect-mayrequire`. Both pass.

## Roadmap — incremental correctness on add/remove

The incremental path is **not yet trustworthy on add/remove** (coverage runs: CLEAN=0/15; gate &
recompute still FAIL in many cases). Workstreams:
- **P1** capture **all** def types — **DONE, live-validated**. Root cause was NOT a capture gap: the
  defs *were* captured, but `RegisterNode` keyed them by `def.GetType().Name` (simple name) while
  every consumer keys by the XML element name, so namespaced custom def elements
  (`<VFEProps.PropDef>`) and `Class`-attributed defs were filed under a key nobody looks up.
  Live run (`run_test.sh --expect-p1`, 2026-06-24): dirty-set gate PASS, recompute gate PASS, and the
  changed `<JoofTest.PropDef>` was dirtied as `JoofTest.PropDef/TC_P1_Prop` (NOT the legacy
  `PropDef/TC_P1_Prop`). Fixture `TestMod_P1` ships a C# assembly defining the namespaced `Def` subclass.
  `RegisterNode` now keys by the element name (matching `RegisterAbstract`/`KeyForNode`); this
  cleared 40 of 45 gate misses on the vmemese-removal run. **Residual 5 (separate cause, → P4):**
  3 `ThingStyleDef` + 2 `FactionDef` whose element name already matched their type name. These
  turned out to be `MayRequire` flips, not keying — handled by P4 below.
- **P2** added-defs channel (Seed 5) — **DONE** (PR #13, `feat/added-defs-channel`).
- **P4** `MayRequire` / `MayRequireAnyOf` flips in *unchanged* mods — **DONE, live-validated**
  (`feat/p4-mayrequire-flips`). Live run (`run_test.sh --expect-mayrequire`, 2026-06-24):
  dirty-set gate PASS `nonDirtyMismatches=0`, `seedMayRequire=2` (both gated defs dirtied on the
  gate-mod removal). Recompute gate has 1 known mismatch (`TC_MR_Gated`) — the recompute-fidelity
  gap, not P4. Required two harness fixes found along the way: load a minimal modlist (815 ambient
  mods crashed the GC pre-capture) and export `GAGARIN_INCREMENTAL_CACHE=1` (the sidecar is the only
  prior-order source that survives the modlist-change teardown; without it every load-order-diff
  seed silently no-ops). Capture scans the fully-patched doc for `MayRequire`/
  `MayRequireAnyOf` attrs and indexes each gated node under its owning def (`ProvenanceRecorder.
  IndexMayRequire`, called from the `ApplyPatches` postfix) → `mayRequire` map in
  `DependencyGraph.json` (packageId → defNodeIds, case-insensitive). **Seed 6** (`DirtySetComputer`)
  dirties a packageId's gated defs when it is present in exactly one of prior/current load order
  (a true add/remove). Targets all 5 P1 residuals: the `ThingStyleDef` root `MayRequire` and the
  `FactionDef` patch-injected `<li MayRequire>`. Structural parts offline-tested (91 tests); the
  doc-scan capture is live-only — **needs a coverage run to confirm before merge.** Note: the
  *add* direction is only covered when the gated content survives into the patched doc with the
  required mod absent (true for plain `Add`-injected `<li MayRequire>`; a `PatchOperationFindMod`
  that gates the whole op would not be captured with the mod absent).
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
