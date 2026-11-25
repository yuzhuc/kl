from __future__ import annotations

from datetime import datetime, timezone
import json
import os
from pathlib import Path
import subprocess
import sys


ROOT = Path(__file__).resolve().parent.parent
FIXTURE_DIR = ROOT / "tests/fixtures/providers"
STATUS_PATH = FIXTURE_DIR / "live-smoke.status.json"


def capture(command: list[str]) -> subprocess.CompletedProcess[str]:
    try:
        result = subprocess.run(
            command, cwd=ROOT, env=os.environ.copy(), text=True, capture_output=True,
        )
    except OSError as error:
        raise SystemExit(f"{command[0]} could not start: {error}") from error
    print(result.stdout, end="")
    if result.stderr:
        print(result.stderr, end="", file=sys.stderr)
    return result


def live_provider(name: str, key_variable: str, output: Path) -> dict[str, object]:
    key = os.environ.get(key_variable)
    if not key or not key.strip():
        return {"status": "pending", "reason": f"{key_variable} is not set", "fixture": None}

    print(f"\n== live smoke: {name} ==")
    result = capture([
        "cargo", "run", "-q", "-p", "kl-provider", "--example", "live_smoke", "--",
        "--protocol", name, "--key-env", key_variable, "--out", str(output),
    ])
    if result.returncode != 0:
        return {
            "status": "failed",
            "reason": f"live smoke exited with code {result.returncode}",
            "fixture": None,
        }
    return {"status": "ok", "reason": None, "fixture": str(output.resolve())}


def live_mcp(output: Path) -> dict[str, object]:
    print("\n== live smoke: mcp ==")
    result = capture([
        "cargo", "run", "-q", "-p", "kl-core", "--example", "mcp_smoke", "--",
        "--out", str(output),
    ])
    record = None
    for line in result.stdout.splitlines():
        if not line.lstrip().startswith("{"):
            continue
        try:
            record = json.loads(line)
        except json.JSONDecodeError:
            continue
    if record is None:
        return {
            "status": "failed",
            "reason": "MCP smoke produced no status record",
            "fixture": None,
            "servers": [],
        }
    status = record.get("status")
    return {
        "status": status,
        "reason": record.get("reason"),
        "fixture": str(output.resolve()) if status == "ok" else None,
        "servers": record.get("servers", []),
    }


def main() -> None:
    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)
    providers = {
        "openai-chat": live_provider(
            "openai-chat", "OPENAI_API_KEY", FIXTURE_DIR / "openai-chat-live.json",
        ),
        "anthropic": live_provider(
            "anthropic", "ANTHROPIC_API_KEY", FIXTURE_DIR / "anthropic-live.json",
        ),
        "openai-responses": live_provider(
            "openai-responses", "OPENAI_API_KEY", FIXTURE_DIR / "openai-responses-live.json",
        ),
    }
    mcp = live_mcp(FIXTURE_DIR / "mcp-live.json")

    status = {
        "generated_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%fZ"),
        "note": (
            "Live fixtures are written only when the real provider call succeeds. "
            "Skips are recorded as pending, never fabricated."
        ),
        "providers": providers,
        "mcp": mcp,
    }
    STATUS_PATH.write_text(
        json.dumps(status, indent=2) + "\n", encoding="utf-8", newline="\n",
    )
    print(f"status: {STATUS_PATH}")

    failed = [value for value in providers.values() if value["status"] == "failed"]
    if mcp["status"] == "failed":
        failed.append(mcp)
    if failed:
        raise SystemExit(1)
    if all(value["status"] == "pending" for value in providers.values()) and mcp["status"] == "pending":
        print(
            "Live smoke is pending: set OPENAI_API_KEY and/or ANTHROPIC_API_KEY, "
            "configure an MCP server in ~/.kl/mcp.toml, then rerun this script."
        )


main()
