# IncrementalReplay — offline replay harness (prototype)

Standalone, headless harness that proves Gagarin's `Unified.xml` cache can be rebuilt
**incrementally** (recompute only the defs a single-mod change affects) instead of
all-or-nothing. It is the go/no-go gate for the incremental-cache project; it never
launches RimWorld.

## What it does

1. Builds a small **synthetic fixture** of mods (defs + identity/wildcard patches +
   cross-mod inheritance + a forward-reaching wildcard — the correctness hazards).
2. Derives the Piece A `DependencyGraph` and Piece B `PatchClassification` from that
   fixture and round-trips them through their documented JSON schemas, so the harness
   depends only on the contracts, not on Piece A/B code.
3. Simulates a single-mod change, computes the **dirty set** (changed defs + everything
   reachable via patch edges, wildcard re-tests, and inheritance, to a fixpoint),
   recomputes only those defs, and splices them into the baseline document.
4. **Diffs** the spliced result against a full from-scratch rebuild using `XMLDiffPatch`.
   Zero diff = correct.
5. Reports the dirty-set size and partial-vs-full timing, and runs a **negative control**
   (naive prefix-reuse) that must FAIL on the hazard case — proving the harness actually
   exercises the cross-mod hazard.

## Why a re-implemented apply model (not `PatchOperation.Apply`)

`Verse.PatchOperation.Apply` is callable, but constructing real patch objects needs
`DirectXmlToObject` + `GenTypes.AllTypes` + `ModContentPacks`, i.e. a booted game. The
harness therefore uses a faithful minimal apply+inheritance model (`ApplyModel`,
`LoadedState`) that mirrors RimWorld's order (patch the combined doc in load order, then
resolve `ParentName` child-over-parent with cross-mod, load-order-based parent lookup).
See `ApplyModel.cs` for the decompiled-behaviour notes this is based on.

## Run

```bash
FrameworkPathOverride=/usr/lib/mono/4.8-api \
  /home/deck/.dotnet/dotnet build Source/IncrementalReplay/IncrementalReplay.csproj -c Debug
mono Source/IncrementalReplay/bin/IncrementalReplay.exe
```

Exit code 0 = every correctness case is zero-diff AND the negative control failed as
expected; non-zero = a regression (the printed diffgram localises it). Build artifacts
under `bin/` are git-ignored; nothing is written under `1.6/`.
