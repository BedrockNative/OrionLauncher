"""Validated release identity shared by Actions and local packaging. No shell evaluation."""
import argparse
import os
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parent.parent
SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-((?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*))?")


def metadata(root=ROOT, requested=""):
    version = (root / "RELEASE_VERSION").read_text().strip()
    match = SEMVER.fullmatch(version)
    if not match or any(int(match[i]) > 65534 for i in (1, 2, 3)):
        raise ValueError("RELEASE_VERSION must be X.Y.Z[-prerelease], without v, build metadata or leading zeros.")
    if requested and requested != version:
        raise ValueError("Requested version must match RELEASE_VERSION in the selected main commit.")
    notes = root / f"docs/en_US/changelog/release/v{version}.md"
    if notes.is_symlink() or not notes.is_file() or not notes.read_text().strip():
        raise ValueError(f"Missing or empty release changelog: {notes}")
    return {"version": version, "tag": f"v{version}", "prerelease": str(bool(match[4])).lower(), "notes": str(notes)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--github-output", type=Path)
    args = parser.parse_args()
    result = metadata(requested=os.environ.get("REQUESTED_VERSION", ""))
    if args.github_output:
        with args.github_output.open("a") as output:
            for key in ("version", "tag", "prerelease"):
                output.write(f"{key}={result[key]}\n")
    print(f"Validated {result['tag']} using {result['notes']}")
