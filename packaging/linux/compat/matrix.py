"""Developer-only distro matrix. Never called by GitHub Actions.

Builds minimal desktop test bases, runs unprivileged/offline, saves evidence, then
removes only the containers it created. Test images remain cached for reuse.
"""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import time
import uuid

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
TARGETS = {
    "arch": "archlinux:base",
    "debian": "debian:13-slim",
    "fedora": "registry.fedoraproject.org/fedora:44",
    "ubuntu": "ubuntu:24.04",
}


def harness_hash():
    digest = hashlib.sha256()
    for name in ("Dockerfile", "prepare.sh", "probe.py"):
        digest.update(name.encode() + b"\0" + (HERE / name).read_bytes())
    return digest.hexdigest()


def run(command, log, timeout):
    with log.open("wb") as stream:
        return subprocess.run(command, stdout=stream, stderr=subprocess.STDOUT, timeout=timeout).returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("release", type=Path, help="Folder containing both final packages and SHA256SUMS")
    parser.add_argument("--distros", nargs="+", choices=TARGETS, default=["arch", "debian", "fedora"])
    parser.add_argument("--output", type=Path, help="New report directory (never overwritten)")
    parser.add_argument("--reuse-images", action="store_true", help="Reuse previously prepared bases instead of updating them")
    parser.add_argument("--cpus", type=int, default=4)
    parser.add_argument("--memory", default="6g")
    parser.add_argument("--timeout", type=int, default=900, help="Seconds per distro test, excluding image preparation")
    args = parser.parse_args()
    if platform.machine() not in ("x86_64", "AMD64"):
        parser.error("This release matrix requires an x86_64 host; emulation is not a compatibility test.")
    release = args.release.resolve(strict=True)
    if not (release / "SHA256SUMS").is_file() or len(list(release.glob("*.AppImage"))) != 1 or len(list(release.glob("*.tar.gz"))) != 1:
        parser.error("Expected exactly one AppImage, one tar.gz and SHA256SUMS")
    # Commas in Docker --mount paths are not representable safely.
    if "," in str(release):
        parser.error("The release directory must not contain commas")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    output = (args.output or ROOT / "artifacts" / ("compatibility-" + stamp)).resolve()
    output.mkdir(parents=True, exist_ok=False)
    shutil.copy2(release / "SHA256SUMS", output / "tested-SHA256SUMS")
    results = []
    for distro in dict.fromkeys(args.distros):
        folder = output / distro
        folder.mkdir()
        image = f"orion-compat-local:{distro}"
        container = "orion-compat-" + uuid.uuid4().hex
        result = {"distro": distro, "base": TARGETS[distro], "status": "failed"}
        started = time.monotonic()
        print(f"[{distro}] Preparing test base; logs: {folder}", flush=True)
        try:
            if not args.reuse_images:
                code = run(["docker", "build", "--pull", "--no-cache", "--build-arg", f"BASE={TARGETS[distro]}",
                            "--build-arg", f"FAMILY={distro}", "--build-arg", f"HARNESS_SHA256={harness_hash()}",
                            "-t", image, str(HERE)], folder / "build.log", 1800)
                if code:
                    raise RuntimeError(f"Image preparation failed ({code}); see build.log")
            metadata = subprocess.check_output(["docker", "image", "inspect", image], text=True)
            (folder / "image.json").write_text(metadata)
            if json.loads(metadata)[0]["Config"].get("Labels", {}).get("org.orion.compat.harness") != harness_hash():
                raise RuntimeError("Cached test harness is stale; rerun without --reuse-images")
            print(f"[{distro}] Running offline without host GPU, home or desktop mounts", flush=True)
            code = run(["docker", "run", "--name", container, "--network=none", "--cap-drop=ALL",
                        "--security-opt=no-new-privileges", "--pids-limit=512", f"--cpus={args.cpus}",
                        f"--memory={args.memory}", "--shm-size=256m", "--mount",
                        f"type=bind,src={release},dst=/packages,readonly", image], folder / "container.log", args.timeout)
            result["exitCode"] = code
            copied = subprocess.run(["docker", "cp", f"{container}:/tmp/orion-report/.", str(folder)], capture_output=True, text=True)
            if copied.returncode:
                raise RuntimeError("Could not retrieve evidence: " + copied.stderr)
            if code == 0:
                result["status"] = "passed"
        except (RuntimeError, subprocess.SubprocessError, OSError) as error:
            result["error"] = str(error)
        finally:
            # Also retain partial evidence after timeout/failure; never mount a
            # writable host directory inside the tested application container.
            subprocess.run(["docker", "cp", f"{container}:/tmp/orion-report/.", str(folder)], capture_output=True)
            # Exact random name owned by this invocation, never prune the daemon.
            subprocess.run(["docker", "rm", "-f", container], capture_output=True)
            result["seconds"] = round(time.monotonic() - started, 2)
            results.append(result)
            (output / "summary.json").write_text(json.dumps(results, indent=2) + "\n")
            print(f"[{distro}] {result['status'].upper()} ({result['seconds']}s)", flush=True)
    print(f"Report: {output}", flush=True)
    return 0 if all(r["status"] == "passed" for r in results) else 1


if __name__ == "__main__":
    sys.exit(main())
