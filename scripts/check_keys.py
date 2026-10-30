#!/usr/bin/env python3
"""Checks the translation keys of the source tree (the same rules validate-package.py applies to the release ZIP).

Usage: python -B scripts/check_keys.py

Fails (exit 1) when
  * the English and Japanese keyed files differ in keys or in the {n} placeholders of a key,
  * the C# sources use a translation key that is neither translated nor a def name,
  * a dynamic key prefix ("RW_Kind_" + ...) matches no translated key.
"""
import importlib.util
import os
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
KEYED = "Mod/Languages/{}/Keyed/RentalWarehouse.xml"

_spec = importlib.util.spec_from_file_location("validate_package", HERE / "validate-package.py")
_vp = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_vp)


def parse_keyed(path):
    root = ET.parse(path).getroot()
    return {child.tag: (child.text or "") for child in root}


def key_diffs(en, ja):
    diffs = [f"translation key {k} missing in Japanese" for k in sorted(set(en) - set(ja))]
    diffs += [f"translation key {k} missing in English" for k in sorted(set(ja) - set(en))]
    return diffs


def placeholder_diffs(en, ja):
    find = _vp.PLACEHOLDER.findall
    return [
        f"translation key {k}: placeholder mismatch between English and Japanese"
        for k in sorted(set(en) & set(ja))
        if sorted(set(find(en[k]))) != sorted(set(find(ja[k])))
    ]


def undefined_keys(used, defined):
    problems = []
    for key in sorted(used):
        if key.endswith("_"):
            if not any(d.startswith(key) for d in defined):
                problems.append(f"no translation key starts with the dynamic prefix {key}")
        elif key not in defined:
            problems.append(f"translation key {key} used in code but missing in English")
    return problems


def _def_names(defs_dir):
    found = set()
    for path in Path(defs_dir).rglob("*.xml"):
        for d in ET.parse(path).getroot().iter("defName"):
            found.add((d.text or "").strip())
    return found


def check(root):
    root = Path(root)
    en = parse_keyed(root / KEYED.format("English"))
    ja = parse_keyed(root / KEYED.format("Japanese"))
    used = set()
    for path in (root / "Source").rglob("*.cs"):
        used.update(_vp.KEY_LITERAL.findall(path.read_text(encoding="utf-8")))
    defined = set(en) | _def_names(root / "Mod" / "Defs")
    return key_diffs(en, ja) + placeholder_diffs(en, ja) + undefined_keys(used, defined)


def main():
    errors = check(REPO)
    for e in errors:
        print("ERROR:", e)
    print("PASS" if not errors else f"FAIL ({len(errors)} problem(s))")
    return 0 if not errors else 1


if __name__ == "__main__":
    sys.exit(main())
