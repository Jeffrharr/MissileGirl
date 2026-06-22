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
#   bash run_test.sh [--no-teardown] [--expect-fallback]
#     --no-teardown     leaves symlinks/ModsConfig/DLL in place
#     --expect-fallback uses the *_Fallback change files (the change mod owns a container op)
#                       and asserts RecomputeReport shows fallback==true && pass==true instead
#                       of the default real-recompute fallback==false && recomputeMismatches==0
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHANGE_MOD_DIR="$SCRIPT_DIR/TestMod_Change"
DEFS_MOD_DIR="$SCRIPT_DIR/TestMod_Defs"
STATIC_MOD_DIR="$SCRIPT_DIR/TestMod_Static"

NO_TEARDOWN=0
# --expect-fallback: exercise the changed-mod container-op fallback instead of the default
# real-recompute case. It swaps in the *_Fallback change files (in which the CHANGED mod owns
# a PatchOperationSequence) and flips the recompute-report assertion to require fallback==true.
EXPECT_FALLBACK=0
for arg in "$@"; do
    if [[ "$arg" == "--no-teardown" ]]; then
        NO_TEARDOWN=1
    elif [[ "$arg" == "--expect-fallback" ]]; then
        EXPECT_FALLBACK=1
    fi
done

# Pick the change-file pair for this run mode. Default: the leaf-op fixtures that drive a real
# sub-doc recompute. --expect-fallback: the fixtures whose change mod owns a container op, so
# SubDocExpander declines to recompute and the full rebuild stands in.
if [[ $EXPECT_FALLBACK -eq 1 ]]; then
    RUN_A_CHANGE="Change_RunA_Fallback.xml"
    RUN_B_CHANGE="Change_RunB_Fallback.xml"
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
    log "Symlinks removed."

    # Leave Change.xml in place (it's a test artifact; leaving it is harmless and useful for
    # inspecting the state after a run). Clean it up manually if desired.
}

# Launch RimWorldLinux with sandbox disabled, capture PID.
# Retries up to MAX_RETRIES times on the known intermittent early-startup SIGSEGV
# (the Boehm-GC crash in Prepatcher's prestarter GUI, ~1 in 4 launches). The crash
# signature is "Caught fatal signal - signo:11" landing in Player.log BEFORE any
# "GAGARIN:" line — i.e. the process died during native init, before our mod loaded.
# (The "GC_mark_from" backtrace prints on stderr, which we send to /dev/null, so it is
# NOT a reliable Player.log signature — an earlier version grepped for it there and never
# matched, turning every flaky crash into a hard fail.)
launch_rimworld() {
    local max_retries=5
    local attempt=0

    while true; do
        attempt=$((attempt + 1))
        log "Launching RimWorld (attempt $attempt / $max_retries)..."

        # Clear the player log so we get a fresh read each attempt.
        # RimWorld rewrites it on launch, but there can be leftover content
        # from a previous launch that could confuse our grep.
        # We truncate rather than delete so the path always exists.
        : > "$PLAYER_LOG"

        # --no-sandbox: avoids the Boehm-GC SIGSEGV in Prepatcher's prestarter GUI
        # that occurs ~1 in 4 launches when sandboxed.
        "$RIMWORLD/RimWorldLinux" --no-sandbox \
            -logfile "$PLAYER_LOG" \
            2>/dev/null &
        RIMWORLD_PID=$!
        log "RimWorldLinux PID: $RIMWORLD_PID"

        # Wait a bit and then check: did it crash during native init (a fatal signal in
        # Player.log before any GAGARIN output = the known flaky prestarter crash)?
        sleep 60
        if ! kill -0 "$RIMWORLD_PID" 2>/dev/null; then
            # Process already dead. If it died before our mod loaded AND the log shows a
            # fatal signal, it's the flaky early-startup crash — retry. If "GAGARIN:" is
            # present, the mod loaded and this is a REAL crash we must surface (not retry).
            if ! grep -q "GAGARIN:" "$PLAYER_LOG" 2>/dev/null && \
               { grep -q "Caught fatal signal" "$PLAYER_LOG" 2>/dev/null || \
                 grep -q "GC_mark_from" "$PLAYER_LOG" 2>/dev/null; }; then
                log "Detected flaky early-startup SIGSEGV (attempt $attempt). Retrying..."
                if [[ $attempt -ge $max_retries ]]; then
                    fail "RimWorld crashed at startup $max_retries times in a row. Giving up."
                fi
                continue
            else
                fail "RimWorldLinux exited unexpectedly (PID $RIMWORLD_PID). Check $PLAYER_LOG."
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

# ---------------------------------------------------------------------------
# Step 0: ensure test-mod symlinks exist
# ---------------------------------------------------------------------------
log "--- Step 0: setting up test-mod symlinks ---"
ln -sfn "$DEFS_MOD_DIR"   "$MODS_DIR/joof-testharness-defs"
ln -sfn "$STATIC_MOD_DIR" "$MODS_DIR/joof-testharness-static"
ln -sfn "$CHANGE_MOD_DIR" "$MODS_DIR/joof-testharness-change"
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
# Step 1: patch ModsConfig.xml to add test mods after vr.missilegirl
# ---------------------------------------------------------------------------
log "--- Step 1: patching ModsConfig.xml ---"
cp "$MODSCONFIG" "$MODSCONFIG_BAK"
log "Backup saved to $MODSCONFIG_BAK"

python3 - "$MODSCONFIG" <<'PYEOF'
import sys, re

path = sys.argv[1]
content = open(path, encoding="utf-8").read()

# Package IDs to inject, in load order.
# joof.testharness.defs must load first (provides the ThingDefs),
# then joof.testharness.static (static patches layered on top),
# then joof.testharness.change (the patched file we swap between runs).
to_add = [
    "joof.testharness.defs",
    "joof.testharness.static",
    "joof.testharness.change",
]

# Only add entries that aren't already in the file.
for pkg in to_add:
    if pkg in content:
        print(f"  {pkg}: already present")
    else:
        # Insert after vr.missilegirl (the last normal mod)
        content = content.replace(
            "    <li>vr.missilegirl</li>",
            f"    <li>vr.missilegirl</li>\n    <li>{pkg}</li>",
            1
        )
        print(f"  {pkg}: added")

open(path, "w", encoding="utf-8").write(content)
print("ModsConfig.xml updated.")
PYEOF

# ---------------------------------------------------------------------------
# Step 2: prepare Run A (cold baseline + provenance capture)
# ---------------------------------------------------------------------------
log "--- Step 2: preparing Run A (cold baseline) ---"

# Clear the MissileGirl cache so Run A is a guaranteed full cold rebuild.
# The cold rebuild writes DependencyGraph.json which Run B's diagnostic needs.
log "Clearing MissileGirl cache..."
rm -rf "$CACHE_DIR"
mkdir -p "$CACHE_DIR"

# Copy Run A patches into the active Change.xml.
# Default Run A has a narrow xpath (only matches TC_Wildcard_A by defName); the --expect-fallback
# variant instead gives the change mod a PatchOperationSequence (captured into the baseline graph).
log "Setting Change.xml to Run A ($RUN_A_CHANGE)..."
cp "$CHANGE_MOD_DIR/Patches/$RUN_A_CHANGE" "$CHANGE_MOD_DIR/Patches/Change.xml"

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
cp "$CHANGE_MOD_DIR/Patches/$RUN_B_CHANGE" "$CHANGE_MOD_DIR/Patches/Change.xml"

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

if [[ $gate_ok -eq 1 && $recompute_ok -eq 1 ]]; then
    echo ""
    echo "========================================"
    echo "  LIVE TEST HARNESS: PASS"
    echo "  dirty-set gate:  nonDirtyMismatches = 0 (proven superset)"
    echo "  recompute gate:  recomputeMismatches = 0 (sub-doc recompute byte-matches rebuild)"
    echo "========================================"
    EXIT_CODE=0
else
    echo ""
    echo "========================================"
    echo "  LIVE TEST HARNESS: FAIL"
    echo "  dirty-set gate pass=$gate_ok  recompute gate pass=$recompute_ok"
    echo "  See GateReport.json / RecomputeReport.json (+ RecomputeMismatch.json) for details."
    echo "========================================"
    EXIT_CODE=1
fi

# ---------------------------------------------------------------------------
# Step 7: archive the artifacts
# ---------------------------------------------------------------------------
METRICS_DIR="/home/deck/Developer/RimWorldMods/MissileGirl-metrics"
mkdir -p "$METRICS_DIR"
TS="$(date +%Y%m%d-%H%M)"
for f in GateReport.json RecomputeReport.json DirtySet.json DependencyGraph.json RecomputeMismatch.json; do
    src="$CACHE_DIR/$f"
    if [[ -f "$src" ]]; then
        dst="$METRICS_DIR/livetest-runB-${TS}-${f}"
        cp "$src" "$dst"
        log "Archived $f → $dst"
    fi
done

exit $EXIT_CODE
