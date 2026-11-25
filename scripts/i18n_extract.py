#!/usr/bin/env python3
"""Extract Chinese XAML literals into the i18n catalog and rewrite them as keys.

IDs are stable: S. + first 8 hex chars of SHA-256 of the Chinese text. Existing
catalog entries are never overwritten, so English translations survive reruns.
"""

from __future__ import annotations

import hashlib
import html
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CATALOG = ROOT / "gui" / "Kl.Gui" / "Resources" / "i18n" / "catalog.json"
FILES = [
    ROOT / "gui" / "Kl.Gui" / "MainWindow.axaml",
    *sorted((ROOT / "gui" / "Kl.Gui" / "Views").glob("*.axaml")),
]
ATTRIBUTES = r"(?:Text|Content|ToolTip\.Tip|PlaceholderText|Header)"
LITERAL = re.compile(
    r'(\b' + ATTRIBUTES + r'=")([^"]*[㐀-鿿][^"]*)(")'
)
CJK = re.compile(r"[㐀-鿿]")


def catalog_id(text: str) -> str:
    return "S." + hashlib.sha256(text.encode("utf-8")).hexdigest()[:8]


def load_catalog() -> dict[str, dict[str, str]]:
    if CATALOG.exists():
        return json.loads(CATALOG.read_text(encoding="utf-8"))
    return {}


def main() -> int:
    write = "--check" not in sys.argv
    catalog = load_catalog()
    added = 0
    rewritten = 0
    missing: list[str] = []

    for path in FILES:
        source = path.read_text(encoding="utf-8")

        def replace(match: re.Match[str]) -> str:
            nonlocal added, rewritten
            text = html.unescape(match.group(2))
            key = catalog_id(text)
            if key not in catalog:
                catalog[key] = {"zh": text, "en": ""}
                added += 1
            rewritten += 1
            return f"{match.group(1)}{{DynamicResource {key}}}{match.group(3)}"

        updated = LITERAL.sub(replace, source)
        if write and updated != source:
            path.write_text(updated, encoding="utf-8", newline="")
        elif not write:
            for match in LITERAL.finditer(source):
                missing.append(f"{path.relative_to(ROOT)}: {match.group(2)}")

    if write:
        CATALOG.parent.mkdir(parents=True, exist_ok=True)
        CATALOG.write_text(
            json.dumps(dict(sorted(catalog.items())), ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
            newline="",
        )

    if not write and missing:
        print("Unmigrated XAML literals:")
        for item in missing:
            print("  " + item)
        return 1

    empty = sum(1 for entry in catalog.values() if not entry.get("zh") or not entry.get("en"))
    print(f"xaml literals: {rewritten}, new catalog entries: {added}, total: {len(catalog)}")
    print(f"entries missing zh/en: {empty}")
    return 0 if write else 0


if __name__ == "__main__":
    raise SystemExit(main())
