#!/usr/bin/env python3
"""Sample a bulk add/remove candidate set from RimSort's curated bigmodlist.xml
(intersected with currently-subscribed workshop mods), for the edge-cases loop's
Phase 2 combinatorial sweep. Drawing from a list RimSort already curates avoids most
of the accidental def-collision crashes seen with pure random sampling of all
subscribed mods.

Dependency-safe: every mod's <modDependencies> (from About.xml) must be satisfied.
- The base list is closed over modDependencies (missing hard deps are pulled in from
  the curated pool, or the requiring mod is dropped if its dep isn't in the pool at all).
- REMOVE candidates are only chosen from mods nothing else in the surviving active set
  depends on.
- ADD candidates bring their own dependency closure along with them.

Usage: sample_candidates.py <seed> <base-count> <remove-count> <add-count> <out-prefix>
Writes <out-prefix>_base.txt (unsorted candidates for sort_modlist.py) and prints
REMOVE=... / ADD=... lines for run_test.sh.
"""
import random
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from sort_modlist import scan_workshop, DEFAULT_WORKSHOP, BASE_ORDER  # noqa: E402

BIGMODLIST = Path("/home/deck/.local/share/RimSort/modlists/bigmodlist.xml")


def curated_pool(catalog):
    root = ET.parse(BIGMODLIST).getroot()
    active = root.find("activeMods")
    pkgs = [li.text.strip() for li in active if li.text]
    return [p for p in pkgs if p.lower() in catalog]


def close_deps(names, catalog, available_set):
    """Return (closed list, dropped list). Pulls in modDependencies transitively from
    available_set (the full subscribed catalog, not just the curated pool — a hard
    dependency is required regardless of whether RimSort's curated list happens to
    include it); drops a mod (and reports it) if a hard dependency isn't subscribed at all."""
    closed = set(n.lower() for n in names)
    dropped = set()
    changed = True
    while changed:
        changed = False
        for n in list(closed):
            info = catalog.get(n)
            if info is None:
                continue
            for dep in info["modDependencies"]:
                if dep in BASE_ORDER:
                    continue
                if dep not in available_set:
                    dropped.add(n)
                    closed.discard(n)
                    changed = True
                    break
                if dep not in closed:
                    closed.add(dep)
                    changed = True
    lc_to_orig = {}
    for info in catalog.values():
        lc_to_orig[info["packageId"].lower()] = info["packageId"]
    return [lc_to_orig.get(c, c) for c in closed], sorted(dropped)


def dependents_within(candidate_lc, active_lc_set, catalog):
    """Mods in active_lc_set (other than candidate) that declare candidate as a modDependency."""
    out = []
    for m in active_lc_set:
        if m == candidate_lc:
            continue
        info = catalog.get(m)
        if info and candidate_lc in info["modDependencies"]:
            out.append(m)
    return out


def main():
    seed, base_n, remove_n, add_n, prefix = sys.argv[1:6]
    seed, base_n, remove_n, add_n = int(seed), int(base_n), int(remove_n), int(add_n)
    catalog = scan_workshop(DEFAULT_WORKSHOP)
    available_set = set(catalog.keys())
    pool = curated_pool(catalog)
    rng = random.Random(seed)
    rng.shuffle(pool)

    base_raw = pool[:base_n]
    base, dropped = close_deps(base_raw, catalog, available_set)
    if dropped:
        print(f"# dropped from base (hard dep not in curated pool): {dropped}", file=sys.stderr)
    base_lc_set = set(b.lower() for b in base)

    # REMOVE: only mods nothing else currently in base depends on.
    rng.shuffle(base)
    remove = []
    remaining_lc = set(base_lc_set)
    for cand in base:
        if len(remove) >= remove_n:
            break
        cand_lc = cand.lower()
        if dependents_within(cand_lc, remaining_lc, catalog):
            continue
        remove.append(cand)
        remaining_lc.discard(cand_lc)

    # ADD: pull candidates (with their own dependency closure) from the rest of the pool.
    rest = [p for p in pool if p.lower() not in base_lc_set]
    rng.shuffle(rest)
    add_lc_set = set()
    add = []
    for cand in rest:
        if len(add) >= add_n:
            break
        cand_lc = cand.lower()
        if cand_lc in add_lc_set:
            continue
        closure, cand_dropped = close_deps([cand], catalog, available_set)
        if cand_dropped:
            continue  # unsatisfiable hard dep outside the pool; skip this candidate
        for c in closure:
            c_lc = c.lower()
            if c_lc not in add_lc_set and c_lc not in base_lc_set:
                add.append(c)
                add_lc_set.add(c_lc)

    Path(f"{prefix}_base.txt").write_text("\n".join(base) + "\n")
    print(f"REMOVE={','.join(remove)}")
    print(f"ADD={','.join(add)}")


if __name__ == "__main__":
    main()
