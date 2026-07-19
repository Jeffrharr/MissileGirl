#!/usr/bin/env bash
# TestMods/run_test.sh — Live incremental-cache test harness runner
#
# What this does:
#   1. Symlinks the three test mods into RimWorld's Mods folder if not already there.
#   2. Adds the three test-mod package IDs to ModsConfig.xml (after vr.missilegirl) if
#      not already present, then saves a backup of ModsConfig so it can be restored.
#   3. Clears the MissileGirl cache (forces a cold full rebuild on Run A) and copies
#      Change_RunA.xml → Patches/Change.xml.
#   4. Launches RimWorldLinux (sandbox disabled, to avoid the Boehm-GC SIGSEGV that hits
#      the Prepatcher prestarter GUI about 1 in 4 times under the sandbox). On that crash
#      it detects the GC_mark_from signature in Player.log and retries up to 3 times.
#   5. Waits for "Provenance captured" in Player.log (Run A end marker — the cold load
#      captured the dependency graph we need for Run B's dirty-set computation).
#   6. Copies Change_RunB.xml → Patches/Change.xml (changes the patch file so Gagarin
#      detects a cache miss on Run B), then launches again.
#   7. Waits for "Recompute gate" in Player.log (Run B end marker — both the dirty-set gate
#      and the recompute gate have run and written their reports by this point).
#   8. Parses GateReport.json (dirty-set gate: nonDirtyMismatches==0) AND
#      RecomputeReport.json (recompute gate: pass && recomputeMismatches==0 && !fallback).
#      Prints a one-line verdict for each. Both must pass.
#   9. Restores ModsConfig.xml + the workshop Gagarin.dll, and removes the Mods symlinks.
#
# Usage:
#   cd /home/deck/Developer/RimWorldMods/MissileGirl/TestMods
#   bash run_test.sh [--no-teardown] [--expect-fallback | --expect-added]
#     --no-teardown     leaves symlinks/ModsConfig/DLL in place
#     --expect-fallback uses the *_Fallback change files (the change mod owns a container op)
#                       and asserts RecomputeReport shows fallback==true && pass==true instead
#                       of the default real-recompute fallback==false && recomputeMismatches==0
#     --expect-added    exercises the ADDED-defs channel (P2): joof.testharness.added is kept OUT
#                       of ModsConfig for run A (so its defs are absent from the baseline graph)
#                       and added before run B — a genuine mod-list change. Its new concrete defs
#                       must show up dirty (DirtySet.json seeds.addedDefs > 0), recompute, and
#                       splice in. The Change.xml file is held at run A for both runs, so the ONLY
#                       change between runs is the new mod. Recompute assertion is the default
#                       (real recompute: fallback==false && recomputeMismatches==0).
#     --expect-recompute-gap  Asserts the RECOMPUTE ALLOWLIST declines a cross-def recompute-fidelity
#                       gap (CASE 6) and falls back. The unchanged static mod conditionally patches
#                       TC_CrossEffect based on a test against TC_CrossProbe; dirtying TC_CrossEffect
#                       would make it recompute over a sub-doc missing the probe, flipping the
#                       conditional and diverging from the rebuild. The allowlist admits only
#                       same-def conditionals (read set in the sub-doc), so this cross-def one is not
#                       allowlisted and forces a full rebuild. Asserts nonDirtyMismatches==0 (dirty
#                       set still a superset) AND fallback==true with a cross-def-conditional reason
#                       (pass==true, recomputeMismatches==0).
#     --expect-nested-conditional  Asserts the allowlist correctly resolves a conditional NESTED
#                       inside another conditional's branch (CASE 7, RecomputeAllowlist.BranchParentId
#                       regression, PR #37). TestMod_Static's outer conditional (same-def, in-sub-doc
#                       test) wraps an inner conditional (cross-def test on TC_NestedProbe) whose
#                       branch modifies TC_NestedTarget. TestMod_Change dirties TC_NestedTarget. Before
#                       the fix, BranchParentId resolved the leaf's gating parent to the OUTER
#                       (same-def) conditional instead of the INNER (cross-def) one and wrongly
#                       admitted the recompute, silently diverging from the rebuild. Same assertion
#                       shape as --expect-recompute-gap: nonDirtyMismatches==0 AND fallback==true with
#                       a conditional reason (pass==true, recomputeMismatches==0).
#     --expect-nested-in-sequence  Asserts CAPTURE records a conditional's own test edge when it is
#                       nested inside a PatchOperationSequence's <operations> list (CASE 8, issue #25
#                       item 3 — the ".operations[i]" id-label path, distinct from CASE 7's
#                       ".match"/".nomatch" label path). TestMod_Static wraps a cross-def conditional
#                       (test on TC_SeqNestedProbe) inside a sequence targeting TC_SeqNestedTarget.
#                       TestMod_Change dirties TC_SeqNestedTarget. Same gate assertion shape as
#                       --expect-recompute-gap/--expect-nested-conditional (nonDirtyMismatches==0 AND
#                       fallback==true with a conditional reason, recomputeMismatches==0), PLUS a
#                       direct read of the archived DependencyGraph.json asserting an edge with a
#                       PatchId ending in ".operations[0]" has a non-empty MatchedNodeIds — proving
#                       capture (not just the allowlist decision) saw the sequence-nested conditional.
#     --expect-findmod  Live fixture for issue #40 (PatchOperationFindMod capture). joof.testharness.findmod
#                       (UNCHANGED, byte-identical between runs) carries a PatchOperationFindMod that tests
#                       joof.testharness.gate's DISPLAY NAME (mirroring the real oppey.eyegenes2 /
#                       nals.facialanimation shape) and, when active, strips a tag from its own TC_FM_Host
#                       via a Sequence nested in the <match> branch. The gate mod is active for Run A and
#                       REMOVED before Run B (a pure mod-list change, same toggle as --expect-mayrequire),
#                       so TC_FM_Host's resolved content flips with neither mod's files changing. Asserts
#                       the dirty-set gate stays a superset (nonDirtyMismatches==0) AND the MayRequire seed
#                       fired (seeds.mayRequire > 0 in DirtySet.json) — proof FindModCapture.IndexFindMod
#                       fed the same Seed 6 index the P4 fixture exercises. Recompute is informational (not
#                       yet asserted required) since no recompute-fidelity predictor has been validated for
#                       FindMod-gated content the way MayRequireGate was for P4.
#     --expect-typeprovider  Live fixture for issue #86 (custom Def-type provider removal invalidates
#                       dependent defs across other mods). joof.testharness.typeconsumer (UNCHANGED,
#                       byte-identical between runs) declares several unconditional, unpatched
#                       <JoofTest.GadgetDef> instances -- a custom def type supplied by
#                       joof.testharness.typeprovider's own C# assembly, not this mod's own. The
#                       provider mod is active for Run A and REMOVED before Run B (a pure mod-list
#                       change, same toggle as --expect-mayrequire/--expect-findmod), so the .NET Type
#                       itself disappears and TestMod_TypeConsumer's defs can no longer be constructed
#                       by ANY mod's XML -- even though TestMod_TypeConsumer has no patches touching
#                       these defs and no MayRequire anywhere. This is the real oppey.eyegenes2 /
#                       nals.facialanimation shape (FacialAnimation.EyeballColorDef), reproduced with a
#                       throwaway type. PR 1 scope: asserts only the dirty-set gate stays a superset
#                       (nonDirtyMismatches==0) AND the new type-provider seed fired (seeds.typeProvider
#                       > 0 in DirtySet.json). Recompute is informational (not yet asserted required) --
#                       PR 2 will add a TypeProviderGate mirroring MayRequireGate and tighten this bar.
#     --expect-opgate   Live fixture for issue #40 case 3 (operation-wrapper MayRequire capture).
#                       joof.testharness.opgate (UNCHANGED, byte-identical between runs) carries a
#                       top-level PatchOperationSequence whose single <li Class="PatchOperationRemove"
#                       MayRequire="joof.testharness.gate"> strips a tag from its own TC_OG_Host --
#                       mirroring the real oppey.eyegenes2/nals.facialanimation shape exactly: MayRequire
#                       sits directly on the operation's own wrapper element (NOT inside a
#                       PatchOperationFindMod branch like --expect-findmod, and NOT as def content like
#                       --expect-mayrequire's P4). RimWorld's list deserializer consumes that attribute
#                       before the PatchOperation is even constructed, so the ordinary Apply hook never
#                       sees it -- capture only happens via ProvenanceRecorder.RecordOperationGate /
#                       IndexOperationGate at the deserialization boundary. The gate mod is active for
#                       Run A and REMOVED before Run B (same toggle as --expect-mayrequire/--expect-findmod),
#                       so TC_OG_Host's resolved content flips with neither mod's files changing. Asserts
#                       the dirty-set gate stays a superset (nonDirtyMismatches==0) AND the MayRequire seed
#                       fired (seeds.mayRequire > 0 in DirtySet.json) -- proof the pendingOperationGates
#                       ordering fix (Reset() split -> ResetPendingOperationGates(), called from
#                       TKeySystem_Parse_Patch.Postfix) lets IndexOperationGate actually drain the stash
#                       instead of it being wiped by ApplyPatches_Patch.Prefix before use.
#     --expect-ownership  Live fixture for issue #50 (def-ownership rematch for a changed-but-not-added
#                       mod). joof.testharness.owner is present in BOTH runs' modlist (no ModsConfig
#                       change at all -- unlike --expect-added/--add=, this never touches ModsConfig).
#                       Its own Defs/OwnerDefs.xml is swapped between runs: Run A omits TC_Ownership_Target
#                       entirely (joof.testharness.ownerbase, unchanged and loading first, is the sole
#                       owner captured in the baseline graph); Run B has it declare TC_Ownership_Target,
#                       and since it loads AFTER ownerbase, last-write-wins makes it the new real owner --
#                       a genuine ownership change with zero mod-list delta. Asserts the dirty-set gate
#                       stays a superset (nonDirtyMismatches==0) AND the changed def is dirtied AND the
#                       recompute gate passes (a clean content change, so recompute is required, same as
#                       --expect-p1). Bonus coverage (issue #53): also the only live fixture exercising
#                       the coverage audit (FoldGraph/DefOverrides) for a MOD-VS-MOD override, not just
#                       mod-vs-Core (#53's own test plan) -- pre-#53 this fixture's GateReport.json shows
#                       unattributedChangedCount:1 (false-flagged); post-#53 it's 0. Not asserted by the
#                       harness (informational field), see TestMods/README.md for the manual repro.
#     --expect-conditional-thirdmod  Live fixture for the OPEN nuance noted in
#                       docs/patch-operations-coverage.md's PatchOperationConditional row (CASE 9,
#                       distinct from CASES 6-8): the conditional's xpath-EXISTENCE test target
#                       (TC_ThirdModProbe) is supplied by a THIRD mod (TestMod_ThirdMod) — neither
#                       the conditional's owner (TestMod_Static, unchanged) nor the branch-mutated
#                       def's owner (TestMod_Defs, unchanged). TestMod_ThirdMod is present for Run A
#                       and REMOVED before Run B — a pure mod-list change, not a patch-file edit — so
#                       TC_ThirdModProbe's def node disappears entirely between runs. Change.xml is
#                       held at run A for both runs (same pattern as --expect-mayrequire/--remove), so
#                       the mod removal is the ONLY between-run delta. This mode does NOT assume the
#                       outcome: it asserts only the dirty-set superset gate (nonDirtyMismatches==0);
#                       the recompute gate is informational, since whether TC_CondTarget even shows up
#                       dirty is exactly the open question this fixture exists to answer.
#
# What this build proves (M2b-2b, sub-doc sibling expansion):
#   - Dirty-set gate: the dirty set is a true superset (no non-dirty def silently changed).
#   - Recompute gate: recomputing the dirty defs over the dirty+context sub-doc and splicing
#     onto the prior cache byte-matches a full rebuild over EVERY def. CASE 3 (TC_SeqSibling
#     pulled in as context so TestMod_Static's sequence reaches TC_SeqTarget) and CASE 5
#     (the unchanged static mod's conditional re-evaluates over the dirty TC_Conditional) are
#     the cases the dirty-only dead-end could not satisfy.
#
# Flags / build:
#   - This script enables the incremental-cache diagnostics at LAUNCH via GAGARIN_*
#     environment variables (exports below); the DLL no longer needs them edited in.
#   - It deploys the freshly-built dev Gagarin.dll (DEV_GAGARIN_DLL) over the workshop copy
#     the game loads (vr.missilegirl), backing up the original and restoring it on teardown.
#     >>> Build it first: FrameworkPathOverride=/usr/lib/mono/4.8-api \
#         dotnet build Source/Gagarin/Gagarin.csproj -c Release  <<<
#   - RimWorld 1.6 installed at the standard Steam path.
#   - Each cold load takes ~4 minutes; total wall time ~8-10 minutes.
#
# Notes:
#   - Kill signal for RimWorldLinux: pkill -9 -x RimWorldLinux is safe; do NOT use -f
#     (it matches the shell's own command line).

set -euo pipefail

# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------
RIMWORLD="/home/deck/.local/share/Steam/steamapps/common/RimWorld"
MODS_DIR="$RIMWORLD/Mods"
WORKSHOP_MISSILEGIRL="/home/deck/.local/share/Steam/steamapps/workshop/content/294100/3712928623"
CACHE_DIR="/home/deck/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/MissileGirl/Cache"
PLAYER_LOG="/home/deck/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Player.log"
MODSCONFIG="/home/deck/.config/unity3d/Ludeon Studios/RimWorld by Ludeon Studios/Config/ModsConfig.xml"
MODSCONFIG_BAK="/tmp/ModsConfig_testharness.bak.xml"
GATE_REPORT="$CACHE_DIR/GateReport.json"
RECOMPUTE_REPORT="$CACHE_DIR/RecomputeReport.json"
DIRTYSET_REPORT="$CACHE_DIR/DirtySet.json"

# The game loads vr.missilegirl from the WORKSHOP folder, so the dev build must be deployed
# there (not into Mods/). We back up the existing workshop DLL and restore it on teardown.
DEV_GAGARIN_DLL="/home/deck/Developer/RimWorldMods/MissileGirl/1.6/Plugins/Stable/Gagarin.dll"
WORKSHOP_GAGARIN_DLL="$WORKSHOP_MISSILEGIRL/1.6/Plugins/Stable/Gagarin.dll"
WORKSHOP_GAGARIN_BAK="/tmp/Gagarin_testharness.bak.dll"

# Enable the incremental-cache diagnostics at launch (GagarinPrefs reads these on first
# access). Exported here so the RimWorldLinux child process launched below inherits them.
export GAGARIN_CAPTURE_PROVENANCE=1
export GAGARIN_DIRTYSET_DIAGNOSTIC=1
export GAGARIN_DIRTYSET_GATE=1
export GAGARIN_DIRTYSET_RECOMPUTE=1
# TypeProviderGate recompute fidelity (issue #86 PR 2, GagarinPrefs.TypeProviderRecompute) —
# not yet live-validated, so it stays behind its own flag rather than folding unconditionally
# into DirtySetRecompute like MayRequireGate. Enabled here so --expect-typeprovider (and every
# other fixture, harmlessly -- no TypeProviderIndex entries means an empty lookup) actually
# exercises it; once a live run confirms recomputeMismatches==0 this can fold into the
# unconditional DefRecompute path and this export can be dropped.
export GAGARIN_TYPEPROVIDER_RECOMPUTE=1
# The master toggle enables PriorStateSnapshot (the sidecar under .../MissileGirl/Incremental/prior/).
# It is the ONLY source of the TRUE prior modlist/Unified/hash that survives OnInitialization's
# modlist-change teardown — the live ModList.xml is re-dumped to the CURRENT order before the
# diagnostic reads it. Without it, any seed that compares prior vs current load order (reorder /
# MayRequire) silently sees no change on a mod add/remove and never fires. It gates ONLY the sidecar
# (verified: GagarinPrefs.IncrementalCache is referenced nowhere else), so it does not alter the
# full-rebuild path the gates compare against.
export GAGARIN_INCREMENTAL_CACHE=1
# Testing-only: Verse.Log silently disables ALL logging after 10000 total messages from ANY
# source, which would swallow the "Provenance captured"/"Recompute gate" markers this script
# polls Player.log for on a heavy (hundreds-of-real-mods) run, making a fine (or genuinely
# stuck — the cap makes it impossible to tell which) run look like a hang. See
# GagarinPrefs.DisableLogCap / Log_Patch.cs.
export GAGARIN_DISABLE_LOG_CAP=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHANGE_MOD_DIR="$SCRIPT_DIR/TestMod_Change"
DEFS_MOD_DIR="$SCRIPT_DIR/TestMod_Defs"
STATIC_MOD_DIR="$SCRIPT_DIR/TestMod_Static"
ADDED_MOD_DIR="$SCRIPT_DIR/TestMod_Added"
MAYREQUIRE_MOD_DIR="$SCRIPT_DIR/TestMod_MayRequire"
GATE_MOD_DIR="$SCRIPT_DIR/TestMod_Gate"
P1_MOD_DIR="$SCRIPT_DIR/TestMod_P1"
GENOP_MOD_DIR="$SCRIPT_DIR/TestMod_GenOp"
ROGUEOP_MOD_DIR="$SCRIPT_DIR/TestMod_RogueOp"
FINDMOD_MOD_DIR="$SCRIPT_DIR/TestMod_FindMod"
OPGATE_MOD_DIR="$SCRIPT_DIR/TestMod_OpGate"
THIRDMOD_MOD_DIR="$SCRIPT_DIR/TestMod_ThirdMod"
OWNERBASE_MOD_DIR="$SCRIPT_DIR/TestMod_OwnerBase"
OWNER_MOD_DIR="$SCRIPT_DIR/TestMod_Owner"
TRIALEXEC_MOD_DIR="$SCRIPT_DIR/TestMod_TrialExec"
TYPEPROVIDER_MOD_DIR="$SCRIPT_DIR/TestMod_TypeProvider"
TYPECONSUMER_MOD_DIR="$SCRIPT_DIR/TestMod_TypeConsumer"

# --expect-findmod: exercise the #40 PatchOperationFindMod capture fix. joof.testharness.findmod (an
# UNCHANGED mod) carries a PatchOperationFindMod gated on joof.testharness.gate's DISPLAY NAME (the
# real-world shape: oppey.eyegenes2's patch gated on nals.facialanimation's presence). The gate mod is
# active for run A (so the match branch's Remove strips TC_FM_Host's GatedTag and FindModCapture indexes
# it) and REMOVED before run B (a pure mod-list change). TC_FM_Host must show up dirty via the MayRequire
# seed (DirtySet.json seeds.mayRequire > 0), same as P4, since FindModCapture feeds the same index.
EXPECT_FINDMOD=0

# --expect-opgate: exercise the #40 case-3 pendingOperationGates ordering fix. joof.testharness.opgate
# (an UNCHANGED mod) carries a top-level PatchOperationSequence whose single <li Class=
# "PatchOperationRemove" MayRequire="joof.testharness.gate"> strips a tag from its own TC_OG_Host --
# MayRequire on the operation's own wrapper element (the real oppey.eyegenes2/nals.facialanimation
# shape), not via PatchOperationFindMod branching and not as def content. The gate mod is active for
# run A (so the Remove fires and RecordOperationGate/IndexOperationGate index it) and REMOVED before
# run B (a pure mod-list change). TC_OG_Host must show up dirty via the MayRequire seed (DirtySet.json
# seeds.mayRequire > 0), proving pendingOperationGates survives to be drained instead of being wiped
# by ApplyPatches_Patch.Prefix's old Reset() timing.
EXPECT_OPGATE=0

# --expect-ownership: exercise the #50 def-ownership rematch fix. joof.testharness.owner is present
# in BOTH run A and run B's modlist (never added/removed via ModsConfig -- no presence flip at all).
# Its own Defs/OwnerDefs.xml is swapped between runs: run A does not declare TC_Ownership_Target
# (joof.testharness.ownerbase, an UNCHANGED mod that loads first, is the sole owner in the baseline
# graph); run B has it newly declare TC_Ownership_Target, and since it loads AFTER ownerbase,
# last-write-wins makes it the new real owner. Neither Seed 1 (baseline SourceFile points at
# ownerbase's file, not owner's), Seed 5 (id already in baseline), nor Seed 7/7b (no presence flip)
# catch this without the fix -- ComputeDefOverrideFlips's candidate set must also include mods whose
# own Defs files changed this load, not just newly-added ones.
#
# Incidental bonus coverage (issue #53): this is also the only live fixture exercising the
# capture-completeness audit (DirtySetGate.FoldGraph/graph.DefOverrides) for a MOD-VS-MOD def
# override -- #53's own test plan only covered a mod overriding a Core def. Confirmed by running
# this fixture against a pre-#53 build: GateReport.json reported unattributedChangedCount:1 with
# unattributedChangedIds:["ThingDef/TC_Ownership_Target"] (the audit false-flagging the ownership
# transfer as an invisible op); against a build with #53's fix it's 0. That field is informational
# only (doesn't affect pass), so it's not asserted here -- see TestMods/README.md.
EXPECT_OWNERSHIP=0

NO_TEARDOWN=0
# --expect-fallback: exercise the changed-mod container-op fallback instead of the default
# real-recompute case. It swaps in the *_Fallback change files (in which the CHANGED mod owns
# a PatchOperationSequence) and flips the recompute-report assertion to require fallback==true.
EXPECT_FALLBACK=0
# --expect-added: exercise the added-defs channel (P2). joof.testharness.added is symlinked but
# held OUT of ModsConfig for run A and inserted before run B, so run B is a genuine mod-list
# change whose new concrete defs are absent from the baseline graph (seeds.addedDefs > 0). The
# recompute assertion stays the default (real recompute), and Change.xml is held at run A so the
# only between-run delta is the new mod.
EXPECT_ADDED=0
# --expect-mayrequire: exercise the MayRequire-flip channel (P4). joof.testharness.mayrequire (an
# UNCHANGED mod) carries content gated on joof.testharness.gate — a whole def at its root and a
# patch-injected <li>. The gate mod is activated for run A (so the gated content is included and
# indexed) and REMOVED before run B (a pure mod-list change). The gated defs must show up dirty via
# the MayRequire seed (DirtySet.json seeds.mayRequire > 0) so the dirty-set gate stays a superset,
# AND the recompute gate must pass (DefRecompute drops the now-failing gated def via MayRequireGate,
# matching the rebuild). Change.xml is held at run A for both runs, so the ONLY between-run delta is
# the gate mod removal.
EXPECT_MAYREQUIRE=0
# --expect-p1: exercise the node-id keying fix (P1). joof.testharness.p1 ships a C# assembly with a
# custom def subclass JoofTest.PropDef, authored in XML by its fully-qualified element name
# (<JoofTest.PropDef>, simple type name "PropDef"). The harness swaps the def's p1Tag value between
# run A and run B (a changed DEF file, modlist unchanged), so Seed 1 must dirty it by its NODE id.
# With the fix that id is the element-name "JoofTest.PropDef/TC_P1_Prop" (matching the gate/Unified);
# the old GetType().Name keying produced "PropDef/TC_P1_Prop" and the gate silently missed it. Asserts
# the dirty-set gate stays a superset AND the dirty set contains the element-name id. Change.xml is
# held at run A so the only between-run delta is the P1 def file.
EXPECT_P1=0
# --expect-recompute-gap: assert the SAFETY PREDICTOR catches a cross-def recompute-fidelity gap and
# falls back instead of silently serving a wrong value. TestMod_Static (UNCHANGED) carries a cross-def
# PatchOperationConditional whose TEST is on TC_CrossProbe but whose EFFECT is on TC_CrossEffect
# (CASE 6). TestMod_Change dirties TC_CrossEffect (run-a->run-b); recomputing it over a sub-doc that
# lacks the never-dirty TC_CrossProbe would take the wrong branch and diverge from the full rebuild.
# RecomputeAllowlist declines this (the conditional's read set is not in the sub-doc, so it is not on
# the allowlist) and forces a full-rebuild fallback. Asserts the dirty-set gate stays a superset
# (nonDirtyMismatches==0) AND fallback==true with a cross-def-conditional reason (pass==true,
# recomputeMismatches==0). Before the allowlist this mode asserted the gap REPRODUCED
# (recomputeMismatches>0); the flip is the proof the fix works.
EXPECT_GAP=0
# --expect-nested-conditional: exercise the BranchParentId nested-conditional fix (CASE 7, PR #37,
# issue #25). TestMod_Static (UNCHANGED) carries an OUTER conditional (same-def test on
# TC_NestedTarget) whose match branch is an INNER conditional (cross-def test on TC_NestedProbe)
# that modifies TC_NestedTarget. TestMod_Change dirties TC_NestedTarget (run-a->run-b). Same
# assertion shape as --expect-recompute-gap: the allowlist must resolve the leaf's IMMEDIATE
# (inner, cross-def) gating parent, not the outer one, and decline.
EXPECT_NESTED=0
# --expect-nested-in-sequence: prove CAPTURE (not just the allowlist) records a conditional's own
# test edge when it lives inside a PatchOperationSequence's <operations> list (CASE 8, issue #25
# item 3). TestMod_Static (UNCHANGED) carries a PatchOperationSequence whose operations[0] is a
# cross-def conditional (test on TC_SeqNestedProbe) that modifies TC_SeqNestedTarget.
# TestMod_Change dirties TC_SeqNestedTarget (run-a->run-b). Same gate assertion shape as
# --expect-nested-conditional, PLUS a direct check that DependencyGraph.json holds an edge whose
# PatchId ends in ".operations[0]" with a non-empty MatchedNodeIds.
EXPECT_SEQNESTED=0
# --expect-conditional-thirdmod: CASE 9, the open nuance in docs/patch-operations-coverage.md. See
# the header comment above for the full scenario. TestMod_ThirdMod (present run A, removed run B)
# supplies TC_ThirdModProbe, the sole target of TestMod_Static's xpath-existence conditional test;
# neither TestMod_Static nor TestMod_Defs (TC_CondTarget's owner) changes between runs. Change.xml
# is held at run A for both runs, so the mod removal is the only between-run delta. Only the
# dirty-set gate is asserted (nonDirtyMismatches==0); recompute is informational — this fixture's
# purpose is to reveal whether the pipeline already covers this case, not to assume it does.
EXPECT_THIRDMOD=0
# --expect-op-kind: positive regression, PASSES live (validated 2026-07-09). TestMod_Static
# (UNCHANGED) carries a PatchOperationFindMod (CASE 10) targeting "Harmony" (brrainz.harmony's
# display name, always present) so its <match> fires identically in both runs -- isolating the
# allowlist's admission decision from FindMod's own branch-evaluation semantics (already covered by
# TestMod_FindMod / issue #40). TestMod_Change dirties TC_OpKind_Target directly (run-a->run-b, Seed
# 2). Verse.PatchOperationFindMod is in SafeLeafOps (offline-pinned by
# RecomputeAllowlistTests.FindMod_ShouldBeAdmitted_OnceProvenSafe), so RecomputeAllowlist must ADMIT
# the recompute -- the DEFAULT recompute assertion (fallback==false, recomputeMismatches==0) below.
EXPECT_OPKIND=0
# --expect-order-preserved: positive regression pinning DefRecompute's ordering guarantee for defs
# actually present in the sub-doc (CASE 11). TestMod_Static (UNCHANGED) applies two same-def ops to
# TC_Order_Source in file order: an unconditional Add setting orderFlag=on, then a
# PatchOperationConditional whose test reads that same orderFlag and, on match, adds
# orderTag=flag-was-on. TestMod_Change dirties TC_Order_Source directly (run-a->run-b, Seed 2).
# Unlike --expect-recompute-gap/--expect-nested-conditional, both ops are same-def (in the sub-doc),
# so RecomputeAllowlist must ADMIT them and the recompute gate uses the DEFAULT real-recompute
# assertion (fallback==false, recomputeMismatches==0) -- no special parser needed.
EXPECT_ORDERPRESERVED=0
# --expect-test-op: positive regression, PASSES live (issue #74). CASE 15: TestMod_Static
# (UNCHANGED) carries a PatchOperationSequence whose operations are [0] a PatchOperationTest (an
# always-true, same-def, non-positional existence gate on TC_TestGate_Target itself) then [1] a
# PatchOperationAdd that mutates the same def. Verse.PatchOperationTest has been in SafeLeafOps
# since 2026-07-14 (its ApplyWorker is a pure `SelectSingleNode(xpath) != null` read that never
# touches the XmlDocument -- offline-pinned by
# RecomputeAllowlistTests.Test_ShouldBeAdmitted_OnceProvenSafe), but that entry had never been
# proven live past the fallback point with the recompute gate REQUIRED to pass. TestMod_Change
# dirties TC_TestGate_Target directly (run-a->run-b, Seed 2). Same shape as --expect-op-kind --
# RecomputeAllowlist must ADMIT the recompute, so the recompute gate uses the DEFAULT
# real-recompute assertion (fallback==false, recomputeMismatches==0); recompute_required stays 1.
EXPECT_TESTOP=0
# --expect-scope-escape: live proof that RecomputeAllowlist's positional-safety check is a
# structural per-step parser, not a string-position heuristic (review-flagged gap, offline-pinned by
# RecomputeAllowlistTests.PositionalXpath_SiblingAxisAfterAnchor_Declined /
# PositionalXpath_AfterScopeBreak_Declined). TestMod_Static (UNCHANGED) carries a
# PatchOperationReplace (CASE 12) anchored on TC_ScopeEscape_A's defName but escaping via
# "following-sibling::ThingDef[1]" to TC_ScopeEscape_B -- a lexically-earlier anchor that does NOT
# structurally scope the positional step. TestMod_Change dirties TC_ScopeEscape_B directly (run-a ->
# run-b, Seed 2). Same assertion shape as --expect-recompute-gap: the dirty-set gate must still be a
# superset, and the recompute allowlist must DECLINE (fallback=true, pass=true,
# recomputeMismatches=0) with a fallbackReason naming "positional" -- proving the fix, not just that
# it doesn't crash.
EXPECT_SCOPEESCAPE=0
# --expect-rogue-op: live proof that RecomputeAllowlist.SafeLeafOps is keyed by fully-qualified
# Type.FullName, not bare simple name (the original CRITICAL spoofing gap). TestMod_RogueOp ships
# its OWN class literally named "PatchOperationAdd" (namespace RogueMod, NOT Verse) applied via
# Class="RogueMod.PatchOperationAdd" (CASE 13); it always produces TC_RogueOp_Target. TestMod_Change
# dirties TC_RogueOp_Target directly (run-a -> run-b, Seed 2). Same assertion shape as
# --expect-op-kind: the dirty-set gate must still be a superset, and the recompute allowlist must
# DECLINE (fallback=true, pass=true, recomputeMismatches=0) with a fallbackReason naming
# RogueMod.PatchOperationAdd -- proving the FullName, not just the bare "PatchOperationAdd", is what
# gets checked against SafeLeafOps.
EXPECT_ROGUEOP=0
# --expect-patch-injected-child: positive regression for DefRecompute.BuildTopLevelIdsToRun's
# "unreachable patch-injected child" gap. TestMod_Static (UNCHANGED) injects a brand-new top-level
# def, TC_PatchInjected_Child (CASE 14), via a PatchOperationAdd targeting the Defs ROOT — that
# child has no raw body of its own and ParentName="TC_PatchInjectedBase". TestMod_Change dirties
# TC_PatchInjectedBase directly (run-a -> run-b, Seed 2); inheritance-closure fan-out then dirties
# TC_PatchInjected_Child too, even though its only owner (TestMod_Static) never changed. Before the
# fix, the unchanged-mod top-level filter could never mark TestMod_Static's Add op relevant (its
# captured edge records the Defs-root anchor, never the new child's own id) and DefRecompute wrongly
# recomputed the child as deleted. Same assertion shape as --expect-order-preserved: both ops are
# reachable and must be replayed, so the recompute gate uses the DEFAULT real-recompute assertion
# (fallback==false, recomputeMismatches==0) — no special parser needed.
EXPECT_PATCHINJECTEDCHILD=0
# --expect-trial-execution: issue #72, the trial-execution escape hatch for SubDocExpander's two
# blunt declines. joof.testharness.trialexec is symlinked always but held OUT of ModsConfig for run
# A and inserted before run B -- same P2-style mod-list-change convention as TestMod_Added -- so it
# is simultaneously BOTH decline conditions at once: wholly new this load, AND its own patch is a
# PatchOperationSequence (a container op) applied by the changed mod itself. Either alone forces
# needsFullRebuild==true under the OLD behavior. Its sequence targets only its own two new
# ThingDefs, already dirtied by the added-defs seed (Seed 5) before TrialExecution ever runs, so a
# bounded trial replay (scoped to dirty union context, never the mod's whole universe) discovers
# zero new top-level ids and TrialContainment.IsContained trivially admits it. Same assertion shape
# as --expect-order-preserved/--expect-patch-injected-child: both declines are overridden, so the
# recompute gate uses the DEFAULT real-recompute assertion (fallback==false, recomputeMismatches==0)
# -- proving ADMISSION, not another decline.
EXPECT_TRIALEXEC=0
# --expect-typeprovider: issue #86, custom Def-type provider removal. joof.testharness.typeprovider
# (present Run A, REMOVED before Run B) ships a C# assembly defining JoofTest.GadgetDef : Def;
# joof.testharness.typeconsumer (UNCHANGED both runs) declares unconditional, unpatched
# <JoofTest.GadgetDef> instances of its own. PR 1 scope: dirty-set superset gate
# (nonDirtyMismatches==0) + seeds.typeProvider > 0 in DirtySet.json; recompute is informational.
EXPECT_TYPEPROVIDER=0
# --modlist=FILE: an OPTIONAL extra modlist (one packageId per line, '#' comments allowed) to load
# ON TOP OF the minimal base (Core + DLCs + Harmony + Gagarin + test mods). Use it to reproduce a
# specific problem set captured from a prior run. Hard-capped at MAX_MODS total (default 150) — the
# whole point of the harness is a small, deterministic load; an 815-mod ambient list crashes
# RimWorld's GC during unrelated mod init (zero of our code runs), which is not a test result.
#
# --modlist-verbatim=FILE: like --modlist=, but the file's line order IS the final load order —
# Core/DLC/Harmony/Gagarin are NOT prepended. Use this when the file already came from a real,
# dependency-aware sort (e.g. RimSort) that placed load-order-sensitive mods (prepatcher,
# loadthemlast, etc.) around Core/Harmony rather than after them; the --modlist= prepend-then-append
# scheme would silently break that ordering. Must already contain ludeon.rimworld + vr.missilegirl;
# test-harness mods are still appended at the end.
# --modlist-b=FILE / --modlist-verbatim-b=FILE: like --modlist=/--modlist-verbatim=, but for RUN B
# instead of run A. When given, Step 4 writes ModsConfig for run B from this file directly (same
# construction as run A: Core+DLC+Harmony+Gagarin+file+test-mods, or verbatim if the -verbatim-b
# variant) instead of applying --remove=/--add= regex surgery to run A's ModsConfig. Both run A and
# run B's exact modlists get archived, so a failing run is fully reproducible from two plain files
# instead of reconstructing a diff — easier to debug than the remove/add mutation approach, and
# immune to it (a diff-based B list survives independently of whatever run A's ModsConfig looked
# like right before run B, e.g. after an engine-side ModsConfig.Reset() on a fatal load error).
# Mutually exclusive with --remove=/--add= (pick one style per run).
MODLIST_FILE=""
MODLIST_VERBATIM=""
MODLIST_FILE_B=""
MODLIST_VERBATIM_B=""
MAX_MODS=200
# --remove=PACKAGEID: a real-mod removal scenario. Run A loads the full --modlist; before run B this
# mod is removed from ModsConfig (a pure mod-list change), reproducing real add/remove failures (e.g.
# the vmemese case: removing it drops VFE Props' IfModActive loadFolders prop defs -> P1, and flips
# toastyman's MayRequire ThingStyleDefs -> P4). Asserts only the dirty-set superset gate
# (nonDirtyMismatches==0); recompute is informational (real content includes MayRequire defs that the
# recompute-fidelity gap can't yet reproduce).
REMOVE_MOD=""
# --add=PACKAGEID: the inverse of --remove= — Run A's --modlist/--modlist-verbatim must NOT contain
# this mod; it is inserted into ModsConfig (just before </activeMods>) before Run B, reproducing a
# real mod-ADD scenario (as opposed to --expect-added's synthetic joof.testharness.added mod).
# Comma-separated for multiple. Same gate contract as --remove=: dirty-set superset required,
# recompute informational.
ADD_MOD=""
for arg in "$@"; do
    if [[ "$arg" == "--no-teardown" ]]; then
        NO_TEARDOWN=1
    elif [[ "$arg" == "--expect-fallback" ]]; then
        EXPECT_FALLBACK=1
    elif [[ "$arg" == "--expect-added" ]]; then
        EXPECT_ADDED=1
    elif [[ "$arg" == "--expect-mayrequire" ]]; then
        EXPECT_MAYREQUIRE=1
    elif [[ "$arg" == "--expect-p1" ]]; then
        EXPECT_P1=1
    elif [[ "$arg" == "--expect-recompute-gap" ]]; then
        EXPECT_GAP=1
    elif [[ "$arg" == "--expect-nested-conditional" ]]; then
        EXPECT_NESTED=1
    elif [[ "$arg" == "--expect-nested-in-sequence" ]]; then
        EXPECT_SEQNESTED=1
    elif [[ "$arg" == "--expect-conditional-thirdmod" ]]; then
        EXPECT_THIRDMOD=1
    elif [[ "$arg" == "--expect-findmod" ]]; then
        EXPECT_FINDMOD=1
    elif [[ "$arg" == "--expect-opgate" ]]; then
        EXPECT_OPGATE=1
    elif [[ "$arg" == "--expect-ownership" ]]; then
        EXPECT_OWNERSHIP=1
    elif [[ "$arg" == "--expect-op-kind" ]]; then
        EXPECT_OPKIND=1
    elif [[ "$arg" == "--expect-order-preserved" ]]; then
        EXPECT_ORDERPRESERVED=1
    elif [[ "$arg" == "--expect-test-op" ]]; then
        EXPECT_TESTOP=1
    elif [[ "$arg" == "--expect-scope-escape" ]]; then
        EXPECT_SCOPEESCAPE=1
    elif [[ "$arg" == "--expect-rogue-op" ]]; then
        EXPECT_ROGUEOP=1
    elif [[ "$arg" == "--expect-patch-injected-child" ]]; then
        EXPECT_PATCHINJECTEDCHILD=1
    elif [[ "$arg" == "--expect-trial-execution" ]]; then
        EXPECT_TRIALEXEC=1
    elif [[ "$arg" == "--expect-typeprovider" ]]; then
        EXPECT_TYPEPROVIDER=1
    elif [[ "$arg" == --modlist=* ]]; then
        MODLIST_FILE="${arg#--modlist=}"
    elif [[ "$arg" == --modlist-verbatim=* ]]; then
        MODLIST_VERBATIM="${arg#--modlist-verbatim=}"
    elif [[ "$arg" == --modlist-b=* ]]; then
        MODLIST_FILE_B="${arg#--modlist-b=}"
    elif [[ "$arg" == --modlist-verbatim-b=* ]]; then
        MODLIST_VERBATIM_B="${arg#--modlist-verbatim-b=}"
    elif [[ "$arg" == --remove=* ]]; then
        REMOVE_MOD="${arg#--remove=}"
    elif [[ "$arg" == --add=* ]]; then
        ADD_MOD="${arg#--add=}"
    fi
done

# Single source of truth for "how many --expect-* modes are active" — both the mutual-exclusivity
# check and the --remove= incompatibility check must agree on this set, so they read the same array
# instead of each hand-listing the flags (which drifted silently until now: add a new EXPECT_* and
# forget one of the two sums, and the new mode just silently combines with another instead of
# erroring).
EXPECT_FLAGS=($EXPECT_FALLBACK $EXPECT_ADDED $EXPECT_MAYREQUIRE $EXPECT_P1 $EXPECT_GAP $EXPECT_NESTED $EXPECT_SEQNESTED $EXPECT_THIRDMOD $EXPECT_FINDMOD $EXPECT_OPGATE $EXPECT_OWNERSHIP $EXPECT_OPKIND $EXPECT_ORDERPRESERVED $EXPECT_TESTOP $EXPECT_SCOPEESCAPE $EXPECT_ROGUEOP $EXPECT_PATCHINJECTEDCHILD $EXPECT_TRIALEXEC $EXPECT_TYPEPROVIDER)
sum_expect_flags() {
    local sum=0
    for f in "${EXPECT_FLAGS[@]}"; do
        sum=$(( sum + f ))
    done
    echo "$sum"
}

# A single timestamp for this whole run, used for every archived artifact (reports AND the exact
# modlist that produced them) so a failing run is fully reproducible from one set of files.
RUN_TS="$(date +%Y%m%d-%H%M%S)"
METRICS_DIR="/home/deck/Developer/RimWorldMods/MissileGirl-metrics"

if (( $(sum_expect_flags) > 1 )); then
    echo "[run_test] FAIL: the --expect-* flags are mutually exclusive." >&2
    exit 2
fi
if [[ -n "$REMOVE_MOD" ]] && (( $(sum_expect_flags) > 0 )); then
    echo "[run_test] FAIL: --remove= (real-mod removal) cannot be combined with an --expect-* mode." >&2
    exit 2
fi
if [[ -n "$ADD_MOD" ]] && (( $(sum_expect_flags) > 0 )); then
    echo "[run_test] FAIL: --add= (real-mod addition) cannot be combined with an --expect-* mode." >&2
    exit 2
fi
if [[ -n "$REMOVE_MOD" && -z "$MODLIST_FILE" && -z "$MODLIST_VERBATIM" ]]; then
    echo "[run_test] WARN: --remove=$REMOVE_MOD with no --modlist=/--modlist-verbatim= — the mod must already be in the minimal base or nothing is removed." >&2
fi
if [[ -n "$ADD_MOD" && -z "$MODLIST_FILE" && -z "$MODLIST_VERBATIM" ]]; then
    echo "[run_test] WARN: --add=$ADD_MOD with no --modlist=/--modlist-verbatim= — there is no symlinked real mod to add." >&2
fi
if [[ -n "$MODLIST_FILE" && -n "$MODLIST_VERBATIM" ]]; then
    echo "[run_test] FAIL: --modlist= and --modlist-verbatim= are mutually exclusive." >&2
    exit 2
fi
if [[ -n "$MODLIST_FILE_B" && -n "$MODLIST_VERBATIM_B" ]]; then
    echo "[run_test] FAIL: --modlist-b= and --modlist-verbatim-b= are mutually exclusive." >&2
    exit 2
fi
if [[ ( -n "$MODLIST_FILE_B" || -n "$MODLIST_VERBATIM_B" ) && ( -n "$REMOVE_MOD" || -n "$ADD_MOD" ) ]]; then
    echo "[run_test] FAIL: --modlist-b=/--modlist-verbatim-b= cannot be combined with --remove=/--add= — pick one style for the run-B mod-list change." >&2
    exit 2
fi
if [[ ( -n "$MODLIST_FILE_B" || -n "$MODLIST_VERBATIM_B" ) && (( $(sum_expect_flags) > 0 )) ]]; then
    echo "[run_test] FAIL: --modlist-b=/--modlist-verbatim-b= cannot be combined with an --expect-* mode." >&2
    exit 2
fi

# Pick the change-file pair for this run mode. Default: the leaf-op fixtures that drive a real
# sub-doc recompute. --expect-fallback: the fixtures whose change mod owns a container op, so
# SubDocExpander declines to recompute and the full rebuild stands in.
if [[ $EXPECT_FALLBACK -eq 1 ]]; then
    RUN_A_CHANGE="Change_RunA_Fallback.xml"
    RUN_B_CHANGE="Change_RunB_Fallback.xml"
elif [[ $EXPECT_GAP -eq 1 ]]; then
    # --expect-recompute-gap: the change file MUST differ between runs (run-a -> run-b) so
    # TC_CrossEffect is seeded dirty (Seed 2) and thus recomputed over a sub-doc missing TC_CrossProbe.
    RUN_A_CHANGE="Change_RunA_CrossDef.xml"
    RUN_B_CHANGE="Change_RunB_CrossDef.xml"
elif [[ $EXPECT_NESTED -eq 1 ]]; then
    # --expect-nested-conditional: same shape as --expect-recompute-gap — the change file MUST differ
    # between runs so TC_NestedTarget is seeded dirty (Seed 2) and recomputed under the nested
    # conditional TestMod_Static carries.
    RUN_A_CHANGE="Change_RunA_NestedConditional.xml"
    RUN_B_CHANGE="Change_RunB_NestedConditional.xml"
elif [[ $EXPECT_SEQNESTED -eq 1 ]]; then
    # --expect-nested-in-sequence: same shape as --expect-nested-conditional — the change file MUST
    # differ between runs so TC_SeqNestedTarget is seeded dirty (Seed 2) and recomputed under the
    # sequence-nested conditional TestMod_Static carries.
    RUN_A_CHANGE="Change_RunA_SeqNestedConditional.xml"
    RUN_B_CHANGE="Change_RunB_SeqNestedConditional.xml"
elif [[ $EXPECT_OPKIND -eq 1 ]]; then
    # --expect-op-kind: same shape as --expect-recompute-gap — the change file MUST differ between
    # runs so TC_OpKind_Target is seeded dirty (Seed 2) and recomputed under the FindMod op
    # TestMod_Static carries.
    RUN_A_CHANGE="Change_RunA_OpKind.xml"
    RUN_B_CHANGE="Change_RunB_OpKind.xml"
elif [[ $EXPECT_ORDERPRESERVED -eq 1 ]]; then
    # --expect-order-preserved: same shape — the change file MUST differ between runs so
    # TC_Order_Source is seeded dirty (Seed 2) and recomputed under the ordered same-def ops
    # TestMod_Static carries.
    RUN_A_CHANGE="Change_RunA_OrderPreserved.xml"
    RUN_B_CHANGE="Change_RunB_OrderPreserved.xml"
elif [[ $EXPECT_TESTOP -eq 1 ]]; then
    # --expect-test-op: same shape — the change file MUST differ between runs so
    # TC_TestGate_Target is seeded dirty (Seed 2) and recomputed under the PatchOperationTest-gated
    # sequence TestMod_Static carries.
    RUN_A_CHANGE="Change_RunA_TestOp.xml"
    RUN_B_CHANGE="Change_RunB_TestOp.xml"
elif [[ $EXPECT_SCOPEESCAPE -eq 1 ]]; then
    # --expect-scope-escape: same shape as --expect-recompute-gap — the change file MUST differ
    # between runs so TC_ScopeEscape_B is seeded dirty (Seed 2) and recomputed under the
    # sibling-axis scope-escape op TestMod_Static carries.
    RUN_A_CHANGE="Change_RunA_ScopeEscape.xml"
    RUN_B_CHANGE="Change_RunB_ScopeEscape.xml"
elif [[ $EXPECT_ROGUEOP -eq 1 ]]; then
    # --expect-rogue-op: same shape as --expect-op-kind — the change file MUST differ between runs
    # so TC_RogueOp_Target is seeded dirty (Seed 2) and recomputed under TestMod_RogueOp's bare-
    # name-colliding PatchOperationAdd.
    RUN_A_CHANGE="Change_RunA_RogueOp.xml"
    RUN_B_CHANGE="Change_RunB_RogueOp.xml"
elif [[ $EXPECT_PATCHINJECTEDCHILD -eq 1 ]]; then
    # --expect-patch-injected-child: same shape as --expect-order-preserved — the change file MUST
    # differ between runs so TC_PatchInjectedBase is seeded dirty (Seed 2), fanning out via
    # inheritance closure to TestMod_Static's (unchanged) patch-injected TC_PatchInjected_Child.
    RUN_A_CHANGE="Change_RunA_PatchInjectedChild.xml"
    RUN_B_CHANGE="Change_RunB_PatchInjectedChild.xml"
elif [[ $EXPECT_ADDED -eq 1 || $EXPECT_MAYREQUIRE -eq 1 || $EXPECT_P1 -eq 1 || $EXPECT_THIRDMOD -eq 1 || $EXPECT_FINDMOD -eq 1 || $EXPECT_OPGATE -eq 1 || $EXPECT_TRIALEXEC -eq 1 || $EXPECT_TYPEPROVIDER -eq 1 || -n "$REMOVE_MOD" || -n "$ADD_MOD" ]]; then
    # P2 / P4 / P1 / CASE 9 / #40 FindMod / #40 case-3 op-gate / issue #72 trial-execution /
    # issue #86 type-provider / --remove / --add: hold Change.xml at run A for BOTH runs so the
    # change vehicle's patch file does NOT change. The only between-run delta is mode-specific
    # (P2: a mod added before run B; P4: the gate mod removed; P1: the JoofTest.PropDef def file
    # swapped; CASE 9: TestMod_ThirdMod removed; #40 FindMod: the gate mod removed (same toggle as
    # P4); #40 case-3: the gate mod removed (same toggle as P4/FindMod); issue #72:
    # joof.testharness.trialexec added before run B, same mod-list-change shape as P2; issue #86:
    # joof.testharness.typeprovider removed before run B, same toggle as P4;
    # --remove/--add: a real mod removed/added before run B), so the dirty set is driven purely by
    # that channel rather than a patch-file edit.
    RUN_A_CHANGE="Change_RunA.xml"
    RUN_B_CHANGE="Change_RunA.xml"
else
    RUN_A_CHANGE="Change_RunA.xml"
    RUN_B_CHANGE="Change_RunB.xml"
fi

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
log()  { echo "[run_test] $*"; }

# Distinguishes "the candidate modlist itself is broken/incompatible" from "our code is the
# problem" by grepping Player.log for signatures that already exist and fire today but that
# nothing previously read: Gagarin's own duplicate-defName/Name= detector
# (DuplicateHelper.cs -> "GAGARIN:[DUPLICATE:CRITICAL]") and RimWorld's own fatal
# missing-parent/missing-type/missing-def load errors. Neither of these means our incremental-cache
# code is broken -- they mean the mod set can't be loaded at all, on any build. Falls back to
# "gagarin_bug" (main menu was reached, so startup itself is fine) or "unknown" (main menu was
# never reached and no known bad-modlist signature matched -- still worth a human look).
classify_failure() {
    if grep -q "GAGARIN:\[DUPLICATE:CRITICAL\]" "$PLAYER_LOG" 2>/dev/null; then
        echo "bad_modlist (duplicate defName/Name= conflict across mods -- see GAGARIN:[DUPLICATE:CRITICAL] in Player.log)"
        return
    fi
    if grep -qE "Could not find parent node named|Could not find type named|Could not find def named" "$PLAYER_LOG" 2>/dev/null; then
        echo "bad_modlist (missing dependency/type/def -- RimWorld's own load error in Player.log)"
        return
    fi
    if grep -q "GAGARIN: Main menu reached" "$PLAYER_LOG" 2>/dev/null; then
        echo "gagarin_bug (main menu was reached -- startup succeeded, our code is the suspect)"
        return
    fi
    echo "unknown (main menu never reached, no known bad-modlist signature matched -- inspect Player.log)"
}

fail() {
    echo "[run_test] FAIL: $*" >&2
    echo "[run_test] CLASSIFICATION: $(classify_failure)" >&2
    # Short grace window before cleanup's trap tears down RimWorld/symlinks/ModsConfig, so
    # there's a moment to skim Player.log / process state before it's gone.
    sleep 5
    exit 1
}

cleanup_done=0
cleanup() {
    if [[ $cleanup_done -eq 1 ]]; then return; fi
    cleanup_done=1
    log "Cleaning up..."
    # Kill any running RimWorldLinux (best effort)
    pkill -9 -x RimWorldLinux 2>/dev/null || true
    if [[ $NO_TEARDOWN -eq 0 ]]; then
        teardown
    else
        log "--no-teardown: leaving symlinks and ModsConfig in place."
    fi
}
trap cleanup EXIT INT TERM

teardown() {
    log "Restoring ModsConfig from backup..."
    if [[ -f "$MODSCONFIG_BAK" ]]; then
        cp "$MODSCONFIG_BAK" "$MODSCONFIG"
        log "ModsConfig restored."
    else
        log "Warning: no ModsConfig backup found at $MODSCONFIG_BAK"
    fi

    log "Restoring workshop Gagarin.dll from backup..."
    if [[ -f "$WORKSHOP_GAGARIN_BAK" ]]; then
        cp "$WORKSHOP_GAGARIN_BAK" "$WORKSHOP_GAGARIN_DLL"
        log "Workshop Gagarin.dll restored."
    else
        log "Warning: no Gagarin.dll backup found at $WORKSHOP_GAGARIN_BAK"
    fi

    log "Removing test-mod symlinks..."
    rm -f "$MODS_DIR/joof-testharness-defs"
    rm -f "$MODS_DIR/joof-testharness-static"
    rm -f "$MODS_DIR/joof-testharness-change"
    rm -f "$MODS_DIR/joof-testharness-added"
    rm -f "$MODS_DIR/joof-testharness-mayrequire"
    rm -f "$MODS_DIR/joof-testharness-gate"
    rm -f "$MODS_DIR/joof-testharness-p1"
    rm -f "$MODS_DIR/joof-testharness-genop"
    rm -f "$MODS_DIR/joof-testharness-rogueop"
    rm -f "$MODS_DIR/joof-testharness-thirdmod"
    rm -f "$MODS_DIR/joof-testharness-findmod"
    rm -f "$MODS_DIR/joof-testharness-opgate"
    rm -f "$MODS_DIR/joof-testharness-ownerbase"
    rm -f "$MODS_DIR/joof-testharness-owner"
    rm -f "$MODS_DIR/joof-testharness-trialexec"
    rm -f "$MODS_DIR/joof-testharness-typeprovider"
    rm -f "$MODS_DIR/joof-testharness-typeconsumer"
    log "Symlinks removed."

    # Reset the P1 def file to its committed Run A value so a --expect-p1 run doesn't leave the git
    # tree dirty (the harness overwrites it each run anyway).
    if [[ -f "$P1_MOD_DIR/P1Templates/P1Defs_RunA.xml" ]]; then
        cp "$P1_MOD_DIR/P1Templates/P1Defs_RunA.xml" "$P1_MOD_DIR/Defs/P1Defs.xml" 2>/dev/null || true
    fi

    # Same reset for the ownership fixture's def file (issue #50).
    if [[ -f "$OWNER_MOD_DIR/OwnerTemplates/OwnerDefs_RunA.xml" ]]; then
        cp "$OWNER_MOD_DIR/OwnerTemplates/OwnerDefs_RunA.xml" "$OWNER_MOD_DIR/Defs/OwnerDefs.xml" 2>/dev/null || true
    fi

    # Leave Change.xml in place (it's a test artifact; leaving it is harmless and useful for
    # inspecting the state after a run). Clean it up manually if desired.
}

# Launch RimWorldLinux with sandbox disabled, capture PID.
# Retries up to MAX_RETRIES times on the known intermittent early-startup SIGSEGV
# (the Boehm-GC crash in Prepatcher's prestarter GUI, ~1 in 4 launches). The crash
# signature is "Caught fatal signal - signo:11" landing in Player.log BEFORE any
# "GAGARIN:" line — i.e. the process died during native init, before our mod loaded.
# The "GC_mark_from" backtrace prints on STDERR, so we capture stderr to RIMWORLD_STDERR
# (not /dev/null) and grep BOTH it and Player.log for the crash signatures. An earlier
# version sent stderr to /dev/null and only grepped Player.log, so the most common flake
# signature (GC_mark_from on stderr) was invisible and every such crash hard-failed.
# Liveness check used everywhere instead of a bare `kill -0 "$RIMWORLD_PID"`. On some
# launches RimWorldLinux forks/detaches into a new PID partway through a long load
# (observed live: a heavy real-mod set that took long enough for the handoff to occur
# mid-wait) — the originally-captured $! then reports dead via kill -0 while the actual
# game is still alive and loading, producing a false "exited" FAIL. Treat any live
# process named RimWorldLinux as "still running", not just the one PID we happened to
# capture at launch time.
rimworld_alive() {
    pgrep -x RimWorldLinux >/dev/null 2>&1 || kill -0 "$RIMWORLD_PID" 2>/dev/null
}

RIMWORLD_STDERR="/tmp/rimworld_testharness_stderr.log"
launch_rimworld() {
    local max_retries=5
    local attempt=0

    while true; do
        attempt=$((attempt + 1))
        log "Launching RimWorld (attempt $attempt / $max_retries)..."

        # Safety net: a prior attempt's death-check can misfire (see below) and leave a
        # still-live RimWorldLinux behind while we launch another. Two instances racing on
        # the same Player.log/ModsConfig.xml/Cache dir silently corrupts the run and roughly
        # doubles memory pressure. Guarantee a clean slate before every attempt, including
        # the first (in case a previous invocation of this script was killed mid-run).
        if pgrep -x RimWorldLinux >/dev/null 2>&1; then
            log "Found a stray RimWorldLinux process — killing it before launching."
            pkill -9 -x RimWorldLinux 2>/dev/null || true
            sleep 2
        fi

        # Clear the player log + stderr capture so we get a fresh read each attempt.
        # RimWorld rewrites Player.log on launch, but there can be leftover content
        # from a previous launch that could confuse our grep.
        # We truncate rather than delete so the path always exists.
        : > "$PLAYER_LOG"
        : > "$RIMWORLD_STDERR"

        # --no-sandbox: avoids the Boehm-GC SIGSEGV in Prepatcher's prestarter GUI
        # that occurs ~1 in 4 launches when sandboxed. stderr -> file so we can detect the
        # GC_mark_from backtrace (which never reaches Player.log).
        "$RIMWORLD/RimWorldLinux" --no-sandbox \
            -logfile "$PLAYER_LOG" \
            2>"$RIMWORLD_STDERR" &
        RIMWORLD_PID=$!
        log "RimWorldLinux PID: $RIMWORLD_PID"

        # Poll for death every 5s across the 60s crash window instead of a single point-in-time
        # check — a single "sleep 60; kill -0" can race the exact moment a crashing process
        # finishes exiting, and if it misjudges "dead" while the process is actually still
        # alive, the next attempt launches a SECOND live instance alongside it (observed live:
        # two RimWorldLinux PIDs running simultaneously, fighting over the same log/config/cache
        # and doubling memory use). Polling shrinks that race window and also lets us exit early
        # once GAGARIN: proves the mod is up, instead of always waiting the full 60s.
        local waited=0
        while (( waited < 60 )); do
            if ! rimworld_alive; then
                break
            fi
            if grep -q "GAGARIN:" "$PLAYER_LOG" 2>/dev/null; then
                break
            fi
            sleep 5
            waited=$((waited + 5))
        done
        if ! rimworld_alive; then
            # Process already dead. If it died before our mod loaded ("GAGARIN:" absent), treat
            # it as the flaky early-startup crash and retry — whether or not a recognised
            # signature landed (the signature is on stderr/Player.log but a SIGSEGV during native
            # init can leave neither). A death AFTER "GAGARIN:" means our mod loaded and ran, so
            # it is a REAL crash we must surface, not retry.
            if ! grep -q "GAGARIN:" "$PLAYER_LOG" 2>/dev/null; then
                local sig=""
                if grep -q "Caught fatal signal\|GC_mark_from" "$PLAYER_LOG" "$RIMWORLD_STDERR" 2>/dev/null; then
                    sig=" (signature matched)"
                fi
                log "Died before mod load (attempt $attempt) — treating as flaky early-startup crash${sig}. Retrying..."
                if [[ $attempt -ge $max_retries ]]; then
                    fail "RimWorld died before mod load $max_retries times in a row. Giving up (check $PLAYER_LOG / $RIMWORLD_STDERR)."
                fi
                continue
            else
                fail "RimWorldLinux exited AFTER mod load (PID $RIMWORLD_PID) — real crash. Check $PLAYER_LOG."
            fi
        fi

        # Process still running — we survived the crash window.
        log "RimWorld running (survived 60s crash window)."
        return 0
    done
}

# Wait for a marker string to appear in Player.log, polling every 10 seconds.
# Gives up after TIMEOUT_SECONDS (default 600 = 10 minutes).
wait_for_marker() {
    local marker="$1"
    local label="${2:-$1}"
    local timeout_secs="${3:-600}"
    local elapsed=0

    log "Waiting for: $label"
    while true; do
        if ! rimworld_alive; then
            fail "RimWorldLinux exited before marker '$label' appeared. Check $PLAYER_LOG."
        fi
        if grep -q "$marker" "$PLAYER_LOG" 2>/dev/null; then
            log "Marker found: $label"
            return 0
        fi
        sleep 10
        elapsed=$((elapsed + 10))
        if [[ $elapsed -ge $timeout_secs ]]; then
            fail "Timed out after ${timeout_secs}s waiting for marker: $label"
        fi
        if (( elapsed % 60 == 0 )); then
            log "  ...still waiting (${elapsed}s elapsed)..."
        fi
    done
}

# Wait for a report/marker file to actually exist, tolerant of RimWorld's own
# ModsConfig.Reset() data-load recovery redoing the entire load in-process (see
# ModsConfig_Patch.cs). That retry can emit the SAME log marker more than once per launch,
# so a plain wait_for_marker call can match an earlier, superseded pass whose file never
# gets written before the folder is wiped again. First does a plain poll for the file; if
# that times out and a retry was detected in the log, waits for a LATER occurrence of the
# marker (by count, not just re-grep, since re-grep would just re-match the same old line)
# before polling again. No-ops (returns success) if the file already showed up, or if there
# was no retry — the caller's own post-call fail-check handles a genuine miss.
wait_for_file_after_marker() {
    local report_file="$1"
    local marker="$2"
    local label="$3"
    local wait_budget="${4:-300}"
    local retry_budget="${5:-1800}"
    local elapsed=0

    while [[ ! -f "$report_file" && $elapsed -lt $wait_budget ]]; do
        sleep 5
        elapsed=$((elapsed + 5))
    done
    if [[ -f "$report_file" ]]; then
        return 0
    fi
    if ! grep -q "Removed cache to recover from error" "$PLAYER_LOG" 2>/dev/null; then
        return 0
    fi

    log "$report_file still missing after ${wait_budget}s and a mid-load retry was detected — waiting for a later '$label'..."
    local prior_count current_count retry_elapsed=0
    prior_count=$(grep -c "$marker" "$PLAYER_LOG" 2>/dev/null || echo 0)
    while true; do
        if ! rimworld_alive; then
            fail "RimWorldLinux exited before a post-retry '$label' completed. Check $PLAYER_LOG."
        fi
        current_count=$(grep -c "$marker" "$PLAYER_LOG" 2>/dev/null || echo 0)
        if [[ -f "$report_file" && "$current_count" -gt "$prior_count" ]]; then
            log "Marker found: $label (post-retry)"
            return 0
        fi
        sleep 10
        retry_elapsed=$((retry_elapsed + 10))
        if [[ $retry_elapsed -ge $retry_budget ]]; then
            fail "Timed out after ${retry_budget}s waiting for a post-retry '$label'. Check $PLAYER_LOG."
        fi
    done
}

kill_rimworld() {
    if [[ -n "${RIMWORLD_PID:-}" ]] && rimworld_alive; then
        log "Killing RimWorldLinux (PID $RIMWORLD_PID)..."
        pkill -9 -x RimWorldLinux 2>/dev/null || kill -9 "$RIMWORLD_PID" 2>/dev/null || true
        wait "$RIMWORLD_PID" 2>/dev/null || true
        RIMWORLD_PID=""
    fi
}

parse_gate_report() {
    # Minimal JSON parse: look for "pass":true or "nonDirtyMismatches":0.
    # We avoid requiring jq — use python3 which is always available on Steam Deck.
    python3 - "$GATE_REPORT" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    passed = data.get("pass", False)
    mismatches = data.get("nonDirtyMismatches", -1)
    dirty = data.get("dirtyCount", "?")
    baseline = data.get("baselineDefs", "?")
    rebuild = data.get("rebuildDefs", "?")
    gate_ms = data.get("gateMs", "?")
    print(f"  pass={passed}  nonDirtyMismatches={mismatches}  dirtyCount={dirty}  baselineDefs={baseline}  rebuildDefs={rebuild}  gateMs={gate_ms}ms")
    sys.exit(0 if passed and mismatches == 0 else 1)
except Exception as e:
    print(f"  ERROR parsing GateReport.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_recompute_report() {
    # The recompute gate verdict. Two accepted shapes, selected by EXPECT_FALLBACK:
    #   default (EXPECT_FALLBACK=0): require a REAL recompute — fallback==false, zero
    #     recompute mismatches, pass==true. The default test mods keep all container ops in
    #     the UNCHANGED static mod, so a fallback here is itself a failure of the premise.
    #   --expect-fallback (EXPECT_FALLBACK=1): require the CHANGED-mod container-op fallback —
    #     fallback==true and pass==true. No real recompute runs (the authoritative full rebuild
    #     already did), so recomputeMismatches is 0 and contextCount is 0 by construction.
    # EXPECT_FALLBACK is exported so the python child sees it.
    EXPECT_FALLBACK="$EXPECT_FALLBACK" python3 - "$RECOMPUTE_REPORT" <<'PYEOF'
import os, sys, json
try:
    expect_fallback = os.environ.get("EXPECT_FALLBACK", "0") == "1"
    data = json.load(open(sys.argv[1]))
    passed = data.get("pass", False)
    fallback = data.get("fallback", True)
    mismatches = data.get("recomputeMismatches", -1)
    ctx = data.get("contextCount", "?")
    subdoc = data.get("subDocSize", "?")
    dirty = data.get("dirtyCount", "?")
    ms = data.get("recomputeMs", "?")
    reason = data.get("fallbackReason", None)
    print(f"  pass={passed}  fallback={fallback}  recomputeMismatches={mismatches}  contextCount={ctx}  subDocSize={subdoc}  dirtyCount={dirty}  recomputeMs={ms}ms")
    if reason:
        print(f"  fallbackReason={reason}")
    if expect_fallback:
        ok = passed and fallback and (mismatches == 0)
    else:
        ok = passed and (mismatches == 0) and (not fallback)
    sys.exit(0 if ok else 1)
except Exception as e:
    print(f"  ERROR parsing RecomputeReport.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_recompute_gap() {
    # --expect-recompute-gap only: assert the RECOMPUTE ALLOWLIST declined the cross-def conditional
    # gap and fell back, rather than silently serving the wrong value. PASS =
    #   fallback==true && pass==true && recomputeMismatches==0   (allowlist declined; full rebuild stands)
    # AND the fallbackReason names a (cross-def) conditional (so it is the allowlist's conditional
    # clause, not the changed-mod container-op fallback or anything else). Before the allowlist this
    # mode instead asserted the gap REPRODUCED (fallback==false && recomputeMismatches>0); a regression
    # in either the fixture or the allowlist now surfaces here — a broken allowlist lets the recompute
    # run and mismatch (fallback!=true => FAIL); a broken fixture yields a clean recompute with no
    # fallback (fallback!=true => FAIL).
    python3 - "$RECOMPUTE_REPORT" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    passed = data.get("pass", False)
    fallback = data.get("fallback", False)
    mismatches = data.get("recomputeMismatches", -1)
    reason = data.get("fallbackReason", None) or ""
    print(f"  pass={passed}  fallback={fallback}  recomputeMismatches={mismatches}")
    print(f"  fallbackReason={reason}")
    ok = passed and fallback and (mismatches == 0) and ("conditional" in reason.lower())
    sys.exit(0 if ok else 1)
except Exception as e:
    print(f"  ERROR parsing RecomputeReport.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_recompute_positional() {
    # --expect-scope-escape only: assert the RECOMPUTE ALLOWLIST declined the sibling-axis scope
    # escape as positional-xpath (the structural per-step parser catching what the old
    # string-position heuristic would have missed), rather than silently admitting a recompute that
    # served the wrong def's value. PASS = fallback==true && pass==true && recomputeMismatches==0,
    # AND the fallbackReason names "positional" (so it is specifically the positional-xpath clause,
    # not some other decline category).
    python3 - "$RECOMPUTE_REPORT" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    passed = data.get("pass", False)
    fallback = data.get("fallback", False)
    mismatches = data.get("recomputeMismatches", -1)
    reason = data.get("fallbackReason", None) or ""
    print(f"  pass={passed}  fallback={fallback}  recomputeMismatches={mismatches}")
    print(f"  fallbackReason={reason}")
    ok = passed and fallback and (mismatches == 0) and ("positional" in reason.lower())
    sys.exit(0 if ok else 1)
except Exception as e:
    print(f"  ERROR parsing RecomputeReport.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_recompute_rogueop() {
    # --expect-rogue-op only: assert the RECOMPUTE ALLOWLIST declined the bare-name-colliding
    # RogueMod.PatchOperationAdd as unknown-op-kind (the fully-qualified SafeLeafOps fix), rather than
    # wrongly trusting it as the real, proven-safe Verse.PatchOperationAdd. PASS = fallback==true &&
    # pass==true && recomputeMismatches==0, AND the fallbackReason names the fully-qualified
    # "roguemod.patchoperationadd" (proving the captured/checked type string is qualified, not the
    # bare "PatchOperationAdd" that would collide).
    python3 - "$RECOMPUTE_REPORT" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    passed = data.get("pass", False)
    fallback = data.get("fallback", False)
    mismatches = data.get("recomputeMismatches", -1)
    reason = data.get("fallbackReason", None) or ""
    print(f"  pass={passed}  fallback={fallback}  recomputeMismatches={mismatches}")
    print(f"  fallbackReason={reason}")
    ok = passed and fallback and (mismatches == 0) and ("roguemod.patchoperationadd" in reason.lower())
    sys.exit(0 if ok else 1)
except Exception as e:
    print(f"  ERROR parsing RecomputeReport.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_dependency_graph_edge() {
    # --expect-nested-in-sequence only: direct proof that CAPTURE (not just the allowlist's
    # decision, and not just an inferred fallbackReason string) recorded an edge for the
    # sequence-nested conditional. Reads the just-recaptured Run B DependencyGraph.json (a changed
    # load always triggers a full rebuild + recapture — see CLAUDE.md "Graph self-heals on a
    # changed load") and asserts a patchEdge exists whose patchId ends in the given suffix with a
    # non-empty matchedNodeIds (its read set on TC_SeqNestedProbe).
    local id_suffix="$1"
    python3 - "$CACHE_DIR/DependencyGraph.json" "$id_suffix" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    suffix = sys.argv[2]
    edges = data.get("patchEdges", [])
    match = next((e for e in edges if (e.get("patchId") or "").endswith(suffix)), None)
    if match is None:
        print(f"  No patchEdge found with patchId ending in '{suffix}' ({len(edges)} edges total)")
        sys.exit(1)
    matched = match.get("matchedNodeIds") or []
    print(f"  Found patchEdge patchId={match.get('patchId')} operationType={match.get('operationType')} matchedNodeIds={matched}")
    sys.exit(0 if len(matched) > 0 else 1)
except Exception as e:
    print(f"  ERROR parsing DependencyGraph.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_dirtyset_added() {
    # --expect-added (P2) only: assert the added-defs channel actually fired this run, i.e. the
    # diagnostic seeded one or more brand-new concrete defs (seeds.addedDefs > 0 in DirtySet.json).
    # A zero here would mean run B did not observe the new mod's defs as added (the whole premise
    # of the run), even if the gates happened to pass.
    python3 - "$DIRTYSET_REPORT" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    seeds = data.get("seeds", {})
    added = seeds.get("addedDefs", 0)
    dirty = data.get("dirtyCount", "?")
    print(f"  seeds.addedDefs={added}  dirtyCount={dirty}")
    sys.exit(0 if added > 0 else 1)
except Exception as e:
    print(f"  ERROR parsing DirtySet.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_dirtyset_mayrequire() {
    # --expect-mayrequire (P4) only: assert the MayRequire channel actually fired, i.e. the
    # diagnostic seeded one or more defs via the MayRequire flip (seeds.mayRequire > 0 in
    # DirtySet.json). A zero would mean Seed 6 did not dirty the gated defs even though the gate
    # mod left the load — the exact silent-staleness this PR closes — so the gate would only pass
    # by luck. We expect 2 here (TC_MR_Gated root-gated + TC_MR_Host patch-injected li).
    python3 - "$DIRTYSET_REPORT" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    seeds = data.get("seeds", {})
    mr = seeds.get("mayRequire", 0)
    dirty = data.get("dirtyCount", "?")
    print(f"  seeds.mayRequire={mr}  dirtyCount={dirty}")
    sys.exit(0 if mr > 0 else 1)
except Exception as e:
    print(f"  ERROR parsing DirtySet.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_dirtyset_p1() {
    # --expect-p1 only: assert the changed namespaced def was dirtied under its ELEMENT-NAME node id
    # "JoofTest.PropDef/TC_P1_Prop". This is the direct proof of the P1 fix: the old code keyed the
    # node by GetType().Name ("PropDef/TC_P1_Prop"), which the gate/Unified never look up. Finding the
    # element-name id in the dirty set means RegisterNode keyed it correctly.
    python3 - "$DIRTYSET_REPORT" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    ids = data.get("dirtyNodeIds", []) or []
    want = "JoofTest.PropDef/TC_P1_Prop"
    wrong = "PropDef/TC_P1_Prop"
    has_want = want in ids
    has_wrong = any(i == wrong for i in ids)  # the old, mis-keyed id (should NOT appear)
    print(f"  dirty has '{want}': {has_want}  (legacy-mis-keyed '{wrong}': {has_wrong})")
    sys.exit(0 if has_want else 1)
except Exception as e:
    print(f"  ERROR parsing DirtySet.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_dirtyset_ownership() {
    # --expect-ownership only (issue #50): assert TC_Ownership_Target was dirtied AND the
    # defOverrideRematch seed fired. Neither modlist changes between runs (no add/remove seed can
    # explain it), so a nonzero defOverrideRematch count is the direct proof this went through
    # ComputeDefOverrideFlips's candidate set, not some other seed.
    python3 - "$DIRTYSET_REPORT" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    ids = data.get("dirtyNodeIds", []) or []
    seeds = data.get("seeds", {})
    rematch = seeds.get("defOverrideRematch", 0)
    want = "ThingDef/TC_Ownership_Target"
    has_want = want in ids
    print(f"  dirty has '{want}': {has_want}  seeds.defOverrideRematch={rematch}")
    sys.exit(0 if (has_want and rematch > 0) else 1)
except Exception as e:
    print(f"  ERROR parsing DirtySet.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

parse_dirtyset_typeprovider() {
    # --expect-typeprovider (issue #86) only: assert the type-provider channel actually fired, i.e.
    # Seed 9 dirtied one or more of joof.testharness.typeconsumer's JoofTest.GadgetDef instances via
    # the TypeProviderIndex XOR (seeds.typeProvider > 0 in DirtySet.json). A zero would mean the
    # capture side never recorded joof.testharness.typeprovider as the assembly owner of those defs
    # — the exact silent-staleness this PR closes — so the gate would only pass by luck. We expect 3
    # here (TC_TypeConsumer_A/B/C).
    python3 - "$DIRTYSET_REPORT" <<'PYEOF'
import sys, json
try:
    data = json.load(open(sys.argv[1]))
    seeds = data.get("seeds", {})
    tp = seeds.get("typeProvider", 0)
    dirty = data.get("dirtyCount", "?")
    print(f"  seeds.typeProvider={tp}  dirtyCount={dirty}")
    sys.exit(0 if tp > 0 else 1)
except Exception as e:
    print(f"  ERROR parsing DirtySet.json: {e}", file=sys.stderr)
    sys.exit(2)
PYEOF
}

# ---------------------------------------------------------------------------
# Pre-flight: every test-mod XML must be well-formed BEFORE we launch.
# ---------------------------------------------------------------------------
# A malformed asset under a mod's Patches/ (the classic trap: a literal "--" inside an XML
# <!-- comment -->, illegal per the XML spec) throws in RimWorld's LoadableXmlAsset..ctor, NREs
# Gagarin's constructor postfix, and collapses the whole cold load to a black screen — but the
# symptom is only a 600s timeout waiting for "Provenance captured" plus a misleading downstream
# Cache/AssetsHash.xml DirectoryNotFound. Catch it here in <1s instead of after a 10-minute hang.
# Lints the ChangeTemplates too (they are copied into Patches/Change.xml per run).
log "--- Pre-flight: validating test-mod XML is well-formed ---"
xml_lint_failed=0
while IFS= read -r xmlf; do
    if ! python3 -c "import sys,xml.dom.minidom; xml.dom.minidom.parse(sys.argv[1])" "$xmlf" 2>/tmp/xmllint.$$; then
        echo "[run_test] FAIL: malformed XML: $xmlf" >&2
        sed 's/^/[run_test]   /' /tmp/xmllint.$$ >&2
        xml_lint_failed=1
    fi
# Exclude TestMod_Change/Patches/Change.xml: it is a per-run generated artifact (overwritten from a
# ChangeTemplates/ file in Step 2, AFTER this lint), so a stale copy from a prior failed run must not
# fail pre-flight. The templates it is copied from ARE linted below, which is what matters.
done < <(find "$SCRIPT_DIR"/TestMod_* -name '*.xml' \
            -not -path "$CHANGE_MOD_DIR/Patches/Change.xml" 2>/dev/null)
rm -f /tmp/xmllint.$$
if [[ $xml_lint_failed -eq 1 ]]; then
    fail "test-mod XML is malformed (see above). Common cause: a literal '--' inside an XML comment."
fi
log "Pre-flight XML lint passed."

# ---------------------------------------------------------------------------
# Step 0: ensure test-mod symlinks exist
# ---------------------------------------------------------------------------
log "--- Step 0: setting up test-mod symlinks ---"
ln -sfn "$DEFS_MOD_DIR"   "$MODS_DIR/joof-testharness-defs"
ln -sfn "$STATIC_MOD_DIR" "$MODS_DIR/joof-testharness-static"
ln -sfn "$CHANGE_MOD_DIR" "$MODS_DIR/joof-testharness-change"
# The added mod (P2) is symlinked unconditionally so RimWorld can resolve it once run B inserts it
# into ModsConfig. In the default / fallback runs it is symlinked but never activated, so it is
# inert. Only --expect-added adds its packageId to ModsConfig (and only before run B).
ln -sfn "$ADDED_MOD_DIR"  "$MODS_DIR/joof-testharness-added"
# The MayRequire fixture (P4) and its gate mod are symlinked unconditionally so RimWorld can resolve
# them when --expect-mayrequire activates them in ModsConfig. In other modes they are symlinked but
# never added to ModsConfig, so they are inert.
ln -sfn "$MAYREQUIRE_MOD_DIR" "$MODS_DIR/joof-testharness-mayrequire"
ln -sfn "$GATE_MOD_DIR"       "$MODS_DIR/joof-testharness-gate"
# The P1 fixture (custom namespaced-def assembly) is symlinked unconditionally; only --expect-p1
# activates it in ModsConfig.
ln -sfn "$P1_MOD_DIR"         "$MODS_DIR/joof-testharness-p1"
# The generated-op fixture (custom PatchOperation assembly) is in the BASE modlist (below): it is
# inert in every mode (TC_GenTarget is never dirtied), but on every capture it generates a child op
# at Apply time so capture's apply-time attribution is continuously exercised — its edge must record
# as joof.testharness.genop#N.generated[0], never unindexed#.
ln -sfn "$GENOP_MOD_DIR"      "$MODS_DIR/joof-testharness-genop"
# The rogue-op fixture (CASE 13, custom PatchOperation assembly whose class bare-name-collides with
# Verse.PatchOperationAdd) is in the BASE modlist (below): its op always fires on TC_RogueOp_Target;
# only --expect-rogue-op dirties that def so its producing edge is evaluated by the allowlist.
ln -sfn "$ROGUEOP_MOD_DIR"    "$MODS_DIR/joof-testharness-rogueop"
# The third-mod fixture (CASE 9) is symlinked unconditionally; only --expect-conditional-thirdmod
# activates it in ModsConfig for run A (and removes it before run B).
ln -sfn "$THIRDMOD_MOD_DIR"   "$MODS_DIR/joof-testharness-thirdmod"
# The FindMod fixture (#40) is symlinked unconditionally; only --expect-findmod activates it (and the
# shared gate mod) in ModsConfig for run A.
ln -sfn "$FINDMOD_MOD_DIR"    "$MODS_DIR/joof-testharness-findmod"
# The op-gate fixture (#40 case 3) is symlinked unconditionally; only --expect-opgate activates it
# (and the shared gate mod) in ModsConfig for run A.
ln -sfn "$OPGATE_MOD_DIR"     "$MODS_DIR/joof-testharness-opgate"
# The ownership fixture (#50) is symlinked unconditionally; both mods are ALWAYS in ModsConfig (never
# added/removed) in --expect-ownership -- the whole point is zero mod-list delta between runs.
ln -sfn "$OWNERBASE_MOD_DIR" "$MODS_DIR/joof-testharness-ownerbase"
ln -sfn "$OWNER_MOD_DIR"     "$MODS_DIR/joof-testharness-owner"
# The trial-execution fixture (issue #72) is symlinked unconditionally, same convention as the
# added-defs fixture: inert (never in ModsConfig) unless --expect-trial-execution activates it, and
# only before run B (the genuine mod-list change that drives both of SubDocExpander's declines).
ln -sfn "$TRIALEXEC_MOD_DIR" "$MODS_DIR/joof-testharness-trialexec"
# The type-provider fixture (issue #86) is symlinked unconditionally; only --expect-typeprovider
# activates both mods for run A, then removes joof.testharness.typeprovider (only) before run B --
# joof.testharness.typeconsumer stays active in both runs, unchanged.
ln -sfn "$TYPEPROVIDER_MOD_DIR" "$MODS_DIR/joof-testharness-typeprovider"
ln -sfn "$TYPECONSUMER_MOD_DIR" "$MODS_DIR/joof-testharness-typeconsumer"
log "Symlinks created:"
ls -la "$MODS_DIR/joof-testharness-"* 2>/dev/null || true

# ---------------------------------------------------------------------------
# Step 0b: deploy the freshly-built dev Gagarin.dll over the workshop copy
# ---------------------------------------------------------------------------
log "--- Step 0b: deploying dev Gagarin.dll ---"
[[ -f "$DEV_GAGARIN_DLL" ]] || fail "dev Gagarin.dll not found at $DEV_GAGARIN_DLL — build it first (see header)."
[[ -f "$WORKSHOP_GAGARIN_DLL" ]] || fail "workshop Gagarin.dll not found at $WORKSHOP_GAGARIN_DLL — is vr.missilegirl subscribed?"
cp "$WORKSHOP_GAGARIN_DLL" "$WORKSHOP_GAGARIN_BAK"
log "Workshop Gagarin.dll backed up to $WORKSHOP_GAGARIN_BAK ($(stat -c%s "$WORKSHOP_GAGARIN_BAK") bytes)."
cp "$DEV_GAGARIN_DLL" "$WORKSHOP_GAGARIN_DLL"
log "Deployed dev Gagarin.dll ($(stat -c%s "$DEV_GAGARIN_DLL") bytes) → workshop."

# write_modsconfig TARGET_MODLIST_FILE TARGET_MODLIST_VERBATIM LABEL
# Writes $MODSCONFIG from scratch: Core + installed DLCs + Harmony + Gagarin + (the given modlist
# file, on top) + test-harness mods — or, if a verbatim file is given, that file's own order as-is
# (still must contain ludeon.rimworld + vr.missilegirl). Shared by Step 1 (run A) and, when
# --modlist-b=/--modlist-verbatim-b= is given, Step 4 (run B) — so run B gets its own fully-explicit
# modlist instead of --remove=/--add= regex surgery on whatever run A's ModsConfig looks like by
# then (which can differ from what we wrote, e.g. after an engine-side ModsConfig.Reset()).
write_modsconfig() {
    local target_modlist_file="$1"
    local target_modlist_verbatim="$2"
    local label="$3"
    log "--- writing ModsConfig.xml for $label ---"
EXPECT_ADDED="$EXPECT_ADDED" EXPECT_MAYREQUIRE="$EXPECT_MAYREQUIRE" EXPECT_P1="$EXPECT_P1" \
EXPECT_THIRDMOD="$EXPECT_THIRDMOD" EXPECT_FINDMOD="$EXPECT_FINDMOD" EXPECT_OPGATE="$EXPECT_OPGATE" \
EXPECT_OWNERSHIP="$EXPECT_OWNERSHIP" EXPECT_TYPEPROVIDER="$EXPECT_TYPEPROVIDER" \
MODLIST_FILE="$target_modlist_file" MODLIST_VERBATIM="$target_modlist_verbatim" MAX_MODS="$MAX_MODS" \
RIMWORLD="$RIMWORLD" python3 - "$MODSCONFIG" <<'PYEOF'
import os, sys, re

path = sys.argv[1]
max_mods = int(os.environ.get("MAX_MODS", "200"))

# Preserve <version> + <knownExpansions> from the existing file; replace <activeMods> wholesale.
orig = open(path, encoding="utf-8").read()
m = re.search(r"<version>(.*?)</version>", orig, re.S)
version = m.group(1).strip() if m else ""
m = re.search(r"<knownExpansions>.*?</knownExpansions>", orig, re.S)
known = m.group(0) if m else "<knownExpansions />"

# Only list official DLCs actually installed (Data/<Name> present), in canonical load order.
DLC = [
    ("Royalty",  "ludeon.rimworld.royalty"),
    ("Ideology", "ludeon.rimworld.ideology"),
    ("Biotech",  "ludeon.rimworld.biotech"),
    ("Anomaly",  "ludeon.rimworld.anomaly"),
    ("Odyssey",  "ludeon.rimworld.odyssey"),
]
data_dir = os.path.join(os.environ["RIMWORLD"], "Data")
dlc_ids = [pid for (name, pid) in DLC if os.path.isdir(os.path.join(data_dir, name))]

verbatim_file = os.environ.get("MODLIST_VERBATIM", "")
extra_file = os.environ.get("MODLIST_FILE", "")
if verbatim_file:
    # The file's own order IS the load order (e.g. a RimSort export) — no Core/DLC/Harmony
    # prefix is injected, since that would reorder load-order-sensitive mods placed around Core.
    active = []
    with open(verbatim_file, encoding="utf-8") as fh:
        for line in fh:
            pid = line.split("#", 1)[0].strip()
            if pid and pid not in active:
                active.append(pid)
    for required in ("ludeon.rimworld", "vr.missilegirl"):
        if required not in active:
            print(f"  ERROR: --modlist-verbatim file is missing required '{required}'.",
                  file=sys.stderr)
            sys.exit(3)
else:
    # Base load order: Core, DLCs, Harmony, then Gagarin (the mod under test).
    active = ["ludeon.rimworld"] + dlc_ids + ["brrainz.harmony", "vr.missilegirl"]

    # Optional captured problem set, on top of the base, before the test mods.
    if extra_file:
        with open(extra_file, encoding="utf-8") as fh:
            for line in fh:
                pid = line.split("#", 1)[0].strip()
                if pid and pid not in active:
                    active.append(pid)

# Test-harness mods always load last, in dependency order.
#   defs -> static -> change. The added mod (P2) is inserted before run B, not here.
#   --expect-mayrequire: gate + mayrequire are active for run A; gate is removed before run B.
test_mods = ["joof.testharness.defs", "joof.testharness.static", "joof.testharness.change", "joof.testharness.genop", "joof.testharness.rogueop"]
if os.environ.get("EXPECT_MAYREQUIRE", "0") == "1":
    test_mods += ["joof.testharness.gate", "joof.testharness.mayrequire"]
if os.environ.get("EXPECT_P1", "0") == "1":
    test_mods += ["joof.testharness.p1"]
# CASE 9 (--expect-conditional-thirdmod): the third mod is active for run A only — removed before
# run B in Step 4 below (same present-then-removed pattern as the gate mod for P4).
if os.environ.get("EXPECT_THIRDMOD", "0") == "1":
    test_mods += ["joof.testharness.thirdmod"]
# --expect-findmod (#40): gate + findmod are active for run A; gate is removed before run B (same
# toggle shape as --expect-mayrequire, reusing the same gate mod).
if os.environ.get("EXPECT_FINDMOD", "0") == "1":
    test_mods += ["joof.testharness.gate", "joof.testharness.findmod"]
# --expect-opgate (#40 case 3): gate + opgate are active for run A; gate is removed before run B
# (same toggle shape as --expect-mayrequire/--expect-findmod, reusing the same gate mod).
if os.environ.get("EXPECT_OPGATE", "0") == "1":
    test_mods += ["joof.testharness.gate", "joof.testharness.opgate"]
# --expect-ownership (#50): both mods are active in BOTH runs -- no add/remove at all, unlike every
# other EXPECT_* mode above. ownerbase must precede owner so owner's Run B declaration wins.
if os.environ.get("EXPECT_OWNERSHIP", "0") == "1":
    test_mods += ["joof.testharness.ownerbase", "joof.testharness.owner"]
# --expect-typeprovider (#86): typeprovider (ships the JoofTest.GadgetDef C# type) + typeconsumer
# (declares unpatched instances of it) are both active for run A. typeprovider is removed before
# run B in Step 4 below (same present-then-removed pattern as the gate mod for P4/#40);
# typeconsumer stays active, unchanged, in both runs.
if os.environ.get("EXPECT_TYPEPROVIDER", "0") == "1":
    test_mods += ["joof.testharness.typeprovider", "joof.testharness.typeconsumer"]
active += test_mods

if len(active) > max_mods:
    print(f"  ERROR: {len(active)} mods exceeds the {max_mods}-mod cap (trim --modlist).",
          file=sys.stderr)
    sys.exit(3)

lis = "\n".join(f"    <li>{pid}</li>" for pid in active)
out = (
    '<?xml version="1.0" encoding="utf-8"?>\n'
    "<ModsConfigData>\n"
    f"  <version>{version}</version>\n"
    f"  <activeMods>\n{lis}\n  </activeMods>\n"
    f"  {known}\n"
    "</ModsConfigData>\n"
)
open(path, "w", encoding="utf-8").write(out)
print(f"  minimal modlist written: {len(active)} mods "
      f"(Core + {len(dlc_ids)} DLC + Harmony + Gagarin + {len(test_mods)} test"
      + (f" + {len(active)-len(test_mods)-len(dlc_ids)-3} from --modlist" if extra_file else "")
      + ")")
for pid in active:
    print(f"    <li>{pid}</li>")
PYEOF
    local mc_rc=$?
    if [[ $mc_rc -ne 0 ]]; then
        fail "Failed to write ModsConfig for $label (rc=$mc_rc)."
    fi
}

# ---------------------------------------------------------------------------
# Step 1: write a fresh MINIMAL ModsConfig.xml (self-contained — never the ambient list)
# ---------------------------------------------------------------------------
# The harness ALWAYS loads a small, deterministic modlist: Core + official DLCs + Harmony + Gagarin
# (vr.missilegirl, under test) + the test-harness mods. It does NOT inherit the user's active mods —
# an 815-mod ambient list crashes RimWorld's Boehm GC during unrelated mod init (before any of our
# code runs), which is a flaky non-result. The user's real ModsConfig is backed up and restored on
# teardown, so their setup is untouched. An optional --modlist=FILE adds a captured problem set on
# top, hard-capped at $MAX_MODS total.
log "--- Step 1: writing minimal ModsConfig.xml ---"
cp "$MODSCONFIG" "$MODSCONFIG_BAK"
log "Backup saved to $MODSCONFIG_BAK"

# In --expect-added mode the added mod is deliberately NOT activated for run A — its defs must be
# absent from run A's baseline graph so run B sees them as genuinely added.
write_modsconfig "$MODLIST_FILE" "$MODLIST_VERBATIM" "Run A"

# Archive the exact active modlist NOW (before launching), so even a crashing run leaves behind the
# list that produced it — the reproducible record the user asked for.
mkdir -p "$METRICS_DIR"
MODLIST_ARCHIVE="$METRICS_DIR/livetest-${RUN_TS}-modlist.xml"
cp "$MODSCONFIG" "$MODLIST_ARCHIVE"
log "Archived active modlist (Run A) → $MODLIST_ARCHIVE"

# ---------------------------------------------------------------------------
# Step 2: prepare Run A (cold baseline + provenance capture)
# ---------------------------------------------------------------------------
log "--- Step 2: preparing Run A (cold baseline) ---"

# Clear the MissileGirl cache so Run A is a guaranteed full cold rebuild.
# The cold rebuild writes DependencyGraph.json which Run B's diagnostic needs.
log "Clearing MissileGirl cache..."
rm -rf "$CACHE_DIR"
mkdir -p "$CACHE_DIR"
# Also wipe the prior-state sidecar (.../MissileGirl/Incremental/) so a stale snapshot from an
# earlier run cannot be read as Run A's prior. Run A re-captures it; Run B reads Run A's copy.
rm -rf "$(dirname "$CACHE_DIR")/Incremental"

# Copy Run A patches into the active Change.xml.
# Default Run A has a narrow xpath (only matches TC_Wildcard_A by defName); the --expect-fallback
# variant instead gives the change mod a PatchOperationSequence (captured into the baseline graph).
log "Setting Change.xml to Run A ($RUN_A_CHANGE)..."
# Templates live in ChangeTemplates/ (NOT Patches/) so RimWorld only ever auto-loads the active
# Patches/Change.xml — keeping every other Change_* file out of the load.
cp "$CHANGE_MOD_DIR/ChangeTemplates/$RUN_A_CHANGE" "$CHANGE_MOD_DIR/Patches/Change.xml"

# --expect-p1: set the JoofTest.PropDef def file to its Run A value before the cold capture.
if [[ $EXPECT_P1 -eq 1 ]]; then
    log "Setting P1Defs.xml to Run A (p1Tag=run-a)..."
    cp "$P1_MOD_DIR/P1Templates/P1Defs_RunA.xml" "$P1_MOD_DIR/Defs/P1Defs.xml"
fi

# --expect-ownership (#50): set OwnerDefs.xml to its Run A value (no TC_Ownership_Target
# declaration) before the cold capture, so the baseline graph attributes the def solely to
# joof.testharness.ownerbase.
if [[ $EXPECT_OWNERSHIP -eq 1 ]]; then
    log "Setting OwnerDefs.xml to Run A (no TC_Ownership_Target)..."
    cp "$OWNER_MOD_DIR/OwnerTemplates/OwnerDefs_RunA.xml" "$OWNER_MOD_DIR/Defs/OwnerDefs.xml"
fi

# ---------------------------------------------------------------------------
# Step 3: Run A — cold load, capture provenance
# ---------------------------------------------------------------------------
log "--- Step 3: launching Run A ---"
RIMWORLD_PID=""
launch_rimworld

# Wait for the main menu first: RimWorld queues all mod/def loading strictly BEFORE
# UIRoot_Entry.Init() (see UIRoot_Entry_Patch.cs), so this marker means startup itself
# succeeded -- a hang/crash before this point means the candidate modlist is broken, not our
# code. A shorter budget than the marker below: a load heavy enough to reach the main menu at
# all reaches it well within 15 minutes even for a large candidate set.
wait_for_marker "GAGARIN: Main menu reached" "Main menu reached (Run A)" 900

# Wait for ProvenanceRecorder to finish (writes DependencyGraph.json, logs the marker).
# This is the end of the cold load. 10-minute timeout; a normal cold load is ~4 min.
wait_for_marker "Provenance captured" "Provenance captured (Run A done)" 1800
wait_for_file_after_marker "$CACHE_DIR/DependencyGraph.json" "Provenance captured" "Provenance captured (Run A done)"

log "Run A complete. Killing RimWorldLinux..."
kill_rimworld

# Verify the graph was actually written.
if [[ ! -f "$CACHE_DIR/DependencyGraph.json" ]]; then
    fail "DependencyGraph.json not found after Run A. Capture may have failed."
fi
log "DependencyGraph.json written ($(du -sh "$CACHE_DIR/DependencyGraph.json" | cut -f1))."

# Archive Run A's graph as the PRIOR graph for offline analysis. This is the exact graph the
# recompute gate consults during Run B (via the sidecar snapshot taken at the end of Run A), and
# the one a production incremental load reads. Run B's full rebuild OVERWRITES CACHE_DIR's graph
# (the self-heal), so this is the only window to capture it. The recompute-fidelity safety
# predictor must score recall against THIS graph (the removed mod is still present here), not the
# post-change runB graph the rest of Step 7 archives.
mkdir -p "$METRICS_DIR"
cp "$CACHE_DIR/DependencyGraph.json" "$METRICS_DIR/livetest-runA-${RUN_TS}-DependencyGraph.json"
log "Archived Run A (prior) graph → $METRICS_DIR/livetest-runA-${RUN_TS}-DependencyGraph.json"
# Also snapshot Run A's own Unified.xml here, before Run B's mod-list change overwrites it and
# before PriorStateSnapshot's sidecar gets refreshed past this point — that sidecar is volatile
# (it gets rewritten again on the next load, so reading it AFTER Run B for a post-hoc def diff
# shows Run B vs itself, not Run A vs Run B). This copy is the only stable "before" reference.
if [[ -f "$CACHE_DIR/Unified.xml" ]]; then
    cp "$CACHE_DIR/Unified.xml" "$METRICS_DIR/livetest-runA-${RUN_TS}-Unified.xml"
    log "Archived Run A Unified.xml → $METRICS_DIR/livetest-runA-${RUN_TS}-Unified.xml"
fi

# ---------------------------------------------------------------------------
# Step 4: prepare Run B (changed patch → cache miss → gate)
# ---------------------------------------------------------------------------
log "--- Step 4: preparing Run B (wide predicates — triggers cache miss + gate) ---"

# Swap to Run B patches. Default Run B widens the xpath to @ParentName="TC_WildcardBase",
# newly matching TC_Wildcard_B and TC_Wildcard_C (M2a wildcard flip), and also changes the op
# type on TC_Identity and TC_SeqTarget (pure patch-file edit cases), and adds conditionalTrigger
# to TC_Conditional (CASE 5 conditional branch flip). The --expect-fallback variant instead edits
# a value inside the change mod's own sequence — keeping the container op but marking the patch
# file dirty, so SubDocExpander declines to recompute (fallback).
log "Setting Change.xml to Run B ($RUN_B_CHANGE)..."
cp "$CHANGE_MOD_DIR/ChangeTemplates/$RUN_B_CHANGE" "$CHANGE_MOD_DIR/Patches/Change.xml"

# --expect-p1 (P1): swap the JoofTest.PropDef def file to its Run B value (p1Tag run-a -> run-b),
# AFTER run A captured the baseline graph. This changed DEF file is the only between-run delta, so
# Seed 1 must dirty the namespaced def by its node id — which only matches the gate/Unified id if
# RegisterNode keyed it by the element name (the P1 fix).
if [[ $EXPECT_P1 -eq 1 ]]; then
    log "Swapping P1Defs.xml to Run B (p1Tag=run-b)..."
    cp "$P1_MOD_DIR/P1Templates/P1Defs_RunB.xml" "$P1_MOD_DIR/Defs/P1Defs.xml"
fi

# --expect-ownership (#50): swap OwnerDefs.xml to its Run B value (newly declares
# TC_Ownership_Target). ModsConfig.xml is NOT touched here -- both owner mods stay present in
# both runs, so this changed Defs file is the ONLY between-run delta. Since joof.testharness.owner
# loads after joof.testharness.ownerbase, last-write-wins makes it the new real owner.
if [[ $EXPECT_OWNERSHIP -eq 1 ]]; then
    log "Swapping OwnerDefs.xml to Run B (declares TC_Ownership_Target)..."
    cp "$OWNER_MOD_DIR/OwnerTemplates/OwnerDefs_RunB.xml" "$OWNER_MOD_DIR/Defs/OwnerDefs.xml"
fi

# --expect-added (P2): insert joof.testharness.added into ModsConfig now, AFTER run A captured its
# baseline graph without it. This is the genuine mod-list change that drives the added-defs channel
# — run B then sees TC_Added_* as new concrete defs absent from the baseline graph. (The
# ModsConfig backup taken in step 1 still restores the original on teardown.)
if [[ $EXPECT_ADDED -eq 1 ]]; then
    log "Adding joof.testharness.added to ModsConfig for Run B (the mod-list change)..."
    python3 - "$MODSCONFIG" <<'PYEOF'
import sys
path = sys.argv[1]
content = open(path, encoding="utf-8").read()
pkg = "joof.testharness.added"
if pkg in content:
    print(f"  {pkg}: already present")
else:
    # Land it after the last test mod so it is the trailing, freshly-added entry.
    content = content.replace(
        "    <li>joof.testharness.change</li>",
        f"    <li>joof.testharness.change</li>\n    <li>{pkg}</li>",
        1
    )
    print(f"  {pkg}: added (run-B mod-list change)")
open(path, "w", encoding="utf-8").write(content)
print("ModsConfig.xml updated for Run B.")
PYEOF
fi

# --expect-trial-execution (issue #72): insert joof.testharness.trialexec into ModsConfig now, AFTER
# run A captured its baseline graph without it -- same P2-style mod-list change as --expect-added.
# Run B then sees the mod as BOTH wholly new AND owning a container op (its own PatchOperationSequence),
# the two conditions the trial-execution escape hatch exists to admit instead of declining outright.
if [[ $EXPECT_TRIALEXEC -eq 1 ]]; then
    log "Adding joof.testharness.trialexec to ModsConfig for Run B (the mod-list change)..."
    python3 - "$MODSCONFIG" <<'PYEOF'
import sys
path = sys.argv[1]
content = open(path, encoding="utf-8").read()
pkg = "joof.testharness.trialexec"
if pkg in content:
    print(f"  {pkg}: already present")
else:
    # Land it after the last test mod so it is the trailing, freshly-added entry.
    content = content.replace(
        "    <li>joof.testharness.change</li>",
        f"    <li>joof.testharness.change</li>\n    <li>{pkg}</li>",
        1
    )
    print(f"  {pkg}: added (run-B mod-list change)")
open(path, "w", encoding="utf-8").write(content)
print("ModsConfig.xml updated for Run B.")
PYEOF
fi

# --expect-mayrequire (P4): REMOVE joof.testharness.gate from ModsConfig now, AFTER run A captured a
# baseline graph that includes the gate's content + MayRequire index. This pure mod-list change is
# what flips the gated content (TC_MR_Gated dropped, TC_MR_Host's gated li stripped) for run B, so
# the MayRequire seed must dirty those defs. (Teardown still restores the original from the step-1
# backup.) Same toggle for --expect-findmod and --expect-opgate (#40 case 3).
if [[ $EXPECT_MAYREQUIRE -eq 1 || $EXPECT_FINDMOD -eq 1 || $EXPECT_OPGATE -eq 1 ]]; then
    log "Removing joof.testharness.gate from ModsConfig for Run B (the mod-list change)..."
    python3 - "$MODSCONFIG" <<'PYEOF'
import sys, re
path = sys.argv[1]
content = open(path, encoding="utf-8").read()
pkg = "joof.testharness.gate"
# Drop the gate's <li> line (and its trailing newline/indent) wherever it appears in the active list.
new = re.sub(r"[ \t]*<li>" + re.escape(pkg) + r"</li>\s*\n", "", content, count=1)
if new != content:
    print(f"  {pkg}: removed (run-B mod-list change)")
else:
    print(f"  {pkg}: WARNING not found — gate was not active for run A?")
open(path, "w", encoding="utf-8").write(new)
print("ModsConfig.xml updated for Run B.")
PYEOF
fi

# --expect-typeprovider (#86): REMOVE joof.testharness.typeprovider from ModsConfig now, AFTER run A
# captured a baseline graph that includes its typeProviders index entries. This pure mod-list change
# is what makes JoofTest.GadgetDef's Type disappear for run B, so Seed 9 must dirty
# joof.testharness.typeconsumer's (UNCHANGED) def instances of it. joof.testharness.typeconsumer
# itself stays active in both runs. (Teardown still restores the original from the step-1 backup.)
if [[ $EXPECT_TYPEPROVIDER -eq 1 ]]; then
    log "Removing joof.testharness.typeprovider from ModsConfig for Run B (the mod-list change)..."
    python3 - "$MODSCONFIG" <<'PYEOF'
import sys, re
path = sys.argv[1]
content = open(path, encoding="utf-8").read()
pkg = "joof.testharness.typeprovider"
new = re.sub(r"[ \t]*<li>" + re.escape(pkg) + r"</li>\s*\n", "", content, count=1)
if new != content:
    print(f"  {pkg}: removed (run-B mod-list change)")
else:
    print(f"  {pkg}: WARNING not found — typeprovider was not active for run A?")
open(path, "w", encoding="utf-8").write(new)
print("ModsConfig.xml updated for Run B.")
PYEOF
fi

# --modlist-b=/--modlist-verbatim-b=: write run B's ModsConfig from a fully-explicit file, the same
# way Step 1 wrote run A's — instead of mutating whatever run A's ModsConfig currently contains.
# Mutually exclusive with --remove=/--add= (enforced at arg-parse time above).
if [[ -n "$MODLIST_FILE_B" || -n "$MODLIST_VERBATIM_B" ]]; then
    write_modsconfig "$MODLIST_FILE_B" "$MODLIST_VERBATIM_B" "Run B"
    MODLIST_ARCHIVE_B="$METRICS_DIR/livetest-${RUN_TS}-modlist-runB.xml"
    cp "$MODSCONFIG" "$MODLIST_ARCHIVE_B"
    log "Archived active modlist (Run B) → $MODLIST_ARCHIVE_B"
fi

# --remove=ID[,ID...]: remove the named real mod(s) from ModsConfig for run B (case-insensitive),
# AFTER run A captured the baseline graph with them present. This is the real-mod add/remove change
# under test. Comma-separate to remove a mod together with its dependents in one Run B (e.g. an
# addon that requires the mod being removed) — a single-variable "remove this feature" change even
# when it spans more than one packageId.
if [[ -n "$REMOVE_MOD" ]]; then
    log "Removing $REMOVE_MOD from ModsConfig for Run B (the mod-list change)..."
    REMOVE_MOD="$REMOVE_MOD" python3 - "$MODSCONFIG" <<'PYEOF'
import os, sys, re
path = sys.argv[1]
pkgs = [p.strip() for p in os.environ["REMOVE_MOD"].split(",") if p.strip()]
content = open(path, encoding="utf-8").read()
for pkg in pkgs:
    # Case-insensitive <li> match (RimWorld treats packageIds case-insensitively).
    new = re.sub(r"[ \t]*<li>" + re.escape(pkg) + r"</li>\s*\n", "", content, count=1, flags=re.I)
    if new != content:
        print(f"  {pkg}: removed (run-B mod-list change)")
    else:
        print(f"  {pkg}: WARNING not found in active mods — was it in --modlist for run A?")
    content = new
open(path, "w", encoding="utf-8").write(content)
print("ModsConfig.xml updated for Run B.")
PYEOF
fi

# --add=ID[,ID...]: insert the named real mod(s) into ModsConfig for run B (must NOT have been in
# the --modlist/--modlist-verbatim used for run A), reproducing the inverse real-mod-add change.
# Inserted just before </activeMods> — position doesn't matter for the dirty-set superset gate,
# only presence/absence does.
if [[ -n "$ADD_MOD" ]]; then
    log "Adding $ADD_MOD to ModsConfig for Run B (the mod-list change)..."
    ADD_MOD="$ADD_MOD" python3 - "$MODSCONFIG" <<'PYEOF'
import os, sys, re
path = sys.argv[1]
pkgs = [p.strip() for p in os.environ["ADD_MOD"].split(",") if p.strip()]
content = open(path, encoding="utf-8").read()
for pkg in pkgs:
    if re.search(r"<li>" + re.escape(pkg) + r"</li>", content, flags=re.I):
        print(f"  {pkg}: WARNING already present — was it excluded from --modlist for run A?")
        continue
    new = content.replace("</activeMods>", f"    <li>{pkg}</li>\n  </activeMods>", 1)
    if new != content:
        print(f"  {pkg}: added (run-B mod-list change)")
    else:
        print(f"  {pkg}: WARNING could not insert — </activeMods> not found")
    content = new
open(path, "w", encoding="utf-8").write(content)
print("ModsConfig.xml updated for Run B.")
PYEOF
fi

# Do NOT clear the cache — Run B needs the prior cache (DependencyGraph.json,
# AssetsHash.xml, Unified.xml) to compute the dirty set and run the gate.

# ---------------------------------------------------------------------------
# Step 5: Run B — cache miss, dirty-set diagnostic + gate
# ---------------------------------------------------------------------------
log "--- Step 5: launching Run B ---"
RIMWORLD_PID=""
launch_rimworld

# See the matching comment in Step 3 -- same rationale, Run B's own Player.log (reset by
# launch_rimworld) is checked independently.
wait_for_marker "GAGARIN: Main menu reached" "Main menu reached (Run B)" 900

# Wait for the recompute gate verdict line. The recompute gate runs right after the
# dirty-set gate (both inside the same ParseAndProcessXML postfix, once the full rebuild
# saved the new Unified.xml), so this marker means BOTH gates have run and written their
# reports. 10-minute timeout.
wait_for_marker "Recompute gate" "Recompute gate verdict (Run B done)" 1800
wait_for_file_after_marker "$GATE_REPORT" "Recompute gate" "Recompute gate verdict (Run B done)"

log "Run B complete. Killing RimWorldLinux..."
kill_rimworld

# ---------------------------------------------------------------------------
# Step 6: read and report both gate results
# ---------------------------------------------------------------------------
log "--- Step 6: reading gate reports ---"

if [[ ! -f "$GATE_REPORT" ]]; then
    fail "GateReport.json not found at $GATE_REPORT. The dirty-set gate may not have run."
fi
if [[ ! -f "$RECOMPUTE_REPORT" ]]; then
    fail "RecomputeReport.json not found at $RECOMPUTE_REPORT. The recompute gate may not have run (is DirtySetRecompute enabled?)."
fi

log "GateReport.json:"; cat "$GATE_REPORT"; echo
log "RecomputeReport.json:"; cat "$RECOMPUTE_REPORT"; echo

log "Dirty-set gate result:"
gate_ok=0; parse_gate_report && gate_ok=1 || gate_ok=0
log "Recompute gate result:"
recompute_ok=0; parse_recompute_report && recompute_ok=1 || recompute_ok=0

# --expect-added (P2): in addition to both gates, require the added-defs channel to have fired.
added_ok=1
if [[ $EXPECT_ADDED -eq 1 ]]; then
    log "Added-defs channel result:"
    added_ok=0; parse_dirtyset_added && added_ok=1 || added_ok=0
fi

# --expect-mayrequire (P4 + MayRequire recompute fidelity): require BOTH the MayRequire channel to
# have fired AND the recompute gate to pass. DefRecompute now mirrors the loader's root-level
# MayRequire / MayRequireAnyOf gate (MayRequireGate.Passes): a dirty def whose gating mod left the
# load is dropped from the splice exactly as the full rebuild dropped it, so the spliced cache
# byte-matches. (This row used to treat recompute as informational, before that fidelity fix landed.)
mayrequire_ok=1
recompute_required=1
if [[ $EXPECT_MAYREQUIRE -eq 1 ]]; then
    log "MayRequire channel result:"
    mayrequire_ok=0; parse_dirtyset_mayrequire && mayrequire_ok=1 || mayrequire_ok=0
fi

# --expect-findmod (#40): require the SAME MayRequire seed to have fired (seeds.mayRequire > 0) —
# proof FindModCapture.IndexFindMod resolved the gate mod's display name and fed TC_FM_Host's node
# id into the shared mayRequire index, so Seed 6's existing XOR check dirtied it on gate removal.
# Recompute is informational (not yet asserted required): no recompute-fidelity predictor has been
# validated for FindMod-gated content the way MayRequireGate was for P4.
findmod_ok=1
if [[ $EXPECT_FINDMOD -eq 1 ]]; then
    log "FindMod channel result (#40):"
    findmod_ok=0; parse_dirtyset_mayrequire && findmod_ok=1 || findmod_ok=0
    recompute_required=0
fi

# --expect-opgate (#40 case 3): require the SAME MayRequire seed to have fired (seeds.mayRequire > 0)
# — proof ProvenanceRecorder.RecordOperationGate/IndexOperationGate captured the operation-wrapper
# MayRequire and fed TC_OG_Host's node id into the shared mayRequire index, so Seed 6's existing XOR
# check dirtied it on gate removal (i.e. the pendingOperationGates ordering fix actually let this
# survive to be drained instead of being wiped by the old Reset() timing). Recompute is informational
# (not yet asserted required): the op is wrapped in a PatchOperationSequence, so this is expected to
# decline via the same safe-fallback path --expect-findmod exercises, not a clean recompute.
opgate_ok=1
if [[ $EXPECT_OPGATE -eq 1 ]]; then
    log "Op-gate channel result (#40 case 3):"
    opgate_ok=0; parse_dirtyset_mayrequire && opgate_ok=1 || opgate_ok=0
    recompute_required=0
fi

# --expect-p1 (P1): require the changed namespaced def to be dirtied under its element-name node id.
# Unlike MayRequire, this is a clean change, so the recompute gate IS required to pass too (the def
# recomputes and byte-matches the rebuild).
p1_ok=1
if [[ $EXPECT_P1 -eq 1 ]]; then
    log "P1 node-id result:"
    p1_ok=0; parse_dirtyset_p1 && p1_ok=1 || p1_ok=0
fi

# --expect-ownership (#50): require TC_Ownership_Target to be dirtied via the defOverrideRematch
# seed with zero mod-list delta between runs. A clean content change, so the recompute gate IS
# required to pass too (same contract as --expect-p1).
ownership_ok=1
if [[ $EXPECT_OWNERSHIP -eq 1 ]]; then
    log "Ownership rematch result (#50):"
    ownership_ok=0; parse_dirtyset_ownership && ownership_ok=1 || ownership_ok=0
fi

# --expect-typeprovider (#86, PR 1): require the type-provider seed to have fired (seeds.typeProvider
# > 0) — proof Seed 9's TypeProviderIndex XOR dirtied joof.testharness.typeconsumer's UNCHANGED
# JoofTest.GadgetDef instances purely because joof.testharness.typeprovider (the assembly owner)
# left the load. Recompute is informational (not yet asserted required): PR 2 adds a
# TypeProviderGate/DefRecompute step mirroring MayRequireGate before this can be tightened, same
# progression --expect-mayrequire went through.
typeprovider_ok=1
if [[ $EXPECT_TYPEPROVIDER -eq 1 ]]; then
    log "Type-provider channel result (#86):"
    typeprovider_ok=0; parse_dirtyset_typeprovider && typeprovider_ok=1 || typeprovider_ok=0
    recompute_required=0
fi

# --expect-recompute-gap: the INVERSE verdict. We require the recompute to have genuinely run and
# DIVERGED (the silent wrong value), so the normal recompute-pass is not required; instead gap_ok
# demands recomputeMismatches>0 && fallback==false. The dirty-set gate is still required to be a
# superset (nonDirtyMismatches==0) — the gap is a recompute-fidelity failure, not a dirty-set one.
gap_ok=1
if [[ $EXPECT_GAP -eq 1 ]]; then
    log "Recompute-gap result (allowlist fallback EXPECTED):"
    gap_ok=0; parse_recompute_gap && gap_ok=1 || gap_ok=0
    recompute_required=0
fi

# --expect-nested-conditional: identical assertion shape to --expect-recompute-gap (parse_recompute_gap
# checks fallback==true && pass==true && recomputeMismatches==0 && a "conditional" fallbackReason) —
# CASE 7 is a different fixture shape (nested conditionals) proving the same allowlist property.
nested_ok=1
if [[ $EXPECT_NESTED -eq 1 ]]; then
    log "Nested-conditional result (allowlist fallback EXPECTED):"
    nested_ok=0; parse_recompute_gap && nested_ok=1 || nested_ok=0
    recompute_required=0
fi

# --expect-scope-escape: identical assertion shape to --expect-recompute-gap (parse_recompute_positional
# checks fallback==true && pass==true && recomputeMismatches==0 && a "positional" fallbackReason) —
# CASE 12 proves the structural per-step parser catches a sibling-axis scope escape the old
# string-position heuristic would have wrongly admitted.
scopeescape_ok=1
if [[ $EXPECT_SCOPEESCAPE -eq 1 ]]; then
    log "Scope-escape result (allowlist fallback EXPECTED):"
    scopeescape_ok=0; parse_recompute_positional && scopeescape_ok=1 || scopeescape_ok=0
    recompute_required=0
fi

# --expect-rogue-op: identical assertion shape to --expect-op-kind/--expect-scope-escape
# (parse_recompute_rogueop checks fallback==true && pass==true && recomputeMismatches==0 && a
# fallbackReason naming the fully-qualified RogueMod.PatchOperationAdd) — CASE 13 proves the
# fully-qualified SafeLeafOps sweep, not just that a bare-name-colliding op happens to be declined.
rogueop_ok=1
if [[ $EXPECT_ROGUEOP -eq 1 ]]; then
    log "Rogue-op result (allowlist fallback EXPECTED, fully-qualified type name required):"
    rogueop_ok=0; parse_recompute_rogueop && rogueop_ok=1 || rogueop_ok=0
    recompute_required=0
fi

# --expect-nested-in-sequence: same gate assertion shape as --expect-nested-conditional (CASE 7),
# PLUS a direct DependencyGraph.json check that capture recorded the sequence-nested conditional's
# own edge (patchId ending in ".operations[0]") with a non-empty read set. That second check is the
# actual claim CASE 8 exists to prove — the gate/recompute pass would also happen with an EMPTY read
# set (still cross-def-safe, still declines), so it alone doesn't prove capture saw the edge.
seqnested_ok=1
if [[ $EXPECT_SEQNESTED -eq 1 ]]; then
    log "Nested-in-sequence result (allowlist fallback EXPECTED):"
    seqnested_ok=0; parse_recompute_gap && seqnested_ok=1 || seqnested_ok=0
    log "Nested-in-sequence capture check (DependencyGraph.json):"
    parse_dependency_graph_edge "#4.operations[0]" || seqnested_ok=0
    recompute_required=0
fi

# --expect-op-kind: positive regression, not a decline-proving mode. PatchOperationFindMod is in
# SafeLeafOps (offline-pinned by RecomputeAllowlistTests.FindMod_ShouldBeAdmitted_OnceProvenSafe)
# and this live mode asserts the resulting real recompute (fallback==false, recomputeMismatches==0)
# via the DEFAULT recompute assertion below (recompute_required stays 1) — PASSES live, validated
# 2026-07-09. opkind_ok is a trivial placeholder (never flipped to 0) kept only so it can sit
# alongside the decline-proving _ok vars in the final && condition below.
opkind_ok=1

# --expect-order-preserved: unlike the decline-proving modes above, this fixture is same-def
# (in-sub-doc) for both ops, so the allowlist must ADMIT it — the default recompute assertion
# (recompute_required stays 1) is the whole point of this mode; no separate _ok var needed.

# --expect-test-op: same shape as --expect-order-preserved (positive admission, not a
# decline-proving mode). Verse.PatchOperationTest is in SafeLeafOps (offline-pinned by
# RecomputeAllowlistTests.Test_ShouldBeAdmitted_OnceProvenSafe) and this live mode asserts the
# resulting real recompute (fallback==false, recomputeMismatches==0) via the DEFAULT recompute
# assertion below (recompute_required stays 1) — proven-safe admission, PASSES live from day one
# (issue #74). testop_ok is a trivial placeholder (never flipped to 0), mirroring opkind_ok, kept
# only for symmetry with the final && condition below.
testop_ok=1

# --remove (real-mod removal): the claim is purely that the dirty set stays a SUPERSET over real
# content (nonDirtyMismatches==0). Recompute is informational — a real removal typically includes
# MayRequire-gated defs the recompute-fidelity gap cannot yet reproduce.
if [[ -n "$REMOVE_MOD" ]]; then
    recompute_required=0
fi
# --add (real-mod addition): same informational-recompute contract as --remove, mirrored.
if [[ -n "$ADD_MOD" ]]; then
    recompute_required=0
fi
# --modlist-b=/--modlist-verbatim-b=: same real-bulk-content contract as --remove=/--add= (it IS a
# real-mod add/remove change, just specified as two explicit full lists instead of a diff) — the
# dirty-set superset is the required claim; recompute is informational for the same reason (real
# content routinely includes ops outside SafeLeafOps, e.g. PatchOperationInsert/AddDesignator, which
# are legitimate safe fallbacks, not defects).
if [[ -n "$MODLIST_FILE_B" || -n "$MODLIST_VERBATIM_B" ]]; then
    recompute_required=0
fi

# The recompute gate only counts toward the verdict when it is required for this mode.
recompute_verdict=$recompute_ok
if [[ $recompute_required -eq 0 ]]; then
    recompute_verdict=1
fi

if [[ $gate_ok -eq 1 && $recompute_verdict -eq 1 && $added_ok -eq 1 && $mayrequire_ok -eq 1 && $p1_ok -eq 1 && $gap_ok -eq 1 && $nested_ok -eq 1 && $seqnested_ok -eq 1 && $ownership_ok -eq 1 && $opkind_ok -eq 1 && $testop_ok -eq 1 && $scopeescape_ok -eq 1 && $rogueop_ok -eq 1 && $typeprovider_ok -eq 1 ]]; then
    echo ""
    echo "========================================"
    echo "  LIVE TEST HARNESS: PASS"
    echo "  dirty-set gate:  nonDirtyMismatches = 0 (proven superset)"
    if [[ $EXPECT_GAP -eq 1 ]]; then
        echo "  recompute-gap:   fallback=true (cross-def conditional) — recompute allowlist DECLINED the gap"
    elif [[ $EXPECT_NESTED -eq 1 ]]; then
        echo "  nested-conditional: fallback=true (immediate cross-def parent resolved) — allowlist DECLINED correctly"
    elif [[ $EXPECT_SEQNESTED -eq 1 ]]; then
        echo "  nested-in-sequence: fallback=true AND DependencyGraph.json has a populated read-set edge for the sequence-nested conditional — capture proven (issue #25 item 3)"
    elif [[ $EXPECT_OPKIND -eq 1 ]]; then
        echo "  op-kind:         fallback=false, mismatches=0 (proven-safe admission, PatchOperationFindMod is in SafeLeafOps) — PASSES live"
    elif [[ $EXPECT_TESTOP -eq 1 ]]; then
        echo "  test-op:         fallback=false, mismatches=0 (proven-safe admission, Verse.PatchOperationTest is in SafeLeafOps) — PASSES live (issue #74)"
    elif [[ $EXPECT_SCOPEESCAPE -eq 1 ]]; then
        echo "  scope-escape:    fallback=true (positional-xpath) — sibling-axis escape from a lexically-earlier anchor correctly DECLINED by the structural per-step parser"
    elif [[ $EXPECT_ROGUEOP -eq 1 ]]; then
        echo "  rogue-op:        fallback=true (unknown-op-kind, naming RogueMod.PatchOperationAdd) — bare-name-colliding op correctly DECLINED by the fully-qualified SafeLeafOps sweep"
    elif [[ $recompute_required -eq 1 ]]; then
        echo "  recompute gate:  recomputeMismatches = 0 (sub-doc recompute byte-matches rebuild)"
    else
        echo "  recompute gate:  informational only this mode (pass=$recompute_ok)"
    fi
    if [[ $EXPECT_ADDED -eq 1 ]]; then
        echo "  added-defs (P2): seeds.addedDefs > 0 (new mod's defs dirtied + spliced in)"
    fi
    if [[ $EXPECT_MAYREQUIRE -eq 1 ]]; then
        echo "  MayRequire (P4): seeds.mayRequire > 0 (gated defs dirtied on mod removal)"
    fi
    if [[ $EXPECT_P1 -eq 1 ]]; then
        echo "  node-id (P1):    changed def dirtied as JoofTest.PropDef/TC_P1_Prop (element-name keyed)"
    fi
    if [[ $EXPECT_OWNERSHIP -eq 1 ]]; then
        echo "  ownership (#50): TC_Ownership_Target dirtied via defOverrideRematch with zero mod-list delta"
    fi
    if [[ $EXPECT_TYPEPROVIDER -eq 1 ]]; then
        echo "  type-provider (#86): seeds.typeProvider > 0 (unchanged joof.testharness.typeconsumer's JoofTest.GadgetDef defs dirtied on provider mod removal)"
    fi
    if [[ -n "$REMOVE_MOD" ]]; then
        echo "  real removal:    $REMOVE_MOD removed — dirty set stayed a superset (P1+P4 on real content)"
    fi
    if [[ -n "$ADD_MOD" ]]; then
        echo "  real addition:   $ADD_MOD added — dirty set stayed a superset (Seed 5 on real content)"
    fi
    if [[ -n "$MODLIST_FILE_B" || -n "$MODLIST_VERBATIM_B" ]]; then
        echo "  real bulk change: run A/run B modlists archived separately — dirty set stayed a superset over the full diff"
    fi
    echo "========================================"
    EXIT_CODE=0
else
    echo ""
    echo "========================================"
    echo "  LIVE TEST HARNESS: FAIL"
    echo "  dirty-set gate pass=$gate_ok  recompute gate pass=$recompute_ok (required=$recompute_required)"
    echo "  added-defs pass=$added_ok  mayRequire pass=$mayrequire_ok  p1 pass=$p1_ok  ownership pass=$ownership_ok  recompute-gap pass=$gap_ok  nested-conditional pass=$nested_ok  nested-in-sequence pass=$seqnested_ok  op-kind pass=$opkind_ok  test-op pass=$testop_ok  scope-escape pass=$scopeescape_ok  rogue-op pass=$rogueop_ok  type-provider pass=$typeprovider_ok"
    echo "  See GateReport.json / RecomputeReport.json (+ RecomputeMismatch.json) for details."
    echo "========================================"
    EXIT_CODE=1
fi

# ---------------------------------------------------------------------------
# Step 7: archive the artifacts
# ---------------------------------------------------------------------------
# Reuse RUN_TS so the reports share the timestamp of the modlist archived in Step 1 — one run, one
# timestamp, fully reproducible (livetest-<RUN_TS>-modlist.xml + the reports below).
mkdir -p "$METRICS_DIR"
for f in GateReport.json RecomputeReport.json DirtySet.json DependencyGraph.json RecomputeMismatch.json; do
    src="$CACHE_DIR/$f"
    if [[ -f "$src" ]]; then
        dst="$METRICS_DIR/livetest-runB-${RUN_TS}-${f}"
        cp "$src" "$dst"
        log "Archived $f → $dst"
    fi
done

# Bisection aid: for any coverage-audit-flagged id, show what actually changed in the def
# itself (Run A's Unified.xml vs this rebuild's), not just that it changed. Saved alongside the
# other reports so a later comparison across bisection runs doesn't require re-running.
# Uses the Run A archive from Step 3 (not the live Incremental/prior/ sidecar) — that sidecar
# gets refreshed again after Run B, so reading it here would diff Run B against itself.
PRIOR_UNIFIED="$METRICS_DIR/livetest-runA-${RUN_TS}-Unified.xml"
CURRENT_UNIFIED="$CACHE_DIR/Unified.xml"
if [[ -f "$GATE_REPORT" && -f "$PRIOR_UNIFIED" && -f "$CURRENT_UNIFIED" ]]; then
    mapfile -t UNATTR_IDS < <(python3 -c "
import json
d = json.load(open('$GATE_REPORT'))
for i in d.get('unattributedChangedIds', []):
    print(i)
")
    if [[ ${#UNATTR_IDS[@]} -gt 0 ]]; then
        DEFDIFF_OUT="$METRICS_DIR/livetest-runB-${RUN_TS}-DefDiff.txt"
        python3 "$SCRIPT_DIR/diff_def.py" "$PRIOR_UNIFIED" "$CURRENT_UNIFIED" "${UNATTR_IDS[@]}" | tee "$DEFDIFF_OUT"
        log "Archived unattributed-def diffs → $DEFDIFF_OUT"
    fi
fi

exit $EXIT_CODE
