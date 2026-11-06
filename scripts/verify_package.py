from __future__ import annotations

import argparse
from pathlib import Path
import subprocess
import sys

from package_format import assert_binary_format, is_diagnostic_file, layout_for


ROOT = Path(__file__).resolve().parent.parent


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Verify a packaged KL release bundle.")
    parser.add_argument("--rid", required=True)
    parser.add_argument("--gui", action="store_true", help="require the bundled Avalonia GUI")
    parser.add_argument("--output")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    layout = layout_for(args.rid)
    output = Path(args.output).resolve() if args.output else (ROOT / "dist" / args.rid).resolve()
    archive = output.parent / f"kl-{args.rid}{layout['ext']}"
    problems: list[str] = []

    if not archive.is_file():
        problems.append(f"missing archive: {archive}")

    expected = [
        (output / str(layout["exe"]), "cli executable"),
        (output / str(layout["core"]), "core executable"),
    ]
    if args.gui:
        expected.append((output / "gui" / str(layout["exe"]), "gui executable"))
        expected.append((output / "gui" / str(layout["core"]), "gui core executable"))

    for file, label in expected:
        if not file.is_file():
            problems.append(f"missing {label}: {file}")
            continue
        try:
            assert_binary_format(file, args.rid)
        except ValueError as error:
            problems.append(str(error))

    if archive.is_file():
        listing = subprocess.run(
            ["tar", "-tf", str(archive)],
            text=True,
            capture_output=True,
        )
        if listing.returncode != 0:
            problems.append(f"cannot list archive: {listing.stderr.strip() or listing.returncode}")
        else:
            for raw_entry in listing.stdout.splitlines():
                entry = raw_entry.strip()
                if not entry:
                    continue
                if entry == "." or entry.startswith("./"):
                    problems.append(f"archive entry must not use a './' prefix: {entry}")
                name = entry.rsplit("/", 1)[-1]
                if is_diagnostic_file(name):
                    problems.append(f"archive contains a diagnostic file: {entry}")

    if problems:
        for problem in problems:
            print(f"error: {problem}", file=sys.stderr)
        return 1

    print(f"verified {args.rid}: {archive}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
