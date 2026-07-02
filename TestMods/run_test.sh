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
#   - This script enables the four incremental-cache diagnostics at LAUNCH via GAGARIN_*
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

# Enable the four incremental-cache diagnostics at launch (GagarinPrefs reads these on first
# access). Exported here so the RimWorldLinux child process launched below inherits them.
export GAGARIN_CAPTURE_PROVENANCE=1
export GAGARIN_DIRTYSET_DIAGNOSTIC=1
export GAGARIN_DIRTYSET_GATE=1
export GAGARIN_DIRTYSET_RECOMPUTE=1
# The master toggle enables PriorStateSnapshot (the sidecar under .../MissileGirl/Incremental/prior/).
# It is the ONLY source of the TRUE prior modlist/Unified/hash that survives OnInitialization's
# modlist-change teardown — the live ModList.xml is re-dumped to the CURRENT order before the
# diagnostic reads it. Without it, any seed that compares prior vs current load order (reorder /
# MayRequire) silently sees no change on a mod add/remove and never fires. It gates ONLY the sidecar
# (verified: GagarinPrefs.IncrementalCache is referenced nowhere else), so it does not alter the
# full-rebuild path the gates compare against.
export GAGARIN_INCREMENTAL_CACHE=1

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHANGE_MOD_DIR="$SCRIPT_DIR/TestMod_Change"
DEFS_MOD_DIR="$SCRIPT_DIR/TestMod_Defs"
STATIC_MOD_DIR="$SCRIPT_DIR/TestMod_Static"
ADDED_MOD_DIR="$SCRIPT_DIR/TestMod_Added"
MAYREQUIRE_MOD_DIR="$SCRIPT_DIR/TestMod_MayRequire"
GATE_MOD_DIR="$SCRIPT_DIR/TestMod_Gate"
P1_MOD_DIR="$SCRIPT_DIR/TestMod_P1"
GENOP_MOD_DIR="$SCRIPT_DIR/TestMod_GenOp"

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
# --modlist=FILE: an OPTIONAL extra modlist (one packageId per line, '#' comments allowed) to load
# ON TOP OF the minimal base (Core + DLCs + Harmony + Gagarin + test mods). Use it to reproduce a
# specific problem set captured from a prior run. Hard-capped at 100 mods total — the whole point of
# the harness is a small, deterministic load; an 815-mod ambient list crashes RimWorld's GC during
# unrelated mod init (zero of our code runs), which is not a test result.
MODLIST_FILE=""
MAX_MODS=100
# --remove=PACKAGEID: a real-mod removal scenario. Run A loads the full --modlist; before run B this
# mod is removed from ModsConfig (a pure mod-list change), reproducing real add/remove failures (e.g.
# the vmemese case: removing it drops VFE Props' IfModActive loadFolders prop defs -> P1, and flips
# toastyman's MayRequire ThingStyleDefs -> P4). Asserts only the dirty-set superset gate
# (nonDirtyMismatches==0); recompute is informational (real content includes MayRequire defs that the
# recompute-fidelity gap can't yet reproduce).
REMOVE_MOD=""
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
    elif [[ "$arg" == --modlist=* ]]; then
        MODLIST_FILE="${arg#--modlist=}"
    elif [[ "$arg" == --remove=* ]]; then
        REMOVE_MOD="${arg#--remove=}"
    fi
done

# Single source of truth for "how many --expect-* modes are active" — both the mutual-exclusivity
# check and the --remove= incompatibility check must agree on this set, so they read the same array
# instead of each hand-listing the flags (which drifted silently until now: add a new EXPECT_* and
# forget one of the two sums, and the new mode just silently combines with another instead of
# erroring).
EXPECT_FLAGS=($EXPECT_FALLBACK $EXPECT_ADDED $EXPECT_MAYREQUIRE $EXPECT_P1 $EXPECT_GAP $EXPECT_NESTED $EXPECT_SEQNESTED)
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
if [[ -n "$REMOVE_MOD" && -z "$MODLIST_FILE" ]]; then
    echo "[run_test] WARN: --remove=$REMOVE_MOD with no --modlist= — the mod must already be in the minimal base or nothing is removed." >&2
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
elif [[ $EXPECT_ADDED -eq 1 || $EXPECT_MAYREQUIRE -eq 1 || $EXPECT_P1 -eq 1 || -n "$REMOVE_MOD" ]]; then
    # P2 / P4 / P1 / --remove: hold Change.xml at run A for BOTH runs so the change vehicle's patch
    # file does NOT change. The only between-run delta is mode-specific (P2: a mod added before run B;
    # P4: the gate mod removed; P1: the JoofTest.PropDef def file swapped; --remove: a real mod removed
    # before run B), so the dirty set is driven purely by that channel rather than a patch-file edit.
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
fail() { echo "[run_test] FAIL: $*" >&2; exit 1; }

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
    log "Symlinks removed."

    # Reset the P1 def file to its committed Run A value so a --expect-p1 run doesn't leave the git
    # tree dirty (the harness overwrites it each run anyway).
    if [[ -f "$P1_MOD_DIR/P1Templates/P1Defs_RunA.xml" ]]; then
        cp "$P1_MOD_DIR/P1Templates/P1Defs_RunA.xml" "$P1_MOD_DIR/Defs/P1Defs.xml" 2>/dev/null || true
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
RIMWORLD_STDERR="/tmp/rimworld_testharness_stderr.log"
launch_rimworld() {
    local max_retries=5
    local attempt=0

    while true; do
        attempt=$((attempt + 1))
        log "Launching RimWorld (attempt $attempt / $max_retries)..."

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

        # Wait a bit and then check: did it crash during native init (a fatal signal before
        # any GAGARIN output = the known flaky prestarter crash)?
        sleep 60
        if ! kill -0 "$RIMWORLD_PID" 2>/dev/null; then
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
        if ! kill -0 "$RIMWORLD_PID" 2>/dev/null; then
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

kill_rimworld() {
    if [[ -n "${RIMWORLD_PID:-}" ]] && kill -0 "$RIMWORLD_PID" 2>/dev/null; then
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
EXPECT_ADDED="$EXPECT_ADDED" EXPECT_MAYREQUIRE="$EXPECT_MAYREQUIRE" EXPECT_P1="$EXPECT_P1" \
MODLIST_FILE="$MODLIST_FILE" MAX_MODS="$MAX_MODS" \
RIMWORLD="$RIMWORLD" python3 - "$MODSCONFIG" <<'PYEOF'
import os, sys, re

path = sys.argv[1]
max_mods = int(os.environ.get("MAX_MODS", "100"))

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

# Base load order: Core, DLCs, Harmony, then Gagarin (the mod under test).
active = ["ludeon.rimworld"] + dlc_ids + ["brrainz.harmony", "vr.missilegirl"]

# Optional captured problem set, on top of the base, before the test mods.
extra_file = os.environ.get("MODLIST_FILE", "")
if extra_file:
    with open(extra_file, encoding="utf-8") as fh:
        for line in fh:
            pid = line.split("#", 1)[0].strip()
            if pid and pid not in active:
                active.append(pid)

# Test-harness mods always load last, in dependency order.
#   defs -> static -> change. The added mod (P2) is inserted before run B, not here.
#   --expect-mayrequire: gate + mayrequire are active for run A; gate is removed before run B.
test_mods = ["joof.testharness.defs", "joof.testharness.static", "joof.testharness.change", "joof.testharness.genop"]
if os.environ.get("EXPECT_MAYREQUIRE", "0") == "1":
    test_mods += ["joof.testharness.gate", "joof.testharness.mayrequire"]
if os.environ.get("EXPECT_P1", "0") == "1":
    test_mods += ["joof.testharness.p1"]
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
mc_rc=$?
if [[ $mc_rc -ne 0 ]]; then
    fail "Failed to write minimal ModsConfig (rc=$mc_rc)."
fi

# Archive the exact active modlist NOW (before launching), so even a crashing run leaves behind the
# list that produced it — the reproducible record the user asked for.
mkdir -p "$METRICS_DIR"
MODLIST_ARCHIVE="$METRICS_DIR/livetest-${RUN_TS}-modlist.xml"
cp "$MODSCONFIG" "$MODLIST_ARCHIVE"
log "Archived active modlist → $MODLIST_ARCHIVE"

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

# ---------------------------------------------------------------------------
# Step 3: Run A — cold load, capture provenance
# ---------------------------------------------------------------------------
log "--- Step 3: launching Run A ---"
RIMWORLD_PID=""
launch_rimworld

# Wait for ProvenanceRecorder to finish (writes DependencyGraph.json, logs the marker).
# This is the end of the cold load. 10-minute timeout; a normal cold load is ~4 min.
wait_for_marker "Provenance captured" "Provenance captured (Run A done)" 600

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

# --expect-mayrequire (P4): REMOVE joof.testharness.gate from ModsConfig now, AFTER run A captured a
# baseline graph that includes the gate's content + MayRequire index. This pure mod-list change is
# what flips the gated content (TC_MR_Gated dropped, TC_MR_Host's gated li stripped) for run B, so
# the MayRequire seed must dirty those defs. (Teardown still restores the original from the step-1
# backup.)
if [[ $EXPECT_MAYREQUIRE -eq 1 ]]; then
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

# --remove=ID: remove the named real mod from ModsConfig for run B (case-insensitive), AFTER run A
# captured the baseline graph with it. This is the real-mod add/remove change under test.
if [[ -n "$REMOVE_MOD" ]]; then
    log "Removing $REMOVE_MOD from ModsConfig for Run B (the mod-list change)..."
    REMOVE_MOD="$REMOVE_MOD" python3 - "$MODSCONFIG" <<'PYEOF'
import os, sys, re
path = sys.argv[1]
pkg = os.environ["REMOVE_MOD"]
content = open(path, encoding="utf-8").read()
# Case-insensitive <li> match (RimWorld treats packageIds case-insensitively).
new = re.sub(r"[ \t]*<li>" + re.escape(pkg) + r"</li>\s*\n", "", content, count=1, flags=re.I)
if new != content:
    print(f"  {pkg}: removed (run-B mod-list change)")
else:
    print(f"  {pkg}: WARNING not found in active mods — was it in --modlist for run A?")
open(path, "w", encoding="utf-8").write(new)
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

# Wait for the recompute gate verdict line. The recompute gate runs right after the
# dirty-set gate (both inside the same ParseAndProcessXML postfix, once the full rebuild
# saved the new Unified.xml), so this marker means BOTH gates have run and written their
# reports. 10-minute timeout.
wait_for_marker "Recompute gate" "Recompute gate verdict (Run B done)" 600

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

# --expect-p1 (P1): require the changed namespaced def to be dirtied under its element-name node id.
# Unlike MayRequire, this is a clean change, so the recompute gate IS required to pass too (the def
# recomputes and byte-matches the rebuild).
p1_ok=1
if [[ $EXPECT_P1 -eq 1 ]]; then
    log "P1 node-id result:"
    p1_ok=0; parse_dirtyset_p1 && p1_ok=1 || p1_ok=0
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

# --remove (real-mod removal): the claim is purely that the dirty set stays a SUPERSET over real
# content (nonDirtyMismatches==0). Recompute is informational — a real removal typically includes
# MayRequire-gated defs the recompute-fidelity gap cannot yet reproduce.
if [[ -n "$REMOVE_MOD" ]]; then
    recompute_required=0
fi

# The recompute gate only counts toward the verdict when it is required for this mode.
recompute_verdict=$recompute_ok
if [[ $recompute_required -eq 0 ]]; then
    recompute_verdict=1
fi

if [[ $gate_ok -eq 1 && $recompute_verdict -eq 1 && $added_ok -eq 1 && $mayrequire_ok -eq 1 && $p1_ok -eq 1 && $gap_ok -eq 1 && $nested_ok -eq 1 && $seqnested_ok -eq 1 ]]; then
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
    if [[ -n "$REMOVE_MOD" ]]; then
        echo "  real removal:    $REMOVE_MOD removed — dirty set stayed a superset (P1+P4 on real content)"
    fi
    echo "========================================"
    EXIT_CODE=0
else
    echo ""
    echo "========================================"
    echo "  LIVE TEST HARNESS: FAIL"
    echo "  dirty-set gate pass=$gate_ok  recompute gate pass=$recompute_ok (required=$recompute_required)"
    echo "  added-defs pass=$added_ok  mayRequire pass=$mayrequire_ok  p1 pass=$p1_ok  recompute-gap pass=$gap_ok  nested-conditional pass=$nested_ok  nested-in-sequence pass=$seqnested_ok"
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

exit $EXIT_CODE
