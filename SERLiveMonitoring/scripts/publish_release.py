#!/usr/bin/env python3
"""Publish a self-contained release for Linux or Windows x64."""

import argparse
import shutil
import subprocess
import tempfile
from pathlib import Path


TARGETS = {
    "lx64": "linux-x64",
    "wx64": "win-x64",
}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "target",
        nargs="*",
        default=['wx64', 'lx64'],
        help="one or more release targets: lx64 for Linux or wx64 for Windows (default: wx64 lx64)",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="show the publish command without running it",
    )
    args = parser.parse_args()

    solution_directory = Path(__file__).resolve().parent.parent
    for target in args.target:
        runtime = TARGETS[target]
        output_directory = solution_directory / "publish" / runtime
        if not args.dry_run:
            shutil.rmtree(output_directory, ignore_errors=True)

        command = [
            "dotnet",
            "publish",
            "./SERLiveMonitoring.csproj",
            "-c",
            "Release",
            "-r",
            runtime,
            "--self-contained",
            "true",
            "-o",
            f"./publish/{runtime}",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:DebugType=None",
        ]

        print(" ".join(command))
        if not args.dry_run:
            with tempfile.TemporaryDirectory(prefix=f"SERLiveMonitoring-{runtime}-") as temporary_root:
                build_directory = Path(temporary_root) / "source"
                shutil.copytree(
                    solution_directory,
                    build_directory,
                    ignore=shutil.ignore_patterns(
                        ".git", ".vscode", "bin", "obj", "publish", "datastore*"
                    ),
                )
                command_for_build = command.copy()
                command_for_build[10] = str(output_directory)
                subprocess.run(command_for_build, cwd=build_directory, check=True)


if __name__ == "__main__":
    main()