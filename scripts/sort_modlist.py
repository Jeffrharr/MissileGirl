#!/usr/bin/env python3
"""Topologically sort a candidate RimWorld packageId subset using About.xml
loadAfter/loadBefore/modDependencies tags across the workshop content dir + Core/DLCs.

Usage:
    sort_modlist.py <candidate-file> [--workshop DIR] [-o OUTFILE]

candidate-file: one packageId per line, '#' comments allowed, blank lines ignored.
Output: packageIds in dependency-safe load order, one per line, ready for
`run_test.sh --modlist-verbatim=`.

Unknown/missing mods are warned about (stderr) and kept in input order at the end.
Cycles are warned about (stderr) and broken by input order for the offending mods,
rather than failing the whole sort.
"""
import sys
import argparse
import xml.etree.ElementTree as ET
from pathlib import Path

DEFAULT_WORKSHOP = Path("/home/deck/.local/share/Steam/steamapps/workshop/content/294100")

# Fixed base order: these always load first, in this order, if present in the candidate set.
BASE_ORDER = [
    "ludeon.rimworld",
    "ludeon.rimworld.royalty",
    "ludeon.rimworld.ideology",
    "ludeon.rimworld.biotech",
    "ludeon.rimworld.anomaly",
    "brrainz.harmony",
]


def _text_list(node, tag):
    parent = node.find(tag)
    if parent is None:
        return []
    out = []
    for li in parent.findall("li"):
        if li.text and li.text.strip():
            out.append(li.text.strip())
        else:
            pid = li.find("packageId")
            if pid is not None and pid.text:
                out.append(pid.text.strip())
    return out


def scan_workshop(workshop_dir):
    """Return {lowercased packageId: {'packageId', 'name', 'loadAfter', 'loadBefore', 'modDependencies'}}."""
    mods = {}
    for about_path in workshop_dir.glob("*/About/About.xml"):
        try:
            root = ET.parse(about_path).getroot()
        except ET.ParseError as e:
            print(f"warn: failed to parse {about_path}: {e}", file=sys.stderr)
            continue
        pid_el = root.find("packageId")
        if pid_el is None or not pid_el.text:
            continue
        pid = pid_el.text.strip()
        name_el = root.find("name")
        mods[pid.lower()] = {
            "packageId": pid,
            "name": name_el.text.strip() if name_el is not None and name_el.text else pid,
            "loadAfter": [x.lower() for x in _text_list(root, "loadAfter")],
            "loadBefore": [x.lower() for x in _text_list(root, "loadBefore")],
            "modDependencies": [x.lower() for x in _text_list(root, "modDependencies")],
        }
    return mods


def topo_sort(candidates, catalog):
    """candidates: ordered list of packageIds (original case). Returns sorted list (original case)."""
    lc_to_orig = {c.lower(): c for c in candidates}
    cand_set = set(lc_to_orig.keys())

    # Base order first, then the rest in input order (stable tiebreak).
    base = [c for c in BASE_ORDER if c in cand_set]
    rest = [c.lower() for c in candidates if c.lower() not in BASE_ORDER]
    ordered_ids = base + rest

    # edges[a] = set of b such that a must load before b
    base_set = set(BASE_ORDER)

    def add_edge(a, b):
        # a must load before b. BASE_ORDER's relative order is authoritative and fixed:
        # never let a tag reorder two base mods, and never let a non-base mod force
        # itself ahead of a base mod (base always loads first).
        if a in base_set and b in base_set:
            return
        if b in base_set and a not in base_set:
            return
        edges.setdefault(a, set()).add(b)

    edges = {c: set() for c in ordered_ids}
    for c in ordered_ids:
        info = catalog.get(c)
        if info is None:
            continue
        for dep in info["loadAfter"] + info["modDependencies"]:
            if dep in cand_set and dep != c:
                add_edge(dep, c)
        for dep in info["loadBefore"]:
            if dep in cand_set and dep != c:
                add_edge(c, dep)

    # Kahn's algorithm, stable-tiebreak by ordered_ids position, cycle-tolerant.
    pos = {c: i for i, c in enumerate(ordered_ids)}
    indeg = {c: 0 for c in ordered_ids}
    for a, outs in edges.items():
        for b in outs:
            indeg[b] = indeg.get(b, 0) + 1

    result = []
    remaining = set(ordered_ids)
    while remaining:
        ready = sorted((c for c in remaining if indeg.get(c, 0) == 0), key=lambda c: pos[c])
        if not ready:
            # cycle: break it by picking the earliest-input-order remaining node
            pick = min(remaining, key=lambda c: pos[c])
            print(f"warn: dependency cycle detected involving {pick}; breaking by input order", file=sys.stderr)
            ready = [pick]
        for c in ready:
            result.append(c)
            remaining.discard(c)
            for b in edges.get(c, ()):
                if b in remaining:
                    indeg[b] = max(0, indeg.get(b, 0) - 1)

    return [lc_to_orig[c] for c in result]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("candidate_file")
    ap.add_argument("--workshop", default=str(DEFAULT_WORKSHOP))
    ap.add_argument("-o", "--output")
    args = ap.parse_args()

    candidates = []
    for line in Path(args.candidate_file).read_text().splitlines():
        line = line.split("#", 1)[0].strip()
        if line:
            candidates.append(line)

    if len(candidates) > 200:
        print(f"warn: {len(candidates)} candidates exceeds run_test.sh's MAX_MODS=200 cap", file=sys.stderr)

    catalog = scan_workshop(Path(args.workshop))

    missing = [c for c in candidates if c.lower() not in catalog and c.lower() not in BASE_ORDER]
    if missing:
        print(f"warn: {len(missing)} candidate packageId(s) not found in workshop scan (kept, undated): {missing}", file=sys.stderr)

    sorted_ids = topo_sort(candidates, catalog)

    out_text = "\n".join(sorted_ids) + "\n"
    if args.output:
        Path(args.output).write_text(out_text)
    else:
        sys.stdout.write(out_text)


if __name__ == "__main__":
    main()
