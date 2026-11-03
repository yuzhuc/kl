from __future__ import annotations

import json
from pathlib import Path
import re
import sys
from typing import Callable


ROOT = Path(__file__).resolve().parent.parent
ReadFile = Callable[[str], str | None]
Pattern = re.Pattern[str]


def must(read: ReadFile, file: str, pattern: Pattern, note: str) -> str | None:
    text = read(file)
    if text is None:
        return f"{file}: file is missing"
    if pattern.search(text) is None:
        return f"{file}: expected {note}"
    return None


def must_not(read: ReadFile, file: str, pattern: Pattern, note: str) -> str | None:
    text = read(file)
    if text is None:
        return f"{file}: file is missing"
    if pattern.search(text) is not None:
        return f"{file}: still claims {note}"
    return None


def check_pty(read: ReadFile) -> list[str]:
    return [
        value
        for value in [
            must(read, "README.md", re.compile(r"`tty=true` uses portable-pty"), "the portable-pty PTY note"),
            must_not(read, "README.md", re.compile(r"add PTY"), "PTY as an upcoming feature"),
            must(read, "docs/architecture/overview.md", re.compile(r"Shell PTY is implemented"), "the PTY status line"),
            must(read, "crates/kl-tools/src/pty.rs", re.compile(r"portable_pty"), "the portable-pty implementation"),
            must(read, "crates/kl-tools/Cargo.toml", re.compile(r"portable-pty"), "the portable-pty dependency"),
        ]
        if value is not None
    ]


def check_sandbox_backends(read: ReadFile) -> list[str]:
    return [
        value
        for value in [
            must(read, "README.md", re.compile(r"`bubblewrap`"), "the bubblewrap backend"),
            must(read, "README.md", re.compile(r"Seatbelt"), "the Seatbelt backend"),
            must(read, "docs/security/policy.md", re.compile(r"bubblewrap"), "the bubblewrap backend"),
            must(read, "docs/security/policy.md", re.compile(r"Seatbelt"), "the Seatbelt backend"),
            must(read, "docs/architecture/overview.md", re.compile(r"bubblewrap"), "the bubblewrap backend"),
            must(read, "docs/architecture/overview.md", re.compile(r"Seatbelt"), "the Seatbelt backend"),
            must(read, "crates/kl-sandbox/src/linux.rs", re.compile(r"bubblewrap"), "the bubblewrap backend"),
            must(read, "crates/kl-sandbox/src/macos.rs", re.compile(r"Seatbelt"), "the Seatbelt backend"),
        ]
        if value is not None
    ]


def check_mcp_sandbox(read: ReadFile) -> list[str]:
    return [
        value
        for value in [
            must(read, "README.md", re.compile(r"MCP server processes start through the active `SandboxManager`"), "the sandboxed MCP note"),
            must(read, "docs/security/policy.md", re.compile(r"start through the active sandbox"), "the sandboxed MCP note"),
            must(read, "crates/kl-core/src/mcp.rs", re.compile(r"sandbox\.spawn\("), "the sandboxed MCP spawn"),
        ]
        if value is not None
    ]


def check_allow_session(read: ReadFile) -> list[str]:
    return [
        value
        for value in [
            must(read, "README.md", re.compile(r"allow for this session"), "the session approval note"),
            must(read, "docs/security/policy.md", re.compile(r"`allow_session`"), "the allow_session decision"),
            must(read, "tui/Kl.Tui/SessionUi.cs", re.compile(r"allow_session"), "the allow_session decision"),
        ]
        if value is not None
    ]


def check_tool_metadata(read: ReadFile) -> list[str]:
    return [
        value
        for value in [
            must(
                read,
                "docs/architecture/overview.md",
                re.compile(r"`should_defer`,\s*`destructive` and concurrency hints are internal scheduling metadata"),
                "the internal tool metadata contract",
            ),
            must(read, "crates/kl-tools/src/lib.rs", re.compile(r"fn should_defer"), "the internal should_defer hook"),
        ]
        if value is not None
    ]


def check_benchmark_tasks(read: ReadFile) -> list[str]:
    text = read("benchmarks/tasks.json")
    if text is None:
        return ["benchmarks/tasks.json: file is missing"]
    try:
        tasks = json.loads(text)
    except json.JSONDecodeError as error:
        return [f"benchmarks/tasks.json: invalid JSON ({error})"]

    problems: list[str] = []
    if not isinstance(tasks, list):
        problems.append("benchmarks/tasks.json: expected a JSON array")
    elif len(tasks) != 23:
        problems.append(f"benchmarks/tasks.json: expected 23 tasks, found {len(tasks)}")
    note = must(read, "README.md", re.compile(r"23-task acceptance harness"), "the 23-task harness note")
    if note is not None:
        problems.append(note)
    return problems


def check_release_policy(read: ReadFile) -> list[str]:
    problems = [
        value
        for value in [
            must(read, "README.md", re.compile(r"Releases are built only from pushes to `main`"), "the main-only release policy"),
            must(read, "README.md", re.compile(r"`v<version>-rc1`"), "the v<version>-rcN tag scheme"),
            must(read, "README.md", re.compile(r"current `v<major>\.<minor>\.` series"), "the current-series retention rule"),
            must_not(read, "README.md", re.compile(r"retention list has\s+not been chosen"), "an undecided retention list"),
            must(read, ".github/workflows/release.yml", re.compile(r"branches: \[main\]"), "a main-only push trigger"),
            must(read, ".github/workflows/release.yml", re.compile(r"-rc\$\{rc\}"), "the -rcN tag suffix"),
            must_not(read, ".github/workflows/release.yml", re.compile(r"pull_request"), "pull request triggers"),
            must_not(read, ".github/workflows/release.yml", re.compile(r"workflow_dispatch"), "manual dispatch triggers"),
        ]
        if value is not None
    ]
    workflow = read(".github/workflows/release.yml")
    if workflow is not None:
        count = len(re.findall(r"branches:", workflow))
        if count != 1:
            problems.append(f".github/workflows/release.yml: expected 1 branch trigger, found {count}")
    return problems


def check_no_sha_files(read: ReadFile) -> list[str]:
    return [
        value
        for value in [
            must(read, "README.md", re.compile(r"without `\.sha256` files"), "the archive-only release note"),
            must(read, "scripts/package.py", re.compile(r"checksum = archive\.with_name\(archive\.name \+ \"\.sha256\"\)[\s\S]*?checksum\.unlink\(missing_ok=True\)"), "the .sha256 cleanup"),
            must_not(read, ".github/workflows/release.yml", re.compile(r"sha256"), "checksum artifacts"),
        ]
        if value is not None
    ]


def check_release_matrix(read: ReadFile) -> list[str]:
    workflow = read(".github/workflows/release.yml")
    readme = read("README.md")
    packer = read("scripts/package_format.py")
    if workflow is None or readme is None or packer is None:
        return ["release matrix: file is missing"]

    rid_pattern = re.compile(r"\b(?:win|linux|osx)-[a-z0-9]+\b")
    workflow_rids = re.findall(r"^\s+(?:- )?rid: (\S+)$", workflow, re.MULTILINE)
    readme_block = re.search(r"current release matrix is:[\s\S]*?```text([\s\S]*?)```", readme)
    readme_rids = [] if readme_block is None else rid_pattern.findall(readme_block.group(1))
    packer_rids = re.findall(r'^\s+"([a-z0-9-]+)":\s*\{\s*"kind":', packer, re.MULTILINE)
    problems: list[str] = []
    labels_and_rids = [
        (".github/workflows/release.yml", workflow_rids),
        ("README.md", readme_rids),
        ("scripts/package_format.py", packer_rids),
    ]
    for label, rids in labels_and_rids:
        if not rids:
            problems.append(f"{label}: expected a release RID list")
    expected = sorted(set(workflow_rids))
    if expected:
        for label, rids in labels_and_rids[1:]:
            actual = sorted(set(rids))
            if actual != expected:
                problems.append(f"{label}: expected RIDs [{', '.join(expected)}], found [{', '.join(actual)}]")
    return problems


def check_python_tooling_only(read: ReadFile) -> list[str]:
    return [
        value
        for value in [
            must(read, "README.md", re.compile(r"Python 3\.11\+ for development tooling"), "the Python tooling prerequisite"),
            must(read, "README.md", re.compile(r"product binaries and runtime do not depend on Python"), "the Python runtime boundary"),
            must_not(read, "README.md", re.compile(r"Node\.js [0-9]+\+ for repository scripts"), "Node.js build tooling"),
            must(read, "docs/adr/0001-rust-core-csharp-cli.md", re.compile(r"Python is allowed for development tooling"), "the Python tooling decision"),
        ]
        if value is not None
    ]


CHECKS = [
    {"id": "python-tooling-only", "description": "Python is development tooling, not a product dependency", "failures": check_python_tooling_only},
    {"id": "pty", "description": "Shell PTY is implemented through portable-pty", "failures": check_pty},
    {"id": "sandbox-backends", "description": "Linux bubblewrap and macOS Seatbelt backends exist", "failures": check_sandbox_backends},
    {"id": "mcp-sandbox", "description": "MCP server processes start through the sandbox", "failures": check_mcp_sandbox},
    {"id": "allow-session", "description": "Approvals support a session-scoped allow rule", "failures": check_allow_session},
    {"id": "tool-metadata-internal", "description": "Tool scheduling metadata stays internal and is not provider-visible", "failures": check_tool_metadata},
    {"id": "benchmark-tasks", "description": "The benchmark harness keeps 23 tasks", "failures": check_benchmark_tasks},
    {"id": "release-policy", "description": "Releases are built only from main and tagged v<version>-rcN", "failures": check_release_policy},
    {"id": "no-sha-files", "description": "Releases ship archives only, without .sha256 files", "failures": check_no_sha_files},
    {"id": "release-matrix", "description": "The release RID matrix is identical in the workflow, README and packer", "failures": check_release_matrix},
]


def check_docs(read: ReadFile) -> dict[str, object]:
    failures = []
    for check in CHECKS:
        details = check["failures"](read)
        if details:
            failures.append({"id": check["id"], "description": check["description"], "details": details})
    return {"total": len(CHECKS), "failures": failures}


def read_from_disk(file: str) -> str | None:
    full = ROOT / file
    return full.read_text(encoding="utf-8") if full.is_file() else None


def main() -> int:
    result = check_docs(read_from_disk)
    failures = result["failures"]
    for failure in failures:
        print(f"docs check failed: {failure['id']} ({failure['description']})", file=sys.stderr)
        for detail in failure["details"]:
            print(f"  - {detail}", file=sys.stderr)
    if failures:
        print(f"docs check: {len(failures)} of {result['total']} claims regressed", file=sys.stderr)
        return 1
    print(f"docs check: {result['total']} claims verified")
    return 0


if __name__ == "__main__":
    sys.exit(main())
