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
#   7. Waits for "Dirty-set gate" in Player.log (Run B end marker — the gate ran and wrote
#      GateReport.json).
#   8. Parses GateReport.json: PASS when nonDirtyMismatches==0. Prints a one-line verdict.
#   9. Restores ModsConfig.xml from the backup and removes the Mods symlinks.
#
# Usage:
#   cd /home/deck/Developer/RimWorldMods/MissileGirl-testharness/TestMods
#   bash run_test.sh [--no-teardown]   # --no-teardown leaves symlinks + ModsConfig in place
#
# Requirements:
#   - The deployed Gagarin.dll must have CaptureProvenance=true, DirtySetDiagnostic=true,
#     DirtySetGate=true (i.e. the M2b bring-up build, 89088 bytes, built 2026-06-21).
#   - RimWorld 1.6 installed at the standard Steam path.
#   - Each cold load takes ~4 minutes; total wall time ~8-10 minutes.
#
# Notes:
#   - This script deliberately does NOT enable DirtySetRecompute. That flag is known to
#     fail (the M2b-2b sub-doc approach hit the PatchOperationSequence wall). The test
#     proves the dirty-set GATE (nonDirtyMismatches=0), not the recompute.
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

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHANGE_MOD_DIR="$SCRIPT_DIR/TestMod_Change"
DEFS_MOD_DIR="$SCRIPT_DIR/TestMod_Defs"
STATIC_MOD_DIR="$SCRIPT_DIR/TestMod_Static"

NO_TEARDOWN=0
for arg in "$@"; do
    if [[ "$arg" == "--no-teardown" ]]; then
        NO_TEARDOWN=1
    fi
done

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

    log "Removing test-mod symlinks..."
    rm -f "$MODS_DIR/joof-testharness-defs"
    rm -f "$MODS_DIR/joof-testharness-static"
    rm -f "$MODS_DIR/joof-testharness-change"
    log "Symlinks removed."

    # Leave Change.xml in place (it's a test artifact; leaving it is harmless and useful for
    # inspecting the state after a run). Clean it up manually if desired.
}

# Launch RimWorldLinux with sandbox disabled, capture PID.
# Retries up to MAX_RETRIES times on the Boehm-GC SIGSEGV crash
# (signature: GC_mark_from in Player.log before any GAGARIN line).
launch_rimworld() {
    local max_retries=3
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

        # Wait a bit and then check: did it crash immediately (GC_mark_from before
        # any GAGARIN output = the known Boehm crash)?
        sleep 60
        if ! kill -0 "$RIMWORLD_PID" 2>/dev/null; then
            # Process already dead.
            if grep -q "GC_mark_from" "$PLAYER_LOG" 2>/dev/null && \
               ! grep -q "GAGARIN:" "$PLAYER_LOG" 2>/dev/null; then
                log "Detected Boehm-GC SIGSEGV crash (attempt $attempt). Retrying..."
                if [[ $attempt -ge $max_retries ]]; then
                    fail "RimWorld crashed with Boehm-GC SIGSEGV $max_retries times in a row. Giving up."
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
# Run A has a narrow xpath (only matches TC_Wildcard_A by defName).
log "Setting Change.xml to Run A (narrow predicates)..."
cp "$CHANGE_MOD_DIR/Patches/Change_RunA.xml" "$CHANGE_MOD_DIR/Patches/Change.xml"

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

# Swap to Run B patches. Run B widens the xpath to @ParentName="TC_WildcardBase",
# newly matching TC_Wildcard_B and TC_Wildcard_C (M2a wildcard flip), and also
# changes the op type on TC_Identity and TC_SeqTarget (pure patch-file edit cases),
# and adds conditionalTrigger to TC_Conditional (CASE 5 conditional branch flip).
log "Setting Change.xml to Run B (wide predicates)..."
cp "$CHANGE_MOD_DIR/Patches/Change_RunB.xml" "$CHANGE_MOD_DIR/Patches/Change.xml"

# Do NOT clear the cache — Run B needs the prior cache (DependencyGraph.json,
# AssetsHash.xml, Unified.xml) to compute the dirty set and run the gate.

# ---------------------------------------------------------------------------
# Step 5: Run B — cache miss, dirty-set diagnostic + gate
# ---------------------------------------------------------------------------
log "--- Step 5: launching Run B ---"
RIMWORLD_PID=""
launch_rimworld

# Wait for the gate verdict line. The gate runs right after ParseAndProcessXML
# saves the new Unified.xml, so this marker appears once the full rebuild completes.
# 10-minute timeout.
wait_for_marker "Dirty-set gate" "Dirty-set gate verdict (Run B done)" 600

log "Run B complete. Killing RimWorldLinux..."
kill_rimworld

# ---------------------------------------------------------------------------
# Step 6: read and report the gate result
# ---------------------------------------------------------------------------
log "--- Step 6: reading GateReport.json ---"

if [[ ! -f "$GATE_REPORT" ]]; then
    fail "GateReport.json not found at $GATE_REPORT. The gate may not have run."
fi

log "GateReport.json:"
cat "$GATE_REPORT"
echo

log "Parsed result:"
if parse_gate_report; then
    echo ""
    echo "========================================"
    echo "  LIVE TEST HARNESS: PASS"
    echo "  nonDirtyMismatches = 0"
    echo "  The dirty set is a proven superset."
    echo "========================================"
    EXIT_CODE=0
else
    EXIT_CODE=$?
    echo ""
    echo "========================================"
    echo "  LIVE TEST HARNESS: FAIL"
    echo "  See GateReport.json for mismatch IDs."
    echo "========================================"
fi

# ---------------------------------------------------------------------------
# Step 7: archive the artifacts
# ---------------------------------------------------------------------------
METRICS_DIR="/home/deck/Developer/RimWorldMods/MissileGirl-metrics"
mkdir -p "$METRICS_DIR"
TS="$(date +%Y%m%d-%H%M)"
for f in GateReport.json DirtySet.json DependencyGraph.json RecomputeMismatch.json; do
    src="$CACHE_DIR/$f"
    if [[ -f "$src" ]]; then
        dst="$METRICS_DIR/livetest-runB-${TS}-${f}"
        cp "$src" "$dst"
        log "Archived $f → $dst"
    fi
done

exit $EXIT_CODE
