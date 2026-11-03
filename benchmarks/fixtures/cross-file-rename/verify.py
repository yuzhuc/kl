from __future__ import annotations

from pathlib import Path
import re
import sys


root = Path(sys.argv[1]) if len(sys.argv) > 1 else Path.cwd()
src = root / "src"
problems: list[str] = []
if not src.is_dir():
    print("src directory is missing", file=sys.stderr)
    raise SystemExit(1)

for file in sorted(src.glob("*.js")):
    if "LEGACY_TIMEOUT" in file.read_text(encoding="utf-8"):
        problems.append(f"{file.name} still references LEGACY_TIMEOUT")

config = (src / "config.js").read_text(encoding="utf-8") if (src / "config.js").is_file() else ""
client = (src / "client.js").read_text(encoding="utf-8") if (src / "client.js").is_file() else ""
index = (src / "index.js").read_text(encoding="utf-8") if (src / "index.js").is_file() else ""
if not re.search(r"export\s+const\s+TIMEOUT_MS\s*=\s*5000", config):
    problems.append("config.js does not export TIMEOUT_MS = 5000")
if "TIMEOUT_MS" not in client or not re.search(r"return\s+TIMEOUT_MS", client):
    problems.append("client.js does not return TIMEOUT_MS")
if not re.search(r"export\s+\{[^}]*TIMEOUT_MS[^}]*\}", index):
    problems.append("index.js does not export TIMEOUT_MS")
if not re.search(r"export\s+\{[^}]*requestTimeout[^}]*\}", index):
    problems.append("index.js does not export requestTimeout")

if problems:
    print("\n".join(problems), file=sys.stderr)
    raise SystemExit(1)
print("cross-file rename fixture passed")
