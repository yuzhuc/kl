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
DEFAULT_OUTPUT = ROOT / "bin"


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
    # Keep the directory itself: on Windows a shell cd'd into it locks the
    # directory handle, but its contents can still be replaced.
    path.mkdir(parents=True, exist_ok=True)
    for child in path.iterdir():
        try:
            if child.is_dir():
                shutil.rmtree(child)
            else:
                child.unlink()
        except PermissionError as error:
            raise SystemExit(
                f"cannot replace {child}: close any running app from {path} and retry"
            ) from error


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
    single_file: bool,
) -> Path:
    cargo_profile = "release" if configuration.lower() == "release" else "debug"
    core_name = "kl-core.exe" if runtime.startswith("win") else "kl-core"
    runner_name = "kl-sandbox-runner.exe" if runtime.startswith("win") else "kl-sandbox-runner"
    core = ROOT / "target" / cargo_profile / core_name
    runner = ROOT / "target" / cargo_profile / runner_name

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
        "true" if self_contained else "false",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
    ]
    if single_file:
        common.extend([
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
        ])
    run(["dotnet", "publish", "gui/Kl.Gui/Kl.Gui.csproj", *common, "-o", str(gui_output)], env)
    run(["dotnet", "publish", "tui/Kl.Tui/Kl.Tui.csproj", *common, "-o", str(tui_output)], env)

    if not core.is_file():
        raise SystemExit(f"core binary not found: {core}")
    if not runner.is_file():
        raise SystemExit(f"sandbox runner binary not found: {runner}")
    for app_output in (gui_output, tui_output):
        shutil.copy2(core, app_output / core_name)
        shutil.copy2(runner, app_output / runner_name)

    schema_target = tui_output / "schemas"
    if schema_target.exists():
        shutil.rmtree(schema_target)
    shutil.copytree(ROOT / "schemas", schema_target)

    remove_diagnostics(gui_output)
    remove_diagnostics(tui_output)

    if runtime.startswith("win"):
        (output / "run-gui.bat").write_text(
            '@echo off\nstart "" "%~dp0gui\\kl.exe"\n',
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
            '#!/usr/bin/env sh\nSCRIPT_DIR=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexec "$SCRIPT_DIR/gui/kl" "$@"\n',
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
    parser.add_argument("--skip-checks", action="store_true")
    deployment = parser.add_mutually_exclusive_group()
    deployment.add_argument(
        "--self-contained", dest="self_contained", action="store_true", default=True,
        help="include the .NET runtime in bin/gui and bin/tui (default)",
    )
    deployment.add_argument(
        "--framework-dependent", dest="self_contained", action="store_false",
        help="omit the .NET runtime for a smaller output",
    )
    deployment.add_argument(
        "--single-file", action="store_true",
        help="bundle the app, dependencies and runtime into one executable",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    host = host_runtime()
    if args.runtime != host:
        raise SystemExit(
            f"--runtime {args.runtime} does not match the host {host}; "
            "use scripts/package.py for cross-RID builds"
        )
    env = build_env()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)

    if not args.skip_checks:
        run(["cargo", "fmt", "--all", "--", "--check"], env)
        run(["cargo", "clippy", "--workspace", "--all-targets", "--", "-D", "warnings"], env)
        run(["cargo", "test", "--workspace"], env)
        run(["dotnet", "test", "kl.sln", "-c", args.configuration], env)

    profile = ["--release"] if args.configuration.lower() == "release" else []
    run(["cargo", "build", "-p", "kl-core", *profile], env)
    run(["cargo", "build", "-p", "kl-sandbox", "--bin", "kl-sandbox-runner", *profile], env)

    core = publish_apps(
        args.configuration, args.runtime, output, env, args.self_contained, args.single_file,
    )
    print(f"\nCaches: {ROOT / 'target'}")
    print(f"Final outputs: {output}")
    print(f"GUI: {output / 'gui'}")
    print(f"TUI: {output / 'tui'}")
    print(f"Core source: {core}")
    if args.single_file:
        deployment = "self-contained single-file"
    elif args.self_contained:
        deployment = "self-contained"
    else:
        deployment = "framework-dependent (lean local build)"
    print(f"Deployment: {deployment}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
