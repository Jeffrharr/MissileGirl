#!/usr/bin/env python3
"""Extract and diff one or more defs (by "{ElementName}/{defName}" id) between the prior
and rebuilt Unified.xml, so a bisection run shows what actually changed, not just that it
did. Used by run_test.sh's post-Run-B step; also runnable standalone against two archived
Unified.xml files.

Usage: diff_def.py <prior_unified.xml> <current_unified.xml> <id> [<id> ...]
"""
import sys
import difflib
import xml.etree.ElementTree as ET


def find_defs(unified_path, element_name, def_name):
    """Returns (list of (item_path, element) matches, error). Unified.xml wraps each
    source file's defs in <Item path="..."><Def>...</Def></Item>, and — critically — a
    def can appear MORE THAN ONCE (e.g. a mod duplicates a vanilla defName; DefDatabase.Add
    is last-write-wins at the engine level but Unified.xml keeps every contributing copy).
    Missing that fooled earlier callers into reporting "not found" when only the direct-
    child (non-Item-nested) case was checked, and would silently hide duplicate-defName
    overrides by only ever looking at the first match.
    """
    try:
        tree = ET.parse(unified_path)
    except (FileNotFoundError, ET.ParseError) as e:
        return [], f"(could not read/parse {unified_path}: {e})"
    root = tree.getroot()
    matches = []
    for item in root.iter('Item'):
        item_path = item.get('path', '?')
        for el in item.iter(element_name):
            defname_el = el.find('defName')
            if defname_el is not None and defname_el.text == def_name:
                matches.append((item_path, el))
    if not matches:
        return [], f"(no <{element_name}> with <defName>{def_name}</defName> found in {unified_path})"
    return matches, None


def pretty(el):
    ET.indent(el, space='  ')
    return ET.tostring(el, encoding='unicode')


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        sys.exit(1)
    prior_path, current_path = sys.argv[1], sys.argv[2]
    ids = sys.argv[3:]

    any_diff = False
    for id_ in ids:
        if '/' not in id_:
            print(f"=== {id_}: skipped (expected '{{Element}}/{{defName}}') ===")
            continue
        element_name, def_name = id_.split('/', 1)

        prior_matches, prior_err = find_defs(prior_path, element_name, def_name)
        current_matches, current_err = find_defs(current_path, element_name, def_name)

        print(f"=== {id_} ===")
        if prior_err:
            print(prior_err)
        if current_err:
            print(current_err)
        if len(prior_matches) > 1:
            print(f"NOTE: {len(prior_matches)} copies in prior (duplicate defName across "
                  f"source files): {[p for p, _ in prior_matches]}")
        if len(current_matches) > 1:
            print(f"NOTE: {len(current_matches)} copies in current (duplicate defName across "
                  f"source files): {[p for p, _ in current_matches]}")
        if not prior_matches or not current_matches:
            print()
            continue

        # Compare every prior copy against every current copy; a genuine duplicate-defName
        # override usually means the LAST copy (by document order, i.e. last-loaded mod) is
        # the one DefDatabase.Add actually keeps — but show all pairs since which one is
        # "new" vs "pre-existing" is exactly what we're trying to find out.
        for pi, (p_path, p_el) in enumerate(prior_matches):
            for ci, (c_path, c_el) in enumerate(current_matches):
                prior_text = pretty(p_el).splitlines()
                current_text = pretty(c_el).splitlines()
                diff = list(difflib.unified_diff(
                    prior_text, current_text,
                    fromfile=f'prior[{pi}] ({p_path})',
                    tofile=f'current[{ci}] ({c_path})',
                    lineterm=''))
                if diff:
                    any_diff = True
                    print('\n'.join(diff))
                else:
                    print(f"(prior[{pi}] == current[{ci}], both from {p_path})")
        print()

    sys.exit(0 if any_diff else 2)


if __name__ == '__main__':
    main()
