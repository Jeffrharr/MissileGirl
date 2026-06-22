#!/usr/bin/env python3
"""
Transitive closure analysis for incremental recompute sub-doc expansion.

Given a dirty set (from DirtySet.json) and a dependency graph (DependencyGraph.json),
computes how many additional "context" defs must be included in the recompute sub-doc
so that PatchOperationSequences containing a dirty def don't abort prematurely due to
absent sibling defs.

Usage:
    python3 scripts/closure.py \
        metrics/M2a-baseline-narrow-20260621-1311.json \
        metrics/M2a-dirtyset-WIDE-20260621-1314.json
"""

import json
import re
import sys
from collections import defaultdict


def sequence_parent(patch_id):
    """
    If this patchId is a direct sequence child (suffix ends with .operations[N]),
    return the patchId of the parent sequence container. Otherwise return None.

    Examples:
      "mod#5.match.operations[2]"  -> "mod#5.match"
      "mod#3.operations[0]"        -> "mod#3"
      "mod#7"                      -> None  (top-level op, no parent sequence)
      "mod#5.match"                -> None  (conditional branch, not a seq child)
    """
    m = re.match(r'^(.+)\.operations\[\d+\]$', patch_id)
    return m.group(1) if m else None


def main(graph_path, dirty_path):
    with open(graph_path) as f:
        graph = json.load(f)
    with open(dirty_path) as f:
        dirty_data = json.load(f)

    edges = graph['patchEdges']
    dirty_ids = set(dirty_data['dirtyNodeIds'])
    total_nodes = dirty_data['totalNodes']

    # Index: modifiedNodeId -> edges that produced that modification
    node_to_edges = defaultdict(list)
    for e in edges:
        for nid in e.get('modifiedNodeIds', []):
            node_to_edges[nid].append(e)

    # Index: full parent patchId -> sequence-child edges under that parent
    # Only edges whose patchId ends in .operations[N] are sequence children.
    seq_parent_to_children = defaultdict(list)
    for e in edges:
        pid = e['patchId']
        mod_prefix, suffix = pid.split('#', 1) if '#' in pid else ('', pid)
        parent_suffix = sequence_parent(suffix)
        if parent_suffix is not None:
            parent_pid = f"{mod_prefix}#{parent_suffix}"
            seq_parent_to_children[parent_pid].append(e)

    # For each dirty node: find which sequences it's part of, collect their siblings.
    context_ids = set()
    # Track per-sequence expansion for the report.
    expansions = []  # (sequence_pid, dirty_trigger, sibling_count, new_context_count)
    seen_sequences = set()

    for dirty_id in sorted(dirty_ids):
        for edge in node_to_edges.get(dirty_id, []):
            pid = edge['patchId']
            mod_prefix, suffix = pid.split('#', 1) if '#' in pid else ('', pid)
            parent_suffix = sequence_parent(suffix)
            if parent_suffix is None:
                continue  # not a sequence child — leaf or conditional branch

            parent_pid = f"{mod_prefix}#{parent_suffix}"
            if parent_pid in seen_sequences:
                continue  # already accounted for this sequence
            seen_sequences.add(parent_pid)

            siblings = seq_parent_to_children.get(parent_pid, [])
            sibling_nodes = set()
            for sib in siblings:
                sibling_nodes.update(sib.get('modifiedNodeIds', []))

            new_ctx = sibling_nodes - dirty_ids
            context_ids.update(new_ctx)
            expansions.append((parent_pid, dirty_id, len(siblings), len(new_ctx)))

    # Results
    subdoc = dirty_ids | context_ids
    print(f"Dirty set:              {len(dirty_ids):>6}")
    print(f"Context (seq siblings): {len(context_ids):>6}")
    print(f"Sub-doc total:          {len(subdoc):>6}  ({100*len(subdoc)/total_nodes:.2f}% of {total_nodes} nodes)")
    print(f"Sequences triggered:    {len(expansions):>6}")
    print()

    # Top expansions by new context added
    top = sorted(expansions, key=lambda x: -x[3])[:10]
    print("Top expansions (by new context defs added):")
    print(f"  {'sequence':<60}  {'trigger def':<35}  {'siblings':>8}  {'new ctx':>7}")
    for seq_pid, trigger, sibling_count, new_count in top:
        print(f"  {seq_pid:<60}  {trigger:<35}  {sibling_count:>8}  {new_count:>7}")

    print()
    # Distribution: how many sequences add 0, 1-5, 6-20, 21+ context defs
    buckets = [0, 0, 0, 0]
    for _, _, _, new_count in expansions:
        if new_count == 0:
            buckets[0] += 1
        elif new_count <= 5:
            buckets[1] += 1
        elif new_count <= 20:
            buckets[2] += 1
        else:
            buckets[3] += 1
    print("Sequence expansion distribution:")
    print(f"  0 new context defs:    {buckets[0]:>4} sequences (dirty def was sole target)")
    print(f"  1–5 new context defs:  {buckets[1]:>4} sequences")
    print(f"  6–20 new context defs: {buckets[2]:>4} sequences")
    print(f"  21+ new context defs:  {buckets[3]:>4} sequences")


if __name__ == '__main__':
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(1)
    main(sys.argv[1], sys.argv[2])
