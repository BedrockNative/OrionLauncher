"""Offline, unprivileged package validation inside a disposable distro container."""
from contextlib import contextmanager
import ctypes
import json
import os
from pathlib import Path
import re
import shutil
import signal
import struct
import subprocess
import sys
import time

REPORT = Path("/tmp/orion-report")
WORK = Path("/tmp/orion compatibility")  # Deliberately exercise paths with spaces.
STAGES = []


def command(*args, timeout=60, env=None):
    return subprocess.run([str(a) for a in args], check=True, timeout=timeout, env=env,
                          stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, errors="replace").stdout


@contextmanager
def stage(name, fatal=False):
    start = time.monotonic()
    result = {"name": name, "status": "failed"}
    print(f"START {name}", flush=True)
    try:
        yield
        result["status"] = "passed"
    except Exception as error:
        result["error"] = str(error)
        if isinstance(error, subprocess.CalledProcessError):
            (REPORT / (name + "-error.log")).write_text(error.stdout or "")
        if fatal:
            raise
    finally:
        result["seconds"] = round(time.monotonic() - start, 2)
        STAGES.append(result)
        (REPORT / "stages.json").write_text(json.dumps(STAGES, indent=2) + "\n")
        print(f"{result['status'].upper()} {name} ({result['seconds']}s)", flush=True)


def stop(process):
    if process.poll() is None:
        os.killpg(process.pid, signal.SIGTERM)
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            os.killpg(process.pid, signal.SIGKILL)
            process.wait(timeout=10)


def rendered_capture(path):
    # XWDFile.h: 25 big-endian CARD32s, name, 12-byte color records, then pixels.
    # A named/mapped X11 window can exist before Avalonia paints its first frame.
    data = path.read_bytes()
    if len(data) < 100:
        return False
    header = struct.unpack_from(">25I", data)
    size, version, layout, _, width, height = header[:6]
    bpp, stride, colors = header[11], header[12], header[19]
    offset = size + colors * 12
    if version != 7 or layout != 2 or bpp not in (24, 32) or not width or not height:
        return False
    step = bpp // 8
    if size < 100 or stride < width * step or len(data) < offset + stride * height:
        return False
    mask = header[14] | header[15] | header[16]
    order = "little" if header[7] == 0 else "big"
    seen = set()
    for y in range(height):
        start = offset + y * stride
        for x in range(width):
            pixel = start + x * step
            seen.add(int.from_bytes(data[pixel:pixel + step], order) & mask)
            if len(seen) > 8:
                return True
    return False


def gui(executable, name, env):
    with (REPORT / (name + ".log")).open("w") as log:
        process = subprocess.Popen([str(executable)], env=env, stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
        try:
            deadline = time.monotonic() + 75
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError(f"{name} exited before showing a window ({process.returncode})")
                tree = command("xwininfo", "-root", "-tree")
                match = re.search(r'(0x[0-9a-fA-F]+) "Orion Launcher"', tree)
                if match and "Map State: IsViewable" in command("xwininfo", "-id", match[1]):
                    (REPORT / (name + "-windows.txt")).write_text(tree)
                    time.sleep(5)
                    if process.poll() is not None:
                        raise RuntimeError(f"{name} exited after opening its window")
                    capture = REPORT / (name + ".xwd")
                    command("xwd", "-id", match[1], "-silent", "-out", capture)
                    if rendered_capture(capture):
                        output = (REPORT / (name + ".log")).read_text(errors="replace")
                        if "Fontconfig warning:" in output or "Fontconfig error:" in output:
                            raise RuntimeError(f"{name} parsed incompatible Fontconfig configuration")
                        return
                time.sleep(1)
            raise RuntimeError(f"{name} never showed a rendered main window")
        finally:
            stop(process)


def main():
    REPORT.mkdir()
    WORK.mkdir()
    shutil.copy2("/etc/os-release", REPORT / "os-release")
    shutil.copy2("/opt/orion-tests/packages.txt", REPORT / "packages.txt")
    (REPORT / "host.txt").write_text(command("uname", "-a") + command("ldd", "--version") + f"uid={os.getuid()}\nnetwork=none\n")
    if os.getuid() == 0:
        raise RuntimeError("The compatibility test must not run as root")
    for name in ("cache", "config", "data", "runtime"):
        path = WORK / name
        path.mkdir(mode=0o700)
        os.environ[f"XDG_{name.upper()}_HOME" if name != "runtime" else "XDG_RUNTIME_DIR"] = str(path)
    os.environ.update(DISPLAY=":99", LIBGL_ALWAYS_SOFTWARE="1", GALLIUM_DRIVER="llvmpipe")
    with (REPORT / "xvfb.log").open("w") as log:
        display = subprocess.Popen(["Xvfb", ":99", "-screen", "0", "1280x900x24", "-nolisten", "tcp", "-ac"],
                                   stdout=log, stderr=subprocess.STDOUT, start_new_session=True)
    try:
        for attempt in range(30):
            if Path("/tmp/.X11-unix/X99").exists():
                break
            if display.poll() is not None:
                raise RuntimeError("Xvfb failed")
            time.sleep(0.2)
        run_tests()
    finally:
        stop(display)


def run_tests():
    packages = Path("/packages")
    with stage("checksums", fatal=True):
        result = subprocess.run(["sha256sum", "--check", "SHA256SUMS"], cwd=packages, capture_output=True, text=True, check=True)
        (REPORT / "checksums.log").write_text(result.stdout)
    with stage("tar-extraction", fatal=True):
        command("tar", "-xzf", next(packages.glob("*.tar.gz")), "-C", WORK, timeout=180)
    bundle = next(WORK.glob("OrionLauncher-*"))
    app = bundle / "usr/lib/orion"
    os.environ.update(FONTCONFIG_FILE=str(bundle / "etc/fonts/fonts.conf"), FONTCONFIG_PATH=str(bundle / "etc/fonts"))
    with stage("host-software-egl"):
        (REPORT / "host-software-egl.log").write_text(command(sys.executable, __file__, "--egl-probe"))
    with stage("bundled-software-egl"):
        (REPORT / "bundled-software-egl.log").write_text(command(sys.executable, __file__, "--egl-probe",
            env={**os.environ, "LD_LIBRARY_PATH": str(app / "native"), "EGL_LOG_LEVEL": "debug", "LIBGL_DEBUG": "verbose"}))
    with stage("native-dependencies"):
        failures = []
        count = 0
        with (REPORT / "elf.log").open("w") as log:
            for binary in bundle.rglob("*"):
                if binary.is_symlink() or not binary.is_file():
                    continue
                with binary.open("rb") as stream:
                    if stream.read(4) != b"\x7fELF":
                        continue
                result = subprocess.run(["ldd", str(binary)], capture_output=True, text=True)
                text = result.stdout + result.stderr
                log.write(f"\n{binary.relative_to(bundle)}\n{text}")
                count += 1
                if "not found" in text or "version `" in text or (result.returncode and "statically linked" not in text and "not a dynamic executable" not in text):
                    failures.append(str(binary.relative_to(bundle)))
        (REPORT / "elf-count.txt").write_text(str(count) + "\n")
        if failures:
            raise RuntimeError("Unresolved ELF dependencies: " + ", ".join(failures))
    with stage("launcher-cli"):
        (REPORT / "launcher-cli.log").write_text(command(bundle / "AppRun", "--help"))
    with stage("xodus-cli"):
        xodus = next((app / "runtimes/xodus").rglob("xodus-cli"))
        (REPORT / "xodus-cli.log").write_text(command(xodus, "--version") + command(xodus, "accounts", "--help"))
    with stage("wine-prefix"):
        wine = next((app / "runtimes/winegdk").glob("*/bin/wine"))
        server = wine.parent / "wineserver"
        prefix = WORK / "wineprefix"
        env = {**os.environ, "WINEPREFIX": str(prefix), "WINESERVER": str(server), "WINEDEBUG": "-all,err+all", "WINEBOOT_HIDE_DIALOG": "1"}
        try:
            (REPORT / "wine-version.log").write_text(command(wine, "--version", env=env))
            with (REPORT / "wineboot.log").open("w") as log:
                subprocess.run([str(wine.parent / "wineboot"), "-u"], env=env, timeout=180, check=True, stdout=log, stderr=subprocess.STDOUT)
            command(server, "-w", env=env, timeout=90)
            if not (prefix / "system.reg").is_file() or not (prefix / "drive_c/windows/system32/d3d12.dll").is_file():
                raise RuntimeError("Wine did not prepare the expected prefix")
        finally:
            subprocess.run([str(server), "-k"], env=env, timeout=20, capture_output=True)
    with stage("webkit-offline"):
        (REPORT / "webkit.log").write_text(command(bundle / "AppRun", env={**os.environ,
            "ORION_PACKAGING_VERIFY_WEBKIT": "1", "EGL_LOG_LEVEL": "debug", "LIBGL_DEBUG": "verbose"}, timeout=45))
    with stage("tar-gui"):
        gui(bundle / "AppRun", "tar-gui", os.environ.copy())
    # Isolate the second launch from the first one's process/lock/config files.
    for name in ("cache", "config", "data", "runtime"):
        path = WORK / ("appimage-" + name)
        path.mkdir(mode=0o700)
        os.environ[f"XDG_{name.upper()}_HOME" if name != "runtime" else "XDG_RUNTIME_DIR"] = str(path)
    image = next(packages.glob("*.AppImage"))
    if not os.access(image, os.X_OK):
        target = WORK / image.name
        shutil.copy2(image, target)
        target.chmod(0o700)
        image = target
    env = {**os.environ, "APPIMAGE_EXTRACT_AND_RUN": "1"}
    with stage("appimage-cli"):
        (REPORT / "appimage-cli.log").write_text(command(image, "--help", env=env, timeout=120))
    with stage("appimage-gui"):
        gui(image, "appimage-gui", env)
    if any(item["status"] != "passed" for item in STAGES):
        raise RuntimeError("One or more compatibility checks failed; see stages.json and individual logs")


def egl_probe():
    # No extra graphics toolkit package that could mask a missing dependency.
    egl = ctypes.CDLL("libEGL.so.1")
    egl.eglGetDisplay.argtypes = [ctypes.c_void_p]
    egl.eglGetDisplay.restype = ctypes.c_void_p
    egl.eglInitialize.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int)]
    egl.eglInitialize.restype = ctypes.c_uint
    egl.eglTerminate.argtypes = [ctypes.c_void_p]
    display = egl.eglGetDisplay(None)
    major, minor = ctypes.c_int(), ctypes.c_int()
    if not egl.eglInitialize(display, ctypes.byref(major), ctypes.byref(minor)):
        raise RuntimeError(f"EGL initialization failed: {egl.eglGetError():#x}")
    print(f"Software EGL initialized: {major.value}.{minor.value}")
    egl.eglTerminate(display)


if __name__ == "__main__" and sys.argv[1:] == ["--egl-probe"]:
    egl_probe()
elif __name__ == "__main__":
    try:
        main()
    except Exception as error:
        REPORT.mkdir(exist_ok=True)
        (REPORT / "failure.txt").write_text(str(error) + "\n")
        print(f"FAILED: {error}", file=sys.stderr, flush=True)
        sys.exit(1)
