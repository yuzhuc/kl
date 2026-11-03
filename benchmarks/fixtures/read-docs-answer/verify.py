from __future__ import annotations

from pathlib import Path
import re
import sys


root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.cwd()
answer = root / "answer.md"
if not answer.is_file():
    print("answer.md was not created", file=sys.stderr)
    raise SystemExit(1)

text = answer.read_text(encoding="utf-8")
problems = []
if not re.search(r"\b5\b", text):
    problems.append("answer.md does not state the maximum retry count (5)")
if "backoff" not in text.lower():
    problems.append("answer.md does not describe the backoff rule")
if not re.search(r"\b200\b", text):
    problems.append("answer.md does not state the base delay (200 ms)")
if problems:
    print("\n".join(problems), file=sys.stderr)
    raise SystemExit(1)
print("answer.md answers the retry policy question")
