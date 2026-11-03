from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

from package_format import assert_binary_format, is_diagnostic_file, layout_for


ROOT = Path(__file__).resolve().parent.parent


def run(command: list[str], cwd: Path = ROOT) -> None:
    print(f"\n== {' '.join(command)} ==")
    try:
        result = subprocess.run(command, cwd=cwd, env=os.environ.copy())
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
    return Path(json.loads(result.stdout)["target_directory"])


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build and package a KL release bundle.")
    parser.add_argument("--rid", required=True)
    parser.add_argument("--rust-target", required=True)
    parser.add_argument("--zig", action="store_true")
    parser.add_argument("--self-contained", action="store_true", help="include the .NET runtime in the package")
    parser.add_argument("--output")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    layout = layout_for(args.rid)
    output = Path(args.output).resolve() if args.output else (ROOT / "dist" / args.rid).resolve()
    archive = output.parent / f"kl-{args.rid}{layout['ext']}"
    cargo_target = cargo_target_directory()

    if output.exists():
        shutil.rmtree(output)
    output.mkdir(parents=True, exist_ok=True)
    archive.parent.mkdir(parents=True, exist_ok=True)

    cargo_command = "zigbuild" if args.zig else "build"
    run(["cargo", cargo_command, "--release", "--target", args.rust_target, "-p", "kl-core"])
    run(
        [
            "dotnet",
            "publish",
            "tui/Kl.Tui/Kl.Tui.csproj",
            "-c",
            "Release",
            "-r",
            args.rid,
            "--self-contained",
            str(args.self_contained).lower(),
            "-p:DebugType=None",
            "-p:DebugSymbols=false",
            "-o",
            str(output),
        ]
    )

    for item in output.iterdir():
        if item.is_file() and is_diagnostic_file(item.name):
            item.unlink()

    core = cargo_target / args.rust_target / "release" / str(layout["core"])
    if not core.is_file():
        raise SystemExit(f"core binary not found: {core}")
    shutil.copy2(core, output / str(layout["core"]))

    schema_target = output / "schemas"
    if schema_target.exists():
        shutil.rmtree(schema_target)
    shutil.copytree(ROOT / "schemas", schema_target)

    if not args.rid.startswith("win"):
        for name in ("kl", "kl-core"):
            file = output / name
            if file.exists():
                file.chmod(file.stat().st_mode | 0o111)

    archive.unlink(missing_ok=True)
    names = sorted(item.name for item in output.iterdir())
    if args.rid.startswith("win"):
        run(["tar", "-a", "-c", "-f", str(archive), "-C", str(output), *names])
    else:
        run(["tar", "-czf", str(archive), "-C", str(output), *names])

    assert_binary_format(output / str(layout["exe"]), args.rid)
    assert_binary_format(output / str(layout["core"]), args.rid)

    checksum = archive.with_name(archive.name + ".sha256")
    checksum.unlink(missing_ok=True)

    print(f"\nPackaged {output}\nArchive {archive}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
