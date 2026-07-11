#!/usr/bin/env python3
"""Mechanically compute the edge-cases loop's consecutive-clean streak from
EDGE_CASES_LOOP_LOG.jsonl, replacing the hand-typed counter in
EDGE_CASES_LOOP_PROGRESS.md.

Usage: compute_streak.py [path-to-log.jsonl]
Defaults to EDGE_CASES_LOOP_LOG.jsonl at the repo root.

Rule (one line per row, applied in file order):
  - category "clean" or "issue_fixed_same_run": streak += 1
  - category "issue_open": streak resets to 0 (a real, still-unresolved
    incremental-cache bug found on that run)
  - category "bad_modlist", "harness_bug", "inconclusive": skipped entirely --
    neither increments nor resets. These are runs where the candidate modlist
    itself was broken, or the harness/tooling (not Gagarin) was at fault, or
    the result was never conclusively read -- none of which say anything
    about incremental-cache correctness.

Exit condition (see EDGE_CASES_LOOP_PROGRESS.md / the loop's plan): Phase 2 is
done once this streak reaches 50.
"""
import json
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent.parent
SCHEMA_PATH = HERE / "scripts" / "edge_cases_log.schema.json"
DEFAULT_LOG = HERE / "EDGE_CASES_LOOP_LOG.jsonl"

TARGET_STREAK = 50
COUNTS_UP = {"clean", "issue_fixed_same_run"}
RESETS = {"issue_open"}
EXCLUDED = {"bad_modlist", "harness_bug", "inconclusive"}

TYPE_MAP = {"integer": int, "string": str, "boolean": bool, "object": dict, "array": list}


def check_value(key_path, value, spec, errors):
    types = spec.get("type")
    types = types if isinstance(types, list) else [types]
    if value is None and "null" in types:
        return
    expected = next((TYPE_MAP[t] for t in types if t in TYPE_MAP), None)
    if expected is not None and not isinstance(value, expected):
        errors.append(f"{key_path}: should be {types}, got {type(value).__name__}")
        return
    if "pattern" in spec and isinstance(value, str) and not re.match(spec["pattern"], value):
        errors.append(f"{key_path}: '{value}' does not match pattern '{spec['pattern']}'")
    if "enum" in spec and value not in spec["enum"]:
        errors.append(f"{key_path}: '{value}' not one of {spec['enum']}")
    if "minLength" in spec and isinstance(value, str) and len(value) < spec["minLength"]:
        errors.append(f"{key_path}: length {len(value)} less than minLength {spec['minLength']}")
    if "minimum" in spec and isinstance(value, (int, float)) and value < spec["minimum"]:
        errors.append(f"{key_path}: {value} less than minimum {spec['minimum']}")


def validate(entry, schema, path="$"):
    errors = []
    for field in schema.get("required", []):
        if field not in entry:
            errors.append(f"{path}: missing required field '{field}'")
    if schema.get("additionalProperties") is False:
        for key in entry:
            if key not in schema.get("properties", {}):
                errors.append(f"{path}: unexpected field '{key}'")
    for key, spec in schema.get("properties", {}).items():
        if key in entry:
            check_value(f"{path}.{key}", entry[key], spec, errors)
    return errors


def main():
    log_path = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_LOG
    if not log_path.exists():
        print(f"error: {log_path} does not exist", file=sys.stderr)
        return 1

    schema = json.loads(SCHEMA_PATH.read_text())
    streak = 0
    ok = True
    for lineno, line in enumerate(log_path.read_text().splitlines(), start=1):
        if not line.strip():
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError as e:
            print(f"line {lineno}: invalid JSON -- {e}", file=sys.stderr)
            ok = False
            continue

        errors = validate(entry, schema)
        if errors:
            for err in errors:
                print(f"line {lineno}: {err}", file=sys.stderr)
            ok = False
            continue

        category = entry["category"]
        if category in COUNTS_UP:
            streak += 1
        elif category in RESETS:
            streak = 0
        elif category not in EXCLUDED:
            print(f"line {lineno}: unknown category '{category}'", file=sys.stderr)
            ok = False

    if not ok:
        print("Log has validation errors -- streak below may be unreliable.", file=sys.stderr)

    print(f"Consecutive-clean streak: {streak} / {TARGET_STREAK}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
