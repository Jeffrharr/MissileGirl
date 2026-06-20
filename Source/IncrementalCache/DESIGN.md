# Incremental XML Cache — Patch Classification (Piece B)

Part of an exploratory prototype for making Gagarin's patched-def cache *incremental*
instead of all-or-nothing. Today Gagarin caches RimWorld's fully patched def document
and throws the whole thing away when anything changes. The long-term goal is to recompute
only the defs affected by a change, driven by a dependency graph.

This piece answers one question cheaply: **when a def changes, which patches now match it?**

## The identity vs wildcard split

The cost of incremental recompute is dominated by how patches target defs:

- **Identity** patches anchor on a specific def, almost always via a `defName="X"` XPath
  predicate (`Defs/ThingDef[defName="Steel"]/statBases`). These collapse to an O(1)
  dictionary lookup keyed by node id — the cheap path.
- **Wildcard** patches match by structure or attribute and can match nodes that don't even
  exist yet (`Defs/ThingDef[statBases]`, `//li[text()="X"]`). They have no defName anchor,
  so each must be re-tested against changed nodes — the expensive path.

The **identity : wildcard ratio** is the headline number: if most patches are identity,
incremental recompute is cheap and worth building; if most are wildcard, the dependency
graph buys little and we'd re-test almost everything anyway.

## Classification heuristic

For each `patchEdge` (see input contract below):

1. Parse every `defName="..."` / `defName='...'` literal out of the XPath.
2. Compute whether the XPath contains a **structural signal** that could broaden the match
   beyond a single def: descendant axis (`//`), wildcards (`*`), positional predicates
   (`[1]`), `text()`, `contains()`, `starts-with()`, parent axis (`..`), or explicit
   `ancestor::`/`descendant::`/etc.
3. If there is at least one defName **and** no structural signal → **identity**, targeting
   those defNames.
4. Otherwise, if there is no structural signal and Piece A resolved the patch to **exactly
   one** node id → **identity**, deriving the defName from that node id (the part after the
   last `/`). This rescues patches with odd XPaths that empirically hit one def (e.g.
   matching on `label`).
5. Everything else → **wildcard**, keeping the XPath as the re-test predicate.

The heuristic is **deliberately conservative**: when uncertain, classify as wildcard.
Mislabelling a wildcard as identity would silently drop patches during incremental
recompute (a correctness bug); the reverse only costs a little extra re-testing.

## Why standalone, offline, dependency-free

The classifier operates purely on JSON strings (XPaths and node ids), so it needs no
RimWorld assemblies, no Harmony, and no live `PatchOperation` objects. It is a separate
`net481` console project kept **out of `RocketMan.sln`** (that solution is wired to the
publicised RimWorld reference assemblies). It ships its own tiny JSON reader/writer rather
than pulling a NuGet dependency, so it builds with the repo toolchain and zero restore
friction, and runs entirely offline.

## Build & run

```bash
FrameworkPathOverride=/usr/lib/mono/4.8-api \
  /home/deck/.dotnet/dotnet build Source/IncrementalCache/IncrementalCache.csproj -c Debug

# offline unit tests (bundled fixture)
mono Source/IncrementalCache/bin/Debug/Gagarin.IncrementalCache.exe --selftest

# classify a real graph
mono Source/IncrementalCache/bin/Debug/Gagarin.IncrementalCache.exe \
  classify DependencyGraph.json -o PatchClassification.json
```

## Contracts

### Input — `DependencyGraph.json` (produced by Piece A)
Only `patchEdges[]` is read; each edge contributes `patchId`, `sourceMod`,
`operationType`, `xpath`, `matchedNodeIds`, `modifiedNodeIds`. Unknown fields are ignored
for forward compatibility.

### Output — `PatchClassification.json` (consumed by Piece C)
```json
{
  "version": 1,
  "patches": [{"patchId":"...","sourceMod":"...","classification":"identity",
               "targetDefNames":["Steel"],"predicate":null}],
  "identityIndex": {"ThingDef/Steel": ["patchId1","patchId2"]},
  "summary": {"identityCount":0,"wildcardCount":0,
              "perMod":{"mod":{"identity":0,"wildcard":0}}}
}
```

`identityIndex` is the cheap lookup: nodeId → patches keyed to it. When the input has
resolved `matchedNodeIds`, those are used as keys; otherwise the bare defName is used as a
best-effort key for Piece C to reconcile once real ids are present.

## Open questions / unvalidated

- Run against **real** Piece A output: the sample fixture is synthetic. The structural-
  signal blocklist is tuned to common RimWorld XPath shapes and may need widening once real
  patch corpora are seen.
- `PatchOperationSequence` / `PatchOperationConditional` / `PatchOperationFindMod`:
  classification depends entirely on how Piece A flattens nested operations into edges. We
  assume each leaf op becomes its own edge (with its own xpath); the `seqmod.sequence`
  fixture edge models a single union-XPath edge as a sanity check.
- A defName combined with a descendant axis is currently demoted to wildcard. That is safe
  but may over-count wildcards; if such patterns are common we may want a tighter rule that
  still indexes them under the anchored def while flagging the descendant predicate.
