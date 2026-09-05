#!/usr/bin/env python3
"""Bump the application version in SERLiveMonitoring.csproj."""

import argparse
import re
from pathlib import Path


VERSION_PATTERN = re.compile(r"^(\d+)\.(\d+)\.(\d+)$")
PROJECT_VERSION_PATTERN = re.compile(r"(<Version>)([^<]+)(</Version>)")


def parse_version(value: str) -> tuple[int, int, int]:
    match = VERSION_PATTERN.fullmatch(value)
    if match is None:
        raise ValueError(f"version must be in major.minor.patch form: {value!r}")
    return tuple(int(part) for part in match.groups())


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "version",
        nargs="?",
        help="next version; defaults to incrementing the current patch number",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        default=True,
        help="show the change without writing the project file, use firce to write",
    )
    parser.add_argument(
        "--prod",
        action="store_true",
        default=False,
        help="show the change and force writing the project file",
    )
    args = parser.parse_args()

    project_file = Path(__file__).resolve().parent.parent / "SERLiveMonitoring.csproj"
    project_text = project_file.read_text(encoding="utf-8")
    matches = list(PROJECT_VERSION_PATTERN.finditer(project_text))
    if len(matches) != 1:
        raise RuntimeError(f"expected exactly one <Version> element in {project_file}")

    current_version = matches[0].group(2)
    current_parts = parse_version(current_version)
    next_version = args.version or f"{current_parts[0]}.{current_parts[1]}.{current_parts[2] + 1}"
    parse_version(next_version)

    updated_text = PROJECT_VERSION_PATTERN.sub(
        lambda match: f"{match.group(1)}{next_version}{match.group(3)}",
        project_text,
        count=1,
    )

    if args.dry_run and not args.prod:
        print(f"{project_file}: {current_version} -> {next_version}  (use --prod to write)")
        return

    project_file.write_text(updated_text, encoding="utf-8")
    print(f"Updated {project_file}: {current_version} -> {next_version}")


if __name__ == "__main__":
    main()