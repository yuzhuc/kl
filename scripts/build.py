from __future__ import annotations

import argparse
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys

from package_format import is_diagnostic_file


ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUTPUT = ROOT.parent / "bin"


def host_runtime() -> str:
    machine = platform.machine().lower()
    arch = "arm64" if machine in {"arm64", "aarch64"} else "x64"
    if sys.platform.startswith("win"):
        return f"win-{arch}"
    if sys.platform == "darwin":
        return f"osx-{arch}"
    if sys.platform.startswith("linux"):
        return f"linux-{arch}"
    raise SystemExit(f"unsupported host platform: {sys.platform} ({machine})")


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


def publish_apps(
    configuration: str,
    runtime: str,
    output: Path,
    env: dict[str, str],
    self_contained: bool,
) -> Path:
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
        str(self_contained).lower(),
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
    ]
    run(["dotnet", "publish", "gui/DeepSeekHarness.Gui/DeepSeekHarness.Gui.csproj", *common, "-o", str(gui_output)], env)
    run(["dotnet", "publish", "tui/Kl.Tui/Kl.Tui.csproj", *common, "-o", str(tui_output)], env)

    if not core.is_file():
        raise SystemExit(f"core binary not found: {core}")
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
    else:
        gui_launcher = output / "run-gui.sh"
        tui_launcher = output / "run-tui.sh"
        gui_launcher.write_text(
            '#!/usr/bin/env sh\nSCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexec "$SCRIPT_DIR/gui/DeepSeekHarness.Gui" "$@"\n',
            encoding="utf-8",
            newline="\n",
        )
        tui_launcher.write_text(
            '#!/usr/bin/env sh\nSCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexec "$SCRIPT_DIR/tui/kl" "$@"\n',
            encoding="utf-8",
            newline="\n",
        )
        gui_launcher.chmod(0o755)
        tui_launcher.chmod(0o755)
    return core


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Build KL, keep caches in the repository, and stage final files in bin.")
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--runtime", default=host_runtime(), help="dotnet publish runtime identifier")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--self-contained", action="store_true", help="include the .NET runtime for portable output")
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

    core = publish_apps(args.configuration, args.runtime, output, env, args.self_contained)
    print(f"\nCaches: {ROOT / 'target'}")
    print(f"Final outputs: {output}")
    print(f"GUI: {output / 'gui'}")
    print(f"TUI: {output / 'tui'}")
    print(f"Core source: {core}")
    print(f"Deployment: {'self-contained' if args.self_contained else 'framework-dependent'}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
