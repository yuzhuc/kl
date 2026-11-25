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
    demo = (target / "e2e-demo").resolve()
    if not demo.is_relative_to(target):
        raise SystemExit(f"refusing to operate outside target/: {demo}")
    if demo.exists():
        shutil.rmtree(demo)
    shutil.copytree(ROOT / "examples/e2e-demo-template", demo)

    kl = tui_cli()
    config = demo / "kl.toml"
    kl_home = demo / ".kl"

    print("\n== agent task ==")
    run([
        str(kl), "run", "Fix the failing test and run cargo test",
        "--workspace", str(demo), "--config", str(config), "--home", str(kl_home), "--yes",
    ])

    lib = (demo / "src/lib.rs").read_text(encoding="utf-8")
    if "a + b" not in lib:
        raise SystemExit("agent did not apply the expected edit")

    print("\n== audit ==")
    run([str(kl), "audit", "20", "--config", str(config), "--home", str(kl_home)])

    audit = kl_home / "audit/events.jsonl"
    if not audit.is_file():
        raise SystemExit("audit log was not created")
    records = [
        json.loads(line)
        for line in audit.read_text(encoding="utf-8").splitlines()
        if line.strip()
    ]
    if len(records) < 3:
        raise SystemExit(f"expected at least 3 audit records, got {len(records)}")
    for record in records:
        if not record.get("ok") and record.get("decision") != "deny":
            raise SystemExit(f"unexpected failed audit record: {record}")

    print(f"E2E demo passed: {demo}")


main()
