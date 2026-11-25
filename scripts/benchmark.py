from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import time
from typing import Any


ROOT = Path(__file__).resolve().parent.parent
FIXTURE_ROOT = ROOT / "benchmarks" / "fixtures"
MAX_TAIL_CHARS = 8192


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run or validate the KL benchmark task harness.")
    parser.add_argument("--tasks", type=Path, default=ROOT / "benchmarks" / "tasks.json")
    parser.add_argument("--kl", default="kl")
    parser.add_argument("--out", type=Path, default=ROOT / "benchmarks" / "results" / "latest.jsonl")
    parser.add_argument("--workspace-root", type=Path, default=ROOT / "benchmarks" / ".workspaces")
    parser.add_argument("--dry-run", action="store_true")
    parser.add_argument("--verify-only", action="store_true")
    parser.add_argument("--filter")
    parser.add_argument("--state", choices=("initial", "solution"))
    return parser.parse_args()


def validate_tasks(tasks: Any) -> None:
    if not isinstance(tasks, list):
        raise ValueError("tasks file must contain a JSON array")
    ids: set[str] = set()
    for index, task in enumerate(tasks):
        label = f"task[{index}]"
        if not isinstance(task, dict):
            raise ValueError(f"{label} must be an object")
        for field in ("id", "prompt"):
            value = task.get(field)
            if not isinstance(value, str) or not value.strip():
                raise ValueError(f"{label}.{field} must be a non-empty string")
        verify = task.get("verify")
        if isinstance(verify, list):
            if not verify or any(not isinstance(part, str) or part == "" for part in verify):
                raise ValueError(f"{label}.verify must be a non-empty array of strings")
        elif not isinstance(verify, str) or not verify.strip():
            raise ValueError(f"{label}.verify must be a non-empty string or array of strings")
        fixture = task.get("fixture")
        if isinstance(fixture, str):
            if not fixture.strip():
                raise ValueError(f"{label}.fixture must be a non-empty string")
        else:
            workspace = task.get("workspace")
            if not isinstance(workspace, str) or not workspace.strip():
                raise ValueError(f"{label}.workspace must be a non-empty string when no fixture is given")
        timeout = task.get("timeout_secs")
        if isinstance(timeout, bool) or not isinstance(timeout, int) or timeout <= 0:
            raise ValueError(f"{label}.timeout_secs must be a positive integer")
        if task["id"] in ids:
            raise ValueError(f"duplicate task id: {task['id']}")
        ids.add(task["id"])


def select_tasks(tasks: list[dict[str, Any]], filter_text: str | None) -> list[dict[str, Any]]:
    if not filter_text:
        return tasks
    selected = [task for task in tasks if filter_text in task["id"]]
    if not selected:
        raise ValueError(f"no task matches filter: {filter_text}")
    return selected


def expand_workspace(workspace: str) -> str:
    def replace(match: re.Match[str]) -> str:
        name = match.group(1)
        value = os.environ.get(name)
        if not value:
            raise ValueError(f"environment variable {name} is required by workspace {workspace}")
        return value
    return re.sub(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}", replace, workspace)


def tail_text(value: str) -> str:
    return value[-MAX_TAIL_CHARS:]


def spawn_target(file: str, args: list[str]) -> list[str]:
    if os.name == "nt" and re.search(r"\.(cmd|bat)$", file, re.IGNORECASE):
        return ["cmd.exe", "/d", "/c", "call", file, *args]
    return [file, *args]
def run_process(file: str, args: list[str], cwd: Path, timeout_secs: int) -> dict[str, Any]:
    started = time.monotonic()
    command = spawn_target(file, args)
    kwargs: dict[str, Any] = {
        "cwd": cwd,
        "env": os.environ.copy(),
        "stdin": subprocess.DEVNULL,
        "stdout": subprocess.PIPE,
        "stderr": subprocess.PIPE,
        "text": True,
        "encoding": "utf-8",
        "errors": "replace",
    }
    if os.name != "nt":
        kwargs["start_new_session"] = True
    else:
        kwargs["creationflags"] = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    try:
        process = subprocess.Popen(command, **kwargs)
    except OSError as error:
        return {
            "ok": False,
            "exit_code": None,
            "duration_ms": round((time.monotonic() - started) * 1000),
            "stdout_tail": "",
            "stderr_tail": "",
            "error": str(error),
        }
    timed_out = False
    try:
        stdout, stderr = process.communicate(timeout=timeout_secs)
    except subprocess.TimeoutExpired:
        timed_out = True
        if os.name == "nt":
            subprocess.run(
                ["taskkill", "/PID", str(process.pid), "/T", "/F"],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
            )
        else:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except OSError:
                process.kill()
        stdout, stderr = process.communicate()
    result: dict[str, Any] = {
        "ok": not timed_out and process.returncode == 0,
        "exit_code": process.returncode,
        "duration_ms": round((time.monotonic() - started) * 1000),
        "stdout_tail": tail_text(stdout or ""),
        "stderr_tail": tail_text(stderr or ""),
    }
    if timed_out:
        result["timed_out"] = True
    return result


def prepare_fixture(task: dict[str, Any], state: str, workspace_root: Path) -> tuple[Path, Path]:
    fixture_dir = FIXTURE_ROOT / task["fixture"]
    source = fixture_dir / state
    if not source.is_dir():
        raise ValueError(f"fixture {task['fixture']} has no {state} state")
    target = workspace_root / task["id"]
    if target.exists():
        shutil.rmtree(target)
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(source, target)
    return target, fixture_dir


def resolve_workspace(task: dict[str, Any], args: argparse.Namespace, state: str) -> tuple[Path, Path | None]:
    if "fixture" in task:
        return prepare_fixture(task, state, args.workspace_root.resolve())
    workspace = Path(expand_workspace(task["workspace"]))
    if args.verify_only and not workspace.exists():
        raise ValueError(f"workspace does not exist: {workspace}")
    return workspace, None


def expand_verify_command(command: str | list[str], workspace: Path, fixture_dir: Path | None) -> str | list[str]:
    def expand(part: str) -> str:
        value = part.replace("${PYTHON}", sys.executable)
        if fixture_dir is not None:
            value = value.replace("${FIXTURE_DIR}", str(fixture_dir))
        return value.replace("${WORKSPACE}", str(workspace))
    return [expand(part) for part in command] if isinstance(command, list) else expand(command)


def is_runnable_verify(command: str | list[str]) -> bool:
    text = " ".join(command) if isinstance(command, list) else command
    return re.match(r"^manual\s*:", text.strip(), re.IGNORECASE) is None


def run_verify(task: dict[str, Any], workspace: Path, fixture_dir: Path | None) -> dict[str, Any]:
    command = expand_verify_command(task["verify"], workspace, fixture_dir)
    if not is_runnable_verify(task["verify"]):
        return {
            "command": command,
            "skipped": True,
            "ok": False,
            "exit_code": None,
            "duration_ms": 0,
            "stdout_tail": "",
            "stderr_tail": "",
        }
    if isinstance(command, list):
        file, *args = command
    else:
        parts = command.strip().split()
        file, args = parts[0], parts[1:]
    result = run_process(file, args, workspace, task["timeout_secs"])
    return {"command": command, **result}
def run_task(task: dict[str, Any], args: argparse.Namespace) -> dict[str, Any]:
    state = args.state or ("solution" if args.verify_only else "initial")
    try:
        workspace, fixture_dir = resolve_workspace(task, args, state)
    except ValueError as error:
        return {
            "id": task["id"],
            "ok": False,
            "skipped": True,
            "exit_code": None,
            "duration_ms": 0,
            "stdout_tail": "",
            "stderr_tail": "",
            "error": str(error),
            "verify": {
                "command": task["verify"],
                "skipped": True,
                "ok": False,
                "exit_code": None,
                "duration_ms": 0,
                "stdout_tail": "",
                "stderr_tail": "",
            },
        }
    if args.verify_only:
        verify = run_verify(task, workspace, fixture_dir)
        return {
            "id": task["id"],
            "ok": verify["ok"],
            "skipped": bool(verify.get("skipped")),
            "exit_code": verify["exit_code"],
            "duration_ms": verify["duration_ms"],
            "stdout_tail": verify["stdout_tail"],
            "stderr_tail": verify["stderr_tail"],
            "workspace": str(workspace),
            "verify": verify,
        }
    run = run_process(
        args.kl,
        ["run", task["prompt"], "--workspace", str(workspace), "--yes"],
        ROOT,
        task["timeout_secs"],
    )
    verify = run_verify(task, workspace, fixture_dir)
    return {
        "id": task["id"],
        "ok": run["ok"] and (verify["ok"] or bool(verify.get("skipped"))),
        "exit_code": run["exit_code"],
        "duration_ms": run["duration_ms"],
        "stdout_tail": run["stdout_tail"],
        "stderr_tail": run["stderr_tail"],
        "workspace": str(workspace),
        "verify": verify,
    }


def main() -> int:
    args = parse_args()
    tasks = json.loads(args.tasks.read_text(encoding="utf-8-sig"))
    validate_tasks(tasks)
    selected = select_tasks(tasks, args.filter)
    if args.dry_run:
        print(f"Validated {len(tasks)} benchmark tasks.")
        print(f"Selected {len(selected)} task(s) for this run.")
        return 0
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text("", encoding="utf-8")
    failed = 0
    skipped = 0
    for index, task in enumerate(selected, start=1):
        print(f"[{index}/{len(selected)}] {task['id']}")
        result = run_task(task, args)
        with args.out.open("a", encoding="utf-8") as output:
            output.write(json.dumps(result, ensure_ascii=False) + "\n")
        if result["skipped"]:
            skipped += 1
            print(f"  SKIP {result.get('error', 'skipped')}")
            continue
        if not result["ok"]:
            failed += 1
            print(f"  FAIL exit={result['exit_code']}")
        else:
            print(f"  ok {result['duration_ms']}ms")
    print(f"Wrote {len(selected)} results to {args.out}")
    if skipped:
        print(f"Skipped {skipped} task(s).")
    return 1 if failed else 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(str(error), file=sys.stderr)
        sys.exit(1)