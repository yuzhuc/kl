from __future__ import annotations

import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys

from package_format import is_diagnostic_file


ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUTPUT = ROOT.parent / "bin"


def run(command: list[str], env: dict[str, str] | None = None) -> None:
    print(f"\n== {' '.join(command)} ==")
    try:
        result = subprocess.run(command, cwd=ROOT, env=env or os.environ.copy())
    except OSError as error:
        raise SystemExit(f"{command[0]} could not start: {error}") from error
    if result.returncode != 0:
        raise SystemExit(result.returncode)


def build_env() -> dict[str, str]:
    env = os.environ.copy()
    env["CARGO_TARGET_DIR"] = str(ROOT / "target")
    return env


def clean_directory(path: Path) -> None:
    if path.exists():
        shutil.rmtree(path)
    path.mkdir(parents=True, exist_ok=True)


def remove_diagnostics(path: Path) -> None:
    for item in path.rglob("*"):
        if item.is_file() and is_diagnostic_file(item.name):
            item.unlink()


def publish_apps(configuration: str, runtime: str, output: Path, env: dict[str, str]) -> Path:
    cargo_profile = "release" if configuration.lower() == "release" else "debug"
    core_name = "kl-core.exe" if runtime.startswith("win") else "kl-core"
    core = ROOT / "target" / cargo_profile / core_name

    gui_output = output / "gui"
    tui_output = output / "tui"
    clean_directory(gui_output)
    clean_directory(tui_output)

    common = [
        "-c",
        configuration,
        "-r",
        runtime,
        "--self-contained",
        "true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
    ]
    run(["dotnet", "publish", "gui/DeepSeekHarness.Gui/DeepSeekHarness.Gui.csproj", *common, "-o", str(gui_output)], env)
    run(["dotnet", "publish", "tui/Kl.Tui/Kl.Tui.csproj", *common, "-o", str(tui_output)], env)

    if not core.is_file():
        raise SystemExit(f"core binary not found: {core}")
    shutil.copy2(core, gui_output / core_name)
    shutil.copy2(core, tui_output / core_name)

    schema_target = tui_output / "schemas"
    if schema_target.exists():
        shutil.rmtree(schema_target)
    shutil.copytree(ROOT / "schemas", schema_target)

    remove_diagnostics(gui_output)
    remove_diagnostics(tui_output)

    if runtime.startswith("win"):
        (output / "run-gui.bat").write_text(
            '@echo off\nstart "" "%~dp0gui\\DeepSeekHarness.Gui.exe"\n',
            encoding="utf-8",
            newline="\r\n",
        )
        (output / "run-tui.bat").write_text(
            '@echo off\n"%~dp0tui\\kl.exe" %*\n',
            encoding="utf-8",
            newline="\r\n",
        )
    return core


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build KL, keep caches in the repository, and stage final files in bin.")
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--runtime", default="win-x64", help="dotnet publish runtime identifier")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--skip-checks", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    env = build_env()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    if not args.skip_checks:
        run(["cargo", "fmt", "--all", "--", "--check"], env)
        run(["cargo", "clippy", "--workspace", "--all-targets", "--", "-D", "warnings"], env)
        run(["cargo", "test", "--workspace"], env)
        run(["dotnet", "test", "kl.sln", "-c", args.configuration], env)

    cargo_build = ["cargo", "build", "-p", "kl-core"]
    if args.configuration.lower() == "release":
        cargo_build.append("--release")
    run(cargo_build, env)

    core = publish_apps(args.configuration, args.runtime, output, env)
    print(f"\nCaches: {ROOT / 'target'}")
    print(f"Final outputs: {output}")
    print(f"GUI: {output / 'gui'}")
    print(f"TUI: {output / 'tui'}")
    print(f"Core source: {core}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
