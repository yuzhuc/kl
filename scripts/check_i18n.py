#!/usr/bin/env python3
"""Check the GUI localization catalog and any un-migrated Chinese literals."""

from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import i18n_cs

ROOT = Path(__file__).resolve().parents[1]
GUI = ROOT / "gui" / "Kl.Gui"
CATALOG = GUI / "Resources" / "i18n" / "catalog.json"
XAML = [GUI / "MainWindow.axaml", *sorted((GUI / "Views").glob("*.axaml"))]
CJK = re.compile(r"[㐀-鿿]")
KEY = re.compile(r"\{DynamicResource (S\.[0-9a-f]{8})\}")
ATTR = re.compile(r'\b([A-Za-z_.:]+)="([^"]*[㐀-鿿][^"]*)"')
PLACEHOLDER = re.compile(r"(?<!\{)\{(\d+)(?:[,:][^}]*)?\}")
LOC_CALL = re.compile(r"(?:Localization|Loc)\.(?:T|F)\(\s*$")
# XAML Tag values are dead data today; keep them out of the localization scope.
XAML_ALLOWLIST = {"Tag"}

errors: list[str] = []


def check_catalog(catalog: dict[str, dict[str, str]]) -> None:
    by_chinese: dict[str, str] = {}
    for key, entry in catalog.items():
        zh, en = entry.get("zh", ""), entry.get("en", "")
        if not zh or not en:
            errors.append(f"catalog {key}: empty zh or en")
        if key != "S." + hashlib.sha256(zh.encode("utf-8")).hexdigest()[:8]:
            errors.append(f"catalog {key}: id does not match zh text")
        if zh in by_chinese:
            errors.append(f"catalog {key}: duplicate zh text (also {by_chinese[zh]})")
        by_chinese[zh] = key
        if set(PLACEHOLDER.findall(zh)) != set(PLACEHOLDER.findall(en)):
            errors.append(f"catalog {key}: placeholder mismatch zh={zh!r} en={en!r}")


def check_xaml(catalog: dict[str, dict[str, str]]) -> None:
    for path in XAML:
        source = path.read_text(encoding="utf-8")
        for key in KEY.findall(source):
            if key not in catalog:
                errors.append(f"{path.relative_to(ROOT)}: missing catalog key {key}")
        for match in ATTR.finditer(source):
            if match.group(1) in XAML_ALLOWLIST:
                continue
            errors.append(
                f"{path.relative_to(ROOT)}: untranslated {match.group(1)}=\"{match.group(2)}\"")


def check_csharp(catalog: dict[str, dict[str, str]]) -> None:
    for path in sorted(GUI.rglob("*.cs")):
        if "bin" in path.parts or "obj" in path.parts or path.name == "Localization.cs":
            continue
        source = path.read_text(encoding="utf-8")
        for start, end, prefix, is_interp, verbatim in i18n_cs.find_literals(source):
            raw = source[start + len(prefix) + 1 : end - 1]
            if not CJK.search(raw):
                continue
            if i18n_cs.line_has_keep(source, start):
                continue
            before = source[max(0, start - 40) : start]
            if LOC_CALL.search(before):
                continue
            line = source[source.rfind("\n", 0, start) + 1 : source.find("\n", start)]
            errors.append(f"{path.relative_to(ROOT)}: untranslated {line.strip()}")


def main() -> int:
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    check_catalog(catalog)
    check_xaml(catalog)
    check_csharp(catalog)
    if errors:
        print(f"{len(errors)} i18n problems:")
        for error in errors:
            print("  " + error)
        return 1
    print(f"i18n OK: {len(catalog)} catalog entries")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
