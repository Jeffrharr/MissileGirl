#!/usr/bin/env python3
"""
Safety-predictor analysis for the incremental recompute fallback (#2: never serve wrong data).

The incremental recompute can produce a wrong value when a dirty/context def's correct value
depends on patch context the tiny sub-doc can't reproduce (cross-def reads, conditional
re-evaluation, positional/wildcard xpath, list-ordering). Production has no full rebuild to diff
against, so safety requires a STATIC predictor: flag "can't recompute this def faithfully" from the
dependency graph alone, and fall back to a full rebuild whenever it fires.

This script quantifies a candidate predictor — "a def is UNSAFE if it, or any of its inheritance
ancestors, is touched by a context-dependent / container patch op from any mod" — on a captured
DependencyGraph.json:

  * RECALL (safety): of a known set of recompute MISMATCH ids (ground truth from a gate run),
    how many does the predictor flag?  Must be 100% or the predictor is unsafe (false negatives =
    silently-wrong data).
  * OVER-FALLBACK CEILING (coverage cost): what fraction of ALL concrete defs are flagged?  If
    nearly everything is flagged, a change almost always trips the predictor → near-100% fallback →
    no incremental payoff (safe but useless). This is the key MVP-viability number.

A def is "directly touched" by an op if the op's matched/modifiedNodeIds include it. "Container /
context-dependent" op kinds are configurable (see UNSAFE_OPS + SEQUENCE_MEMBERSHIP). Touch then
propagates DOWN the inheritance tree: if an abstract base is unsafe-touched, every concrete def that
inherits from it is unsafe too (the toastyman affordance case — a PatchOperationConditional patches
an abstract TerrainDef base, concrete children inherit the result).

Usage:
    python3 scripts/predictor_analysis.py DependencyGraph.json [RecomputeReport.json]
"""

import json
import re
import sys
from collections import defaultdict, Counter

# Op kinds whose effect/match can depend on state outside a dirty-only sub-doc. Conservative: these
# are the ones we currently believe can diverge when recomputed over a partial document.
UNSAFE_OPS = {
    "PatchOperationConditional",   # evaluates a <match> test that may read cross-def / absent state
    "PatchOperationTest",          # bare conditional test
    "PatchOperationAddIf",
    "PatchOperationReplaceIf",
    "PatchOperationRemoveIf",
    "PatchOperationFindMod",       # mod-presence gated (and its nested ops)
    "PatchOperationInsert",        # positional — sibling order matters
}
# A patchId ending in `.operations[N]` is a child op of a PatchOperationSequence (or a Conditional's
# match/nomatch branch). Sequence membership itself is a context hazard: the sequence aborts on the
# first child whose xpath finds no match, so a child's effect depends on its siblings' targets being
# present. Toggle this to measure the sequence-membership contribution separately.
SEQUENCE_MEMBERSHIP = True

SEQ_CHILD_RE = re.compile(r'\.operations\[\d+\]$')


def is_concrete(node_id):
    return '/' in node_id


def main(graph_path, report_path=None):
    graph = json.load(open(graph_path))
    nodes = [n['id'] for n in graph['nodes']]
    edges = graph['patchEdges']
    inh = graph['inheritanceEdges']

    concrete = [n for n in nodes if is_concrete(n)]
    n_concrete = len(concrete)

    # parent -> [children] over the inheritance tree (propagate touch downward).
    children = defaultdict(list)
    for e in inh:
        p = e.get('parentNodeId')
        c = e.get('childNodeId')
        if p and c:
            children[p].append(c)

    # Directly unsafe-touched nodes: any node a context-dependent op matched or modified.
    directly_unsafe = set()
    op_hits = Counter()      # op kind -> #nodes it contributed (for the breakdown)
    for e in edges:
        op = e.get('operationType') or ''
        pid = e.get('patchId') or ''
        unsafe = op in UNSAFE_OPS or (SEQUENCE_MEMBERSHIP and SEQ_CHILD_RE.search(pid) is not None)
        if not unsafe:
            continue
        touched = set(e.get('modifiedNodeIds', [])) | set(e.get('matchedNodeIds', []))
        if touched:
            key = op if op in UNSAFE_OPS else "Sequence-child"
            op_hits[key] += len(touched)
            directly_unsafe |= touched

    # Propagate downward through inheritance (BFS): a child inheriting from an unsafe base is unsafe.
    unsafe = set(directly_unsafe)
    stack = list(directly_unsafe)
    while stack:
        cur = stack.pop()
        for ch in children.get(cur, ()):
            if ch not in unsafe:
                unsafe.add(ch)
                stack.append(ch)

    unsafe_concrete = {n for n in unsafe if is_concrete(n)}

    print(f"Graph: {graph_path.split('/')[-1]}")
    print(f"  concrete defs:            {n_concrete:>6}")
    print(f"  patch edges:              {len(edges):>6}")
    print(f"  inheritance edges:        {len(inh):>6}")
    print()
    print(f"Predictor: UNSAFE = touched by {sorted(UNSAFE_OPS)}")
    print(f"           + sequence-child membership ({SEQUENCE_MEMBERSHIP}), propagated down inheritance")
    print()
    print(f"  directly unsafe-touched nodes:   {len(directly_unsafe):>6}")
    print(f"  after inheritance propagation:   {len(unsafe):>6}")
    print(f"  >>> UNSAFE concrete defs:        {len(unsafe_concrete):>6}  "
          f"= {100*len(unsafe_concrete)/n_concrete:.1f}% of all concrete defs  (over-fallback ceiling)")
    print()
    print("  contribution by op kind (nodes directly touched, pre-propagation):")
    for k, v in op_hits.most_common():
        print(f"    {v:>6}  {k}")

    if report_path:
        rep = json.load(open(report_path))
        mism = rep.get('mismatchIds') or []
        print()
        print(f"RECALL against {report_path.split('/')[-1]} ({len(mism)} mismatch ids):")
        if not mism:
            print("  (no mismatchIds in this report — nothing to score)")
        else:
            flagged = [m for m in mism if m in unsafe]
            missed = [m for m in mism if m not in unsafe]
            print(f"  flagged by predictor: {len(flagged)}/{len(mism)} "
                  f"({100*len(flagged)/len(mism):.0f}%)")
            if missed:
                print(f"  MISSED (false negatives — UNSAFE predictor would silently serve these wrong):")
                for m in missed:
                    print(f"    {m}")
            else:
                print("  no false negatives — predictor catches every known mismatch ✓")

    allowlist_analysis(graph)


# --- ALLOWLIST fallback-rate study (the MVP hit-rate / backlog) -------------------------------------
# Mirrors Source/Gagarin/Core/Incremental/RecomputeAllowlist.cs: a def is "allowlist-clean" (would take
# the incremental path if dirtied) iff EVERY op producing it — i.e. modifying it OR an inheritance
# ancestor — is a proven-faithful pattern: a non-positional safe-leaf op, or a conditional branch whose
# parent test reads only in-sub-doc defs. Anything else falls back (categorized). Per-def estimate;
# subDoc is approximated as {def + ancestors} (sequence-sibling context is ignored, so same-def
# conditionals reading a sibling are slightly OVER-counted as fallback — a conservative floor on the
# clean rate). This is the production safety model: unknown => fall back => correct-but-slower.
ALLOWLIST_LEAF_OPS = {
    "PatchOperationAdd", "PatchOperationReplace", "PatchOperationRemove",
    "PatchOperationAttributeSet", "PatchOperationAttributeAdd", "PatchOperationAttributeRemove",
    "PatchOperationSetName",
}
POSITIONAL_XPATH = re.compile(r'\[\s*\d+\s*\]|\blast\s*\(|\bposition\s*\(|following-sibling|preceding-sibling')


def _is_branch_child(pid):
    suffix = pid.split('#', 1)[1] if '#' in pid else pid
    return '.match' in suffix or '.nomatch' in suffix


def _branch_parent_id(pid):
    cuts = [i for i in (pid.find('.match'), pid.find('.nomatch')) if i >= 0]
    return pid[:min(cuts)] if cuts else None


def allowlist_analysis(graph):
    nodes = [n['id'] for n in graph['nodes']]
    edges = graph['patchEdges']
    inh = graph['inheritanceEdges']
    concrete = [n for n in nodes if is_concrete(n)]

    parent_of = {}
    for e in inh:
        p, c = e.get('parentNodeId'), e.get('childNodeId')
        if p and c:
            parent_of[c] = p

    # node id -> producing edges (edges that modify it), and conditional parent id -> read set.
    modified_index = defaultdict(list)
    cond_reads = {}
    for e in edges:
        op = e.get('operationType') or ''
        pid = e.get('patchId') or ''
        if 'Conditional' in op and not _is_branch_child(pid):
            cond_reads[pid] = set(e.get('matchedNodeIds') or [])
            continue  # parent test edges produce nothing
        for nid in (e.get('modifiedNodeIds') or []):
            modified_index[nid].append(e)

    def classify_edge(e, subdoc):
        op = e.get('operationType') or ''
        pid = e.get('patchId') or ''
        if not op:
            return 'capture-gap'
        if op not in ALLOWLIST_LEAF_OPS:
            return 'unknown-op-kind'
        if POSITIONAL_XPATH.search(e.get('xpath') or ''):
            return 'positional-xpath'
        if _is_branch_child(pid):
            parent = _branch_parent_id(pid)
            if parent is None or parent not in cond_reads:
                return 'capture-gap'
            if not cond_reads[parent].issubset(subdoc):
                return 'conditional-cross-def'
        return None  # allowlisted

    clean = 0
    causes = Counter()      # first blocking category per blocked def
    for d in concrete:
        relevant = {d}
        cur = d
        while cur in parent_of and parent_of[cur] not in relevant:
            relevant.add(parent_of[cur]); cur = parent_of[cur]
        subdoc = relevant  # approx (no sequence-sibling context)
        block = None
        for t in relevant:
            for e in modified_index.get(t, ()):  # noqa: matches RecomputeAllowlist's relevant-target scan
                cat = classify_edge(e, subdoc)
                if cat:
                    block = cat; break
            if block:
                break
        if block:
            causes[block] += 1
        else:
            clean += 1

    n = len(concrete)
    print()
    print("ALLOWLIST fallback-rate (safe-by-default MVP model):")
    print(f"  allowlist-CLEAN concrete defs: {clean:>6} / {n}  = {100*clean/n:.1f}%  "
          f"(would take the incremental path if dirtied alone)")
    print(f"  would FALL BACK:               {n-clean:>6} / {n}  = {100*(n-clean)/n:.1f}%")
    print("  fallback cause backlog (defs blocked, by first cause — what to allowlist next):")
    for cat, v in causes.most_common():
        print(f"    {v:>6} ({100*v/n:4.1f}%)  {cat}")


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(1)
    main(sys.argv[1], sys.argv[2] if len(sys.argv) > 2 else None)
