#!/usr/bin/env python3
"""Move Chinese C# literals into the i18n catalog.

Plain strings become Loc.T("..."); interpolated strings become
Loc.F("...{0}...", args). Literals on lines marked // i18n-keep are migration
or protocol data and stay as-is. Existing Loc.T/Loc.F arguments are registered
in the catalog without being rewritten.
"""

from __future__ import annotations

import hashlib
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
GUI = ROOT / "gui" / "Kl.Gui"
CATALOG = GUI / "Resources" / "i18n" / "catalog.json"
CJK = re.compile(r"[\u3400-\u9fff]")
ALIAS = "using Loc = Kl.Gui.Services.Localization;"
LOC_CALL = re.compile(r"(?:Localization|Loc)\.(?:T|F)\(\s*$")


def catalog_id(text: str) -> str:
    return "S." + hashlib.sha256(text.encode("utf-8")).hexdigest()[:8]


def find_literals(source: str) -> list[tuple[int, int, str, bool, bool]]:
    """Return (start, end, prefix, is_interp, is_verbatim) for string literals."""
    literals: list[tuple[int, int, str, bool, bool]] = []
    i, n = 0, len(source)
    while i < n:
        if source.startswith("//", i):
            newline = source.find("\n", i)
            if newline < 0:
                break
            i = newline
            continue
        if source.startswith("/*", i):
            close = source.find("*/", i + 2)
            i = n if close < 0 else close + 2
            continue
        if source[i] == "'":
            i += 1
            while i < n and source[i] != "'":
                i += 2 if source[i] == "\\" else 1
            i += 1
            continue
        prefix = ""
        if source.startswith('$@"', i) or source.startswith('@$"', i):
            prefix, quote = source[i : i + 2], i + 2
        elif source.startswith('$"', i):
            prefix, quote = "$", i + 1
        elif source.startswith('@"', i):
            prefix, quote = "@", i + 1
        elif source[i] == '"':
            prefix, quote = "", i
        else:
            i += 1
            continue
        verbatim = "@" in prefix
        k = quote + 1
        if verbatim:
            while k < n:
                if source[k] == '"':
                    if k + 1 < n and source[k + 1] == '"':
                        k += 2
                        continue
                    break
                k += 1
        else:
            while k < n:
                if source[k] == "\\":
                    k += 2
                    continue
                if source[k] == '"':
                    break
                k += 1
        literals.append((i, k + 1, prefix, "$" in prefix, verbatim))
        i = k + 1
    return literals


def decode(text: str, verbatim: bool) -> str:
    if verbatim:
        return text.replace('""', '"')
    out = []
    i = 0
    while i < len(text):
        ch = text[i]
        if ch != "\\":
            out.append(ch)
            i += 1
            continue
        i += 1
        if i >= len(text):
            break
        esc = text[i]
        i += 1
        mapping = {"n": "\n", "r": "\r", "t": "\t", "0": "\0",
                   "\\": "\\", '"': '"', "'": "'", "a": "\a", "b": "\b",
                   "f": "\f", "v": "\v"}
        if esc in mapping:
            out.append(mapping[esc])
        elif esc == "u" and i + 4 <= len(text):
            out.append(chr(int(text[i : i + 4], 16)))
            i += 4
        elif esc == "x":
            digits = ""
            while i < len(text) and len(digits) < 4 and text[i] in "0123456789abcdefABCDEF":
                digits += text[i]
                i += 1
            out.append(chr(int(digits, 16)))
        else:
            out.append(esc)
    return "".join(out)


def escape(text: str) -> str:
    return (text.replace("\\", "\\\\").replace('"', '\\"')
            .replace("\n", "\\n").replace("\r", "\\r").replace("\t", "\\t"))


def split_hole(expr: str) -> tuple[str, str, str]:
    """Split an interpolation hole into expression, alignment and format."""
    depth = 0
    quote = ""
    for index, ch in enumerate(expr):
        if quote:
            if ch == quote:
                quote = ""
            continue
        if ch in "\"'":
            quote = ch
            continue
        if ch in "([{":
            depth += 1
            continue
        if ch in ")]}":
            depth -= 1
            continue
        if depth == 0 and ch in ",:":
            rest = expr[index:]
            if ch == ",":
                fmt = ""
                colon = rest.find(":")
                if colon >= 0:
                    return expr[:index], rest[:colon], rest[colon:]
                return expr[:index], rest, ""
            return expr[:index], "", rest
    return expr, "", ""


def parse_interpolation(body: str, verbatim: bool) -> tuple[str, list[str]] | None:
    """Return (string.Format template, hole expressions)."""
    template_parts: list[str] = []
    arguments: list[str] = []
    text = ""
    i = 0
    while i < len(body):
        ch = body[i]
        if ch == "{" and i + 1 < len(body) and body[i + 1] == "{":
            text += "{"
            i += 2
            continue
        if ch == "}" and i + 1 < len(body) and body[i + 1] == "}":
            text += "}"
            i += 2
            continue
        if ch != "{":
            text += ch
            i += 1
            continue
        depth, quote, j, expr = 1, "", i + 1, ""
        while j < len(body):
            current = body[j]
            if quote:
                expr += current
                if current == "\\" and j + 1 < len(body) and not verbatim:
                    expr += body[j + 1]
                    j += 2
                    continue
                if current == quote:
                    quote = ""
                j += 1
                continue
            if current in "\"'":
                quote = current
                expr += current
                j += 1
                continue
            if current == "{":
                depth += 1
            elif current == "}":
                depth -= 1
                if depth == 0:
                    break
            expr += current
            j += 1
        if j >= len(body):
            return None
        value, align, fmt = split_hole(expr)
        template_parts.append((text, value, align, fmt))
        text = ""
        i = j + 1
    template_parts.append((text, "", "", ""))
    built = ""
    for chunk, value, align, fmt in template_parts:
        built += decode(chunk, verbatim).replace("{", "{{").replace("}", "}}")
        if value:
            built += "{" + str(len(arguments)) + align + fmt + "}"
            arguments.append(value.strip())
    return built, arguments


def line_has_keep(source: str, position: int) -> bool:
    start = source.rfind("\n", 0, position) + 1
    end = source.find("\n", position)
    return "i18n-keep" in source[start : end if end >= 0 else len(source)]


def main() -> int:
    write = "--check" not in sys.argv
    catalog = json.loads(CATALOG.read_text(encoding="utf-8"))
    added = 0
    rewritten = 0
    registered = 0
    skipped: list[str] = []
    edited: dict[Path, str] = {}

    for path in sorted(GUI.rglob("*.cs")):
        if "bin" in path.parts or "obj" in path.parts:
            continue
        source = path.read_text(encoding="utf-8")
        pieces: list[tuple[int, int, str]] = []
        for start, end, prefix, is_interp, verbatim in find_literals(source):
            raw = source[start + len(prefix) + 1 : end - 1]
            if is_interp:
                parsed = parse_interpolation(raw, verbatim)
                if parsed is None:
                    continue
                template, arguments = parsed
                if not CJK.search(template):
                    continue
                if not arguments:
                    # No holes survived: treat as a plain string.
                    is_interp = False
                    runtime_text = template
            else:
                runtime_text = decode(raw, verbatim)
                if not CJK.search(runtime_text):
                    continue
                template, arguments = runtime_text, []

            if line_has_keep(source, start):
                skipped.append(f"{path.relative_to(ROOT)}: {runtime_text}")
                continue

            before = source[max(0, start - 40) : start]
            wrapped = bool(LOC_CALL.search(before))
            if wrapped or "nameof(" in before:
                key = catalog_id(template)
                catalog.setdefault(key, {"zh": template, "en": ""})
                registered += 1
                continue

            after = source[end : end + 12]
            if re.match(r"\s*(=>|\bor\b)", after):
                skipped.append(f"{path.relative_to(ROOT)}: {runtime_text}")
                continue

            key = catalog_id(template)
            if key not in catalog:
                catalog[key] = {"zh": template, "en": ""}
                added += 1
            if is_interp:
                args = ", ".join(f"({argument})" for argument in arguments)
                call = f'Loc.F("{escape(template)}", {args})'
            else:
                call = f'Loc.T("{escape(runtime_text)}")'
            pieces.append((start, end, call))
            rewritten += 1

        if pieces:
            updated = source
            for start, end, call in reversed(pieces):
                updated = updated[:start] + call + updated[end:]
            if ALIAS not in updated:
                lines = updated.splitlines()
                last_using = -1
                for index, line in enumerate(lines):
                    if line.startswith("using "):
                        last_using = index
                    elif line.startswith("namespace "):
                        break
                if last_using >= 0:
                    lines.insert(last_using + 1, ALIAS)
                else:
                    lines.insert(0, ALIAS)
                updated = "\n".join(lines) + ("\n" if updated.endswith("\n") else "")
            edited[path] = updated

    if write:
        for path, content in edited.items():
            path.write_text(content, encoding="utf-8", newline="")
        CATALOG.write_text(
            json.dumps(dict(sorted(catalog.items())), ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
            newline="",
        )

    if skipped:
        print("Skipped (// i18n-keep or switch pattern):")
        for item in skipped:
            print("  " + item)
    print(f"files: {len(edited)}, literals rewritten: {rewritten}, "
          f"new entries: {added}, already wrapped: {registered}, total: {len(catalog)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
