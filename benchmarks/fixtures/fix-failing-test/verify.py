from __future__ import annotations

from pathlib import Path
import re
import sys


root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.cwd()
calc = root / "src" / "calc.js"
if not calc.is_file():
    print("src/calc.js is missing", file=sys.stderr)
    raise SystemExit(1)

text = calc.read_text(encoding="utf-8")
problems = []
if not re.search(r"values\.length\s*===\s*0", text):
    problems.append("calc.js does not guard the empty list")
if not re.search(r"return\s+0", text):
    problems.append("calc.js does not return zero for an empty list")
if "reduce(" not in text:
    problems.append("calc.js no longer computes the average with reduce")
if problems:
    print("\n".join(problems), file=sys.stderr)
    raise SystemExit(1)
print("fix-failing-test fixture passed")
