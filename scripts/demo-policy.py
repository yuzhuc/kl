from __future__ import annotations

import json
import os
from pathlib import Path
import shutil
import subprocess
import sys


ROOT = Path(__file__).resolve().parent.parent


def run(command: list[str]) -> None:
    print(f"\n== {' '.join(command)} ==")
    try:
        result = subprocess.run(command, cwd=ROOT, env=os.environ.copy())
    except OSError as error:
        raise SystemExit(f"{command[0]} could not start: {error}") from error
    if result.returncode != 0:
        raise SystemExit(result.returncode)


def cargo_target_directory() -> Path:
    result = subprocess.run(
        ["cargo", "metadata", "--no-deps", "--format-version", "1"],
        cwd=ROOT,
        env=os.environ.copy(),
        text=True,
        capture_output=True,
    )
    if result.returncode != 0:
        raise SystemExit(result.stderr.strip() or "cargo metadata failed")
    return Path(json.loads(result.stdout)["target_directory"]).resolve()


def tui_cli() -> Path:
    name = "kl.exe" if sys.platform.startswith("win") else "kl"
    return ROOT / "tui/Kl.Tui/bin/Debug/net10.0" / name


def main() -> None:
    run(["cargo", "build", "--workspace"])
    run(["dotnet", "build", "kl.sln"])

    target = cargo_target_directory()
    demo = (target / "policy-demo").resolve()
    if not demo.is_relative_to(target):
        raise SystemExit(f"refusing to operate outside target/: {demo}")
    if demo.exists():
        shutil.rmtree(demo)
    shutil.copytree(ROOT / "examples/policy-demo-template", demo)

    kl = tui_cli()
    config = demo / "kl.toml"
    kl_home = demo / ".kl"

    print("\n== deny checks (even with --yes) ==")
    run([
        str(kl), "run", "Try the dangerous operations",
        "--workspace", str(demo), "--config", str(config), "--home", str(kl_home), "--yes",
    ])

    audit = kl_home / "audit/events.jsonl"
    if not audit.is_file():
        raise SystemExit("audit log was not created")
    records = [
        json.loads(line)
        for line in audit.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    denied = [record for record in records if record.get("decision") == "deny"]
    if len(denied) < 2:
        raise SystemExit(f"expected at least 2 denied audit records, got {len(denied)}")
    for record in denied:
        if record.get("ok"):
            raise SystemExit("denied record unexpectedly succeeded")
        print(f"denied: {record.get('tool')} -> {record.get('resource')}")

    print(f"Policy demo passed: {demo}")


main()
