"""Ubuntu 24.04 x86_64 portable stack. Verified upstream inputs, recursive ELF closure.

glibc and GPU driver interfaces deliberately come from the host. Everything else
is copied into a private AppDir shared by tar.gz and AppImage; no system installs.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from release import ROOT, metadata

HERE = Path(__file__).resolve().parent
# Mesa/GLVND drivers dlopen against the host's Wayland ABI. Bundling Ubuntu's
# older libwayland shadows newer host symbols (e.g. wl_fixes_interface) and
# breaks EGL/WebKit on rolling distributions. Keep this driver boundary intact.
HOST = re.compile(r"^(?:ld-linux.*|lib(?:c|m|mvec|pthread|dl|rt|resolv|util|nss_.*)\.so.*|lib(?:GL|EGL|GLESv2|GLX|GLdispatch|OpenGL|vulkan|gbm|drm.*|wayland-(?:client|server|cursor|egl))\.so.*)$")


def run(*args, **kwargs):
    return subprocess.run([str(a) for a in args], check=True, **kwargs)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def download(item, cache):
    target = cache / item["sha256"]
    if not target.exists():
        run("curl", "--fail", "--location", "--retry", "3", "--proto", "=https", "--proto-redir", "=https", "--output", target, item["url"])
    if digest(target) != item["sha256"]:
        raise ValueError(f"Checksum mismatch for {item['url']}; inspect upstream and update stack.lock.json intentionally.")
    return target


def elf(path):
    if path.is_symlink() or not path.is_file():
        return False
    with path.open("rb") as stream:
        return stream.read(4) == b"\x7fELF"


def copy_tree(source, destination):
    if not source.exists():
        raise FileNotFoundError(source)
    if source.is_dir():
        shutil.copytree(source, destination, dirs_exist_ok=True, symlinks=False)
    else:
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)


def bundle_wine_addons(app, lock, cache):
    # Match Wine's appwiz.cpl versions/hashes. Having the MSIs in WINEDATADIR
    # avoids first-prefix download prompts, including on an offline desktop.
    wine = list((app / "runtimes/winegdk").glob("*/bin/wine"))
    if len(wine) != 1:
        raise ValueError("Expected one Wine installation for bundled addons")
    data = wine[0].parent.parent / "share/wine"
    installers = [file.read_bytes() for file in wine[0].parent.parent.rglob("appwiz.cpl")]
    for name in ("wine-mono", "wine-gecko-x86", "wine-gecko-x64"):
        item = lock[name]
        if not any(item["fileName"].encode("utf-16le") in binary and item["sha256"].encode("ascii") in binary
                   for binary in installers):
            raise ValueError(f"{name} does not match Wine's embedded installer contract; update stack.lock.json")
        copy_tree(download(item, cache), data / item["subdir"] / item["fileName"])


def build_ffmpeg(item, cache, work, app):
    # WineGDK needs ABI 63/61; Ubuntu's FFmpeg 6 ABI cannot be substituted with
    # symlinks. Build the actual upstream ABI against our oldest supported glibc.
    source = work / "ffmpeg-source"
    source.mkdir()
    with tarfile.open(download(item, cache)) as archive:
        archive.extractall(source, filter="data")
    source = next(source.iterdir())
    prefix = app / "native"
    run(source / "configure", f"--prefix={prefix}", "--enable-shared", "--disable-static",
        "--disable-programs", "--disable-doc", "--disable-debug", "--disable-autodetect",
        "--enable-zlib", cwd=source, stdout=subprocess.DEVNULL)
    run("make", f"-j{min(os.cpu_count() or 2, 8)}", cwd=source, stdout=subprocess.DEVNULL)
    run("make", "install-libs", cwd=source, stdout=subprocess.DEVNULL)
    # Include the exact corresponding source and build recipe for LGPL compliance.
    copy_tree(cache / item["sha256"], app.parents[2] / "usr/share/licenses/ffmpeg/source.tar.xz")
    copy_tree(HERE / "package.py", app.parents[2] / "usr/share/licenses/ffmpeg/build-recipe.py")
    for library in (prefix / "lib").glob("*.so*"):
        if library.is_symlink():
            (prefix / library.name).symlink_to(os.readlink(library))
        else:
            copy_tree(library, prefix / library.name)
    shutil.rmtree(prefix / "lib")


def bundle_native(appdir, app):
    native = app / "native"
    native.mkdir(exist_ok=True)
    # dlopen/PInvoke dependencies cannot be discovered from DT_NEEDED alone.
    roots = ["libX11.so.6", "libICE.so.6", "libSM.so.6", "libXi.so.6", "libXrandr.so.2",
             "libXcursor.so.1", "libfontconfig.so.1", "libicuuc.so.74", "libicui18n.so.74",
             "libssl.so.3", "libcrypto.so.3", "libgssapi_krb5.so.2", "libasound.so.2",
             "libpulse.so.0", "libudev.so.1", "libusb-1.0.so.0", "libgnutls.so.30",
             "libgcrypt.so.20", "libunwind.so.8", "libv4l2.so.0", "libSDL2-2.0.so.0",
             "libpcap.so.0.8", "libpcsclite.so.1", "libxkbregistry.so.0",
             "libOSMesa.so.8", "libsecret-1.so.0"]
    cache = run("ldconfig", "-p", capture_output=True, text=True).stdout
    paths = {}
    for line in cache.splitlines():
        match = re.search(r"^\s*(\S+) .*x86-64.* => (\S+)$", line)
        if match:
            paths.setdefault(match[1], Path(match[2]))
    pending = [paths[name] for name in roots]  # Missing required libraries fail the build.
    pending += [p for p in appdir.rglob("*") if elf(p)]
    seen = set()
    owned_packages = set()
    while pending:
        source = pending.pop()
        source = source.resolve()
        if source in seen:
            continue
        seen.add(source)
        if not source.is_relative_to(appdir):
            target = native / source.name
            copy_tree(source, target)
            # Preserve all loader names, including SONAME vs on-disk filename.
            soname = run("patchelf", "--print-soname", source, capture_output=True, text=True).stdout.strip()
            if soname and soname != target.name and not (native / soname).exists():
                (native / soname).symlink_to(target.name)
            owner = subprocess.run(["dpkg-query", "-S", str(source)], capture_output=True, text=True)
            if owner.returncode == 0:
                owned_packages.update(line.split(": /", 1)[0] for line in owner.stdout.splitlines())
        result = subprocess.run(["ldd", str(source)], capture_output=True, text=True,
                                env={**os.environ, "LD_LIBRARY_PATH": f"{source.parent}:{native}"})
        output = result.stdout + result.stderr
        if "not found" in output or "version `" in output:
            raise RuntimeError(f"Unresolved dependencies in {source}:\n{output}")
        if result.returncode and "statically linked" not in output and "not a dynamic executable" not in output:
            raise RuntimeError(output)
        for line in output.splitlines():
            match = re.search(r"^\s*(\S+) => (/\S+) ", line)
            if match and not HOST.fullmatch(match[1]):
                pending.append(Path(match[2]))
    # Wine runtime updates use the upstream libpcap name rather than Debian's
    # historical SONAME for this same 1.x API.
    (native / "libpcap.so.1").symlink_to("libpcap.so.0.8")
    # Private relative RPATH works in both formats and doesn't affect external apps.
    for path in appdir.rglob("*"):
        if not elf(path):
            continue
        dynamic = run("readelf", "-d", path, capture_output=True, text=True).stdout
        if "(NEEDED)" not in dynamic:
            continue
        old = run("patchelf", "--print-rpath", path, capture_output=True, text=True).stdout.strip()
        relative = "$ORIGIN/" + os.path.relpath(native, path.parent)
        # RUNPATH deliberately permits the private relocated WebKit library to
        # take precedence through the owned child's LD_LIBRARY_PATH.
        run("patchelf", "--set-rpath", ":".join(filter(None, ["$ORIGIN", relative, old])), path)
    return run("dpkg-query", "-W", "-f=${Package}=${Version}\n", *sorted(owned_packages),
               capture_output=True, text=True).stdout.splitlines()


def build(published, output):
    identity = metadata()
    version = identity["version"]
    lock = json.loads((HERE / "stack.lock.json").read_text())
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise ValueError("Output directory must be empty; never overwrite published assets.")
    cache = ROOT / "artifacts/package-downloads"
    cache.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="orion-package-") as temporary:
        work = Path(temporary)
        appdir = work / f"OrionLauncher-{version}-linux-x64"
        app = appdir / "usr/lib/orion"
        copy_tree(published, app)
        # Optional legacy LTTng 2.12 profiling provider (ABI 0), unused by the
        # launcher. CoreCLR, EventPipe, dumps and launcher/game logs are retained.
        (app / "libcoreclrtraceptprovider.so").unlink(missing_ok=True)
        for required in ("OrionLauncher", "Orion.Native.dll", "libcoreclr.so", "libSkiaSharp.so", "libHarfBuzzSharp.so"):
            if not (app / required).is_file():
                raise FileNotFoundError(f"Missing published stack component: {required}")
        for name in ("xodus", "winegdk"):
            runtime = app / "runtimes" / name
            runtime.mkdir(parents=True)
            with tarfile.open(download(lock[name], cache)) as archive:
                archive.extractall(runtime, filter="data")
            (runtime / "version.json").write_text(json.dumps(lock[name]["tag"]))
        bundle_wine_addons(app, lock, cache)
        build_ffmpeg(lock["ffmpeg"], cache, work, app)
        # Debian keeps the historic libpcap SONAME .0.8 for the same upstream
        # libpcap 1.x ABI. Adapt only Wine's packet capture module to that name.
        for module in (app / "runtimes/winegdk").rglob("wpcap.so"):
            run("patchelf", "--replace-needed", "libpcap.so.1", "libpcap.so.0.8", module)
        # Native web login needs helper executables, injected bundles and module data,
        # not just libwebkit. Keep their complete relative Ubuntu layout.
        resources = ["usr/lib/x86_64-linux-gnu/webkit2gtk-4.1",
                     "usr/lib/x86_64-linux-gnu/gio/modules",
                     "usr/lib/x86_64-linux-gnu/gstreamer-1.0",
                     "usr/lib/x86_64-linux-gnu/gstreamer1.0",
                     "usr/lib/x86_64-linux-gnu/gdk-pixbuf-2.0",
                     "usr/lib/x86_64-linux-gnu/gtk-3.0",
                     "usr/lib/x86_64-linux-gnu/alsa-lib",
                     "usr/share/glib-2.0/schemas", "usr/share/mime", "usr/share/alsa",
                     "usr/share/fonts/truetype/dejavu", "usr/share/fontconfig", "etc/fonts",
                     "usr/share/icons/Adwaita", "usr/share/icons/hicolor",
                     "etc/ssl/certs/ca-certificates.crt", "usr/bin/xdg-open", "usr/bin/xdg-mime",
                     "usr/bin/bwrap", "usr/bin/xdg-dbus-proxy"]
        for resource in resources:
            copy_tree(Path("/") / resource, appdir / resource)
        run("cc", "-O2", "-Wall", "-Wextra", "-Werror", HERE / "relocate-webkit.c",
            "-o", appdir / "usr/bin/orion-relocate-webkit")
        run("cc", "-O2", "-Wall", "-Wextra", "-Werror", HERE / "webkit-smoke.c",
            "-ldl", "-o", appdir / "usr/bin/orion-webkit-smoke")
        (appdir / "etc/fonts/fonts.conf").write_text('''<?xml version="1.0"?>
<!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd">
<fontconfig><dir prefix="relative">../../usr/share/fonts</dir>
<dir>/usr/share/fonts</dir><dir>/usr/local/share/fonts</dir><dir prefix="xdg">fonts</dir>
<cachedir prefix="xdg">fontconfig</cachedir><include ignore_missing="yes">conf.d</include></fontconfig>
''')
        packages = bundle_native(appdir, app)
        # Attribution for all system packages in the build environment, including
        # helper/resource packages and transitive libraries. Never copy user data.
        for copyright in Path("/usr/share/doc").glob("*/copyright"):
            copy_tree(copyright, appdir / "usr/share/licenses/system" / copyright.parent.name / "copyright")
        copy_tree(ROOT / "LICENSE", appdir / "LICENSE")
        copy_tree(HERE / "AppRun", appdir / "AppRun")
        (appdir / "AppRun").chmod(0o755)
        (appdir / "OrionLauncher").symlink_to("AppRun")
        copy_tree(HERE / "io.bedrocknative.orion.desktop", appdir / "io.bedrocknative.orion.desktop")
        copy_tree(ROOT / "src/Orion.Desktop/Assets/orion.png", appdir / "io.bedrocknative.orion.png")
        (appdir / ".DirIcon").symlink_to("io.bedrocknative.orion.png")
        manifest = {**identity, "notes": f"docs/en_US/changelog/release/v{version}.md",
                    "commit": os.environ.get("GITHUB_SHA", "local"), "architecture": "x86_64",
                    "baseline": "Ubuntu 24.04 / glibc 2.39", "stack": lock,
                    "nativePackages": packages}
        manifest["excludedOptionalComponents"] = ["Legacy LTTng 2.12 tracepoint provider"]
        (output / "build-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        copy_tree(output / "build-manifest.json", appdir / "build-manifest.json")
        copy_tree(Path(identity["notes"]), output / "release-notes.md")
        copy_tree(ROOT / "RELEASING.md", appdir / "RELEASING.md")
        run("desktop-file-validate", appdir / "io.bedrocknative.orion.desktop")
        run(appdir / "AppRun", "--help")
        # Normalize tar ownership; packages contain the same AppDir payload.
        run("tar", "--owner=0", "--group=0", "--numeric-owner", "-czf",
            output / f"OrionLauncher-{version}-linux-x64.tar.gz", "-C", work, appdir.name)
        tool = download(lock["appimagetool"], cache)
        tool.chmod(0o755)
        run(tool, "--appimage-extract", cwd=work, stdout=subprocess.DEVNULL)
        runtime = download(lock["appimage-runtime"], cache)
        run(work / "squashfs-root/AppRun", "--runtime-file", runtime, "--comp", "zstd", "--no-appstream", "--mksquashfs-opt=-no-xattrs",
            appdir, output / f"OrionLauncher-{version}-linux-x64.AppImage", env={**os.environ, "ARCH": "x86_64", "VERSION": version})
        files = sorted(p for p in output.iterdir() if p.is_file() and p.name != "release-notes.md")
        (output / "SHA256SUMS").write_text("".join(f"{digest(p)}  {p.name}\n" for p in files))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("published", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    build(args.published.resolve(), args.output.resolve())
