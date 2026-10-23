#!/usr/bin/env python3
"""Checks a release ZIP of Rental Warehouse before it is published (plan T10, acceptance A16).

Usage: python scripts/validate-package.py dist/RentalWarehouse.zip [--source Source]

Fails (exit 1) when the ZIP
  * contains game, engine or Harmony assemblies,
  * contains development files (sources, project files, pdb, obj/bin, test assemblies) or secrets,
  * lacks a required file,
  * has malformed XML,
  * has English/Japanese keyed translations that differ in keys or placeholders,
  * misses a texture referenced by its own defs,
  * (with --source) lacks a translation for a key the code uses.
"""
import argparse
import re
import sys
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

ROOT = "RentalWarehouse/"
REQUIRED = [
    "About/About.xml",
    "LoadFolders.xml",
    "1.7/Assemblies/RentalWarehouse.dll",
    "1.7/Assemblies/RentalWarehouse.Core.dll",
    "Languages/English/Keyed/RentalWarehouse.xml",
    "Languages/Japanese/Keyed/RentalWarehouse.xml",
]
LANGS = ("English", "Japanese")
GAME_ASSEMBLY = re.compile(
    r"(^|/)(Assembly-CSharp(-firstpass)?|RimWorld|UnityEngine[^/]*|UnityStubs|Waylith[^/]*|Brutal\.[^/]*|0Harmony|Lib\.Harmony[^/]*|mscorlib|netstandard|System\.[^/]*|Microsoft\.[^/]*)\.dll$",
    re.IGNORECASE,
)
DEV_FILE = re.compile(
    r"(\.pdb$|\.cs$|\.csproj$|\.sln$|\.user$|\.ps1$|\.py$|(^|/)(obj|bin|\.git|\.vs|\.idea|TestResults|\.superpowers)/|\.Tests\.dll$|\.deps\.json$)",
    re.IGNORECASE,
)
SECRET_NAME = re.compile(r"(^|/)(\.env[^/]*|id_rsa[^/]*|[^/]*\.pem|[^/]*\.key|[^/]*\.pfx)$", re.IGNORECASE)
SECRET_CONTENT = re.compile(rb"(-----BEGIN [A-Z ]*PRIVATE KEY-----|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|AKIA[0-9A-Z]{16}|xox[baprs]-[A-Za-z0-9-]{10,})")
TEXT_SUFFIXES = (".xml", ".txt", ".md", ".json", ".cfg", ".ini")
KEY_LITERAL = re.compile(r'"(RW_[A-Za-z0-9_]+)"')
PLACEHOLDER = re.compile(r"\{(\d+)\}")


def _keyed(z, name, errors):
    try:
        root = ET.fromstring(z.read(name))
    except ET.ParseError as e:
        errors.append(f"{name}: not well-formed XML ({e})")
        return {}
    return {child.tag: (child.text or "") for child in root}


def _source_keys(source_root):
    literal, dynamic = set(), set()
    for path in Path(source_root).rglob("*.cs"):
        for key in KEY_LITERAL.findall(path.read_text(encoding="utf-8")):
            (dynamic if key.endswith("_") else literal).add(key)
    return literal, dynamic


def _def_names(z, names):
    found = set()
    for n in names:
        if n.startswith(ROOT + "Defs/") and n.endswith(".xml"):
            try:
                for d in ET.fromstring(z.read(n)).iter("defName"):
                    found.add((d.text or "").strip())
            except ET.ParseError:
                pass
    return found


def validate_zip(zip_path, source_root=None):
    errors = []
    with zipfile.ZipFile(zip_path) as z:
        names = [n for n in z.namelist() if not n.endswith("/")]
        name_set = set(names)

        for n in names:
            rel = n[len(ROOT):] if n.startswith(ROOT) else n
            if not n.startswith(ROOT):
                errors.append(f"{n}: outside the {ROOT} folder")
            if GAME_ASSEMBLY.search(rel):
                errors.append(f"{n}: game assembly must not be distributed")
            elif DEV_FILE.search(rel):
                errors.append(f"{n}: development file must not be distributed")
            if SECRET_NAME.search(rel):
                errors.append(f"{n}: possible secret file")
            elif rel.lower().endswith(TEXT_SUFFIXES) and SECRET_CONTENT.search(z.read(n)):
                errors.append(f"{n}: possible secret in content")
            if rel.lower().endswith(".xml"):
                try:
                    ET.fromstring(z.read(n))
                except ET.ParseError as e:
                    errors.append(f"{n}: not well-formed XML ({e})")

        for req in REQUIRED:
            if ROOT + req not in name_set:
                errors.append(f"missing required file {ROOT}{req}")

        keyed = {}
        for lang in LANGS:
            path = f"{ROOT}Languages/{lang}/Keyed/RentalWarehouse.xml"
            keyed[lang] = _keyed(z, path, errors) if path in name_set else {}
        en, ja = keyed["English"], keyed["Japanese"]
        for key in sorted(set(en) - set(ja)):
            errors.append(f"translation key {key} missing in Japanese")
        for key in sorted(set(ja) - set(en)):
            errors.append(f"translation key {key} missing in English")
        for key in sorted(set(en) & set(ja)):
            if sorted(set(PLACEHOLDER.findall(en[key]))) != sorted(set(PLACEHOLDER.findall(ja[key]))):
                errors.append(f"translation key {key}: placeholder mismatch between English and Japanese")

        if source_root:
            literal, dynamic = _source_keys(source_root)
            defs = _def_names(z, names)
            for key in sorted(literal - set(en) - defs):
                errors.append(f"translation key {key} used in code but missing in English")
            for prefix in sorted(dynamic):
                if not any(k.startswith(prefix) for k in en):
                    errors.append(f"no translation key starts with the dynamic prefix {prefix}")

        for n in names:
            if not (n.startswith(ROOT + "Defs/") and n.endswith(".xml")):
                continue
            try:
                root = ET.fromstring(z.read(n))
            except ET.ParseError:
                continue
            for graphic in root.iter("graphicData"):
                tex = (graphic.findtext("texPath") or "").strip()
                cls = (graphic.findtext("graphicClass") or "").strip()
                if not tex:
                    continue
                wanted = [f"{tex}_{side}.png" for side in ("north", "east", "south")] if cls == "Graphic_Multi" else [f"{tex}.png"]
                for w in wanted:
                    if f"{ROOT}Textures/{w}" not in name_set:
                        errors.append(f"{n}: texture Textures/{w} is missing")
    return errors


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("zip")
    parser.add_argument("--source", help="C# source root to check translation keys against")
    args = parser.parse_args(argv)
    errors = validate_zip(args.zip, args.source)
    for e in errors:
        print("ERROR:", e)
    print("PASS" if not errors else f"FAIL ({len(errors)} problem(s))")
    return 0 if not errors else 1


if __name__ == "__main__":
    sys.exit(main())
