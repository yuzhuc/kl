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


def capture(command: list[str]) -> tuple[int, str]:
    try:
        result = subprocess.run(
            command, cwd=ROOT, env=os.environ.copy(), text=True, capture_output=True,
        )
    except OSError as error:
        raise SystemExit(f"{command[0]} could not start: {error}") from error
    print(result.stdout, end="")
    if result.stderr:
        print(result.stderr, end="", file=sys.stderr)
    return result.returncode, result.stdout + result.stderr


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


def write_text(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding="utf-8", newline="\n")


def write_json(path: Path, value: object) -> None:
    write_text(path, json.dumps(value, indent=2) + "\n")


def plugin_source(base: Path, target: Path, version: str, config_version: str) -> Path:
    source = base / f"source-{version}" / "fake-tool"
    manifest = {
        "id": "fake-tool",
        "version": version,
        "api": 1,
        "kind": "tool",
        "entrypoint": str(target / "debug" / fake_plugin_name()),
        "runtime": "sidecar",
        "capabilities": ["tool"],
        "permissions": {
            "network": ["example.com"],
            "env": [],
            "fs_read": ["workspace"],
            "fs_write": [],
        },
        "tool_name": "fake_tool",
        "tool_parameters": {"type": "object", "properties": {}, "additionalProperties": False},
        "config_path": "config.json",
        "health_timeout_ms": 3000,
        "drain_timeout_ms": 1000,
    }
    write_json(source / "manifest.json", manifest)
    write_json(source / "config.json", {"version": config_version})
    return source


def provider_source(base: Path, target: Path, version: str, text: str) -> Path:
    source = base / f"provider-{version}" / "fake-provider"
    manifest = {
        "id": "fake-provider",
        "version": version,
        "api": 1,
        "kind": "provider",
        "entrypoint": str(target / "debug" / fake_plugin_name()),
        "runtime": "sidecar",
        "capabilities": ["chat", "streaming"],
        "permissions": {"network": [], "env": [], "fs_read": [], "fs_write": []},
        "models": ["fake-model"],
        "config_path": "config.json",
        "health_timeout_ms": 3000,
        "drain_timeout_ms": 1000,
    }
    write_json(source / "manifest.json", manifest)
    write_json(source / "config.json", {"provider_text": text})
    return source


def fake_plugin_name() -> str:
    return "fake_plugin.exe" if sys.platform.startswith("win") else "fake_plugin"


def main() -> None:
    run(["cargo", "build", "--workspace"])
    run(["dotnet", "build", "kl.sln"])

    target = cargo_target_directory()
    base = (target / "plugin-demo").resolve()
    if not base.is_relative_to(target):
        raise SystemExit(f"refusing to operate outside target/: {base}")
    if base.exists():
        shutil.rmtree(base)

    workspace = base / "workspace"
    kl_home = base / "home"
    (workspace / "src").mkdir(parents=True)
    kl_home.mkdir(parents=True)
    write_text(workspace / "src/lib.rs", "pub fn value() -> u8 { 1 }\n")

    config_path = base / "kl.toml"
    write_text(config_path, """[agent]
provider = "scripted"
model = "scripted"
scripted_path = "script.json"
""")
    write_text(base / "script.json", """[
  {"tool_calls":[{"id":"plugin-call","name":"fake_tool","arguments":{}}]},
  {"text":"plugin tool executed"}
]
""")

    kl = str(tui_cli())
    v1 = plugin_source(base, target, "1.0.0", "1")
    v2 = plugin_source(base, target, "2.0.0", "2")

    print("\n== install v1 ==")
    run([kl, "plugin", "install", str(v1), "--config", str(config_path), "--home", str(kl_home)])

    print("\n== run v1 tool ==")
    code, output = capture([
        kl, "run", "use the plugin tool",
        "--workspace", str(workspace), "--config", str(config_path), "--home", str(kl_home), "--yes",
    ])
    if code != 0 or "fake tool 1 ok" not in output:
        raise SystemExit("v1 plugin tool did not execute")

    print("\n== hot upgrade to v2 ==")
    run([kl, "plugin", "install", str(v2), "--config", str(config_path), "--home", str(kl_home)])

    print("\n== run v2 tool ==")
    code, output = capture([
        kl, "run", "use the upgraded plugin tool",
        "--workspace", str(workspace), "--config", str(config_path), "--home", str(kl_home), "--yes",
    ])
    if code != 0 or "fake tool 2 ok" not in output:
        raise SystemExit("v2 plugin tool did not execute")

    print("\n== install provider plugin ==")
    provider = provider_source(base, target, "1.0.0", "fake provider plugin response")
    run([kl, "plugin", "install", str(provider), "--config", str(config_path), "--home", str(kl_home)])

    print("\n== run provider plugin ==")
    code, output = capture([
        kl, "run", "say hello", "--provider", "fake-provider", "--model", "fake-model",
        "--workspace", str(workspace), "--config", str(config_path), "--home", str(kl_home), "--yes",
    ])
    if code != 0 or "fake provider plugin response" not in output:
        raise SystemExit("provider plugin did not execute")

    print("\n== plugin list ==")
    run([kl, "plugin", "list", "--config", str(config_path), "--home", str(kl_home)])

    print("\n== audit ==")
    run([kl, "audit", "20", "--config", str(config_path), "--home", str(kl_home)])

    print(f"Plugin demo passed: {base}")


main()
