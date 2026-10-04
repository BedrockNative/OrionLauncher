"""Ubuntu 24.04 x86_64 portable stack. Verified inputs, private ELF dependencies.

Core system/desktop libraries come from the host. Application runtimes and their
remaining dependencies live in a private AppDir shared by tar.gz and AppImage.
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
HOST = re.compile(r"^(?:ld-linux.*|lib(?:c|m|mvec|pthread|dl|rt|resolv|util|anl|BrokenLocale|thread_db|nss_.*)\.so.*|lib(?:GL|EGL|GLESv1_CM|GLESv2|GLX|GLdispatch|OpenGL|vulkan|gbm|glapi|drm.*|wayland-(?:client|server|cursor|egl))\.so.*)$")
# Reviewed desktop baseline, not a list generated from whatever happens to be
# installed on the build machine. Keep version-sensitive GTK/GLib/WebKit, fonts,
# ICU, TLS, Kerberos and codecs private. Do not fetch a mutable exclude list at build
# time. See https://github.com/AppImageCommunity/pkg2appimage/blob/master/excludelist
HOST_SONAMES = frozenset({
    # Compiler runtimes and common base-system libraries.
    "libgcc_s.so.1", "libstdc++.so.6", "libz.so.1",
    "liblzma.so.5", "libzstd.so.1", "libexpat.so.1", "libuuid.so.1",
    "libgmp.so.10", "libgpg-error.so.0", "libffi.so.8", "libpcre2-8.so.0",
    "libmount.so.1", "libblkid.so.1", "libcap.so.2", "libattr.so.1", "libacl.so.1",
    # Session/device interfaces must follow the host services.
    "libdbus-1.so.3", "libudev.so.1", "libsystemd.so.0", "libusb-1.0.so.0",
    "libasound.so.2", "libpulse.so.0", "libpulse-simple.so.0",
    # X11/XWayland desktop.
    "libX11.so.6", "libX11-xcb.so.1", "libxcb.so.1", "libXau.so.6",
    "libXdmcp.so.6", "libICE.so.6", "libSM.so.6", "libXext.so.6",
    "libXrender.so.1", "libXfixes.so.3", "libXi.so.6", "libXrandr.so.2",
    "libXcursor.so.1", "libXinerama.so.1", "libXss.so.1", "libXxf86vm.so.1",
    "libxcb-dri2.so.0", "libxcb-dri3.so.0", "libxcb-glx.so.0",
    "libxcb-present.so.0", "libxcb-randr.so.0", "libxcb-render.so.0",
    "libxcb-shm.so.0", "libxcb-sync.so.1", "libxcb-xfixes.so.0",
})
# Fontconfig/FreeType/HarfBuzz stay together: rolling host HarfBuzz can interpose
# symbols in Avalonia's libHarfBuzzSharp and abort in hb_font_create/free().
# Ubuntu's libbz2.so.1.0 also stays private: Fedora ships a different SONAME.

# dlopen/PInvoke dependencies cannot be discovered from DT_NEEDED alone.
DLOPEN_ROOTS = [
    "libX11.so.6", "libICE.so.6", "libSM.so.6", "libXi.so.6", "libXrandr.so.2",
    "libXcursor.so.1", "libfontconfig.so.1", "libicuuc.so.74", "libicui18n.so.74",
    "libssl.so.3", "libcrypto.so.3", "libgssapi_krb5.so.2", "libasound.so.2",
    "libpulse.so.0", "libudev.so.1", "libusb-1.0.so.0", "libgnutls.so.30",
    "libgcrypt.so.20", "libunwind.so.8", "libv4l2.so.0", "libSDL2-2.0.so.0",
    "libpcap.so.0.8", "libpcsclite.so.1", "libxkbregistry.so.0",
    "libOSMesa.so.8", "libsecret-1.so.0",
]


def host_library(name):
    return name in HOST_SONAMES or bool(HOST.fullmatch(name))


def dynamic_entries(path):
    output = run("readelf", "-d", path, capture_output=True, text=True).stdout
    needed = re.findall(r"\(NEEDED\).*\[([^\]]+)\]", output)
    soname = re.search(r"\(SONAME\).*\[([^\]]+)\]", output)
    return needed, soname[1] if soname else ""


def remove_host_libraries(appdir):
    # Upstream runtime archives and published assets can already contain core
    # libraries. Remove their real files AND aliases before resolving anything,
    # so ldd cannot accidentally resolve against a copy that will later disappear.
    files = list(appdir.rglob("*"))
    removed = set()
    host_names = set()
    for path in files:
        if not elf(path):
            continue
        _, soname = dynamic_entries(path)
        if host_library(path.name) or host_library(soname):
            removed.add(path.resolve())
            host_names.add(soname or path.name)
    aliases = [p for p in files if p.is_symlink() and
               (host_library(p.name) or p.resolve() in removed)]
    for path in aliases:
        if host_library(path.name):
            host_names.add(path.name)
        path.unlink()
    for path in removed:
        path.unlink()
    return host_names


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


def relocate_fontconfig(appdir):
    # Fontconfig 2.15 scans FC_TEMPLATEDIR even with FONTCONFIG_FILE/PATH set
    # (FcInitLoadOwnConfig in src/fcinit.c). Host templates may use newer syntax.
    # A relative name is resolved through FONTCONFIG_PATH, which AppRun sets.
    library = (appdir / "usr/lib/orion/native/libfontconfig.so.1").resolve(strict=True)
    original = b"/usr/share/fontconfig/conf.avail\0"
    replacement = b"conf.avail\0".ljust(len(original), b"\0")
    data = library.read_bytes()
    if data.count(original) != 1:
        raise ValueError("Fontconfig template path changed; review portable relocation before packaging.")
    copy_tree(appdir / "usr/share/fontconfig/conf.avail", appdir / "etc/fonts/conf.avail")
    library.write_bytes(data.replace(original, replacement))


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


def build_reflex_layer(lock, cache, work, app):
    # The upstream Linux binary uses newer GLIBCXX symbols than Ubuntu 24.04.
    # Rebuild only this ELF from the SAME release; keep all Windows DLLs intact.
    manifests = list((app / "runtimes/winegdk").glob("*/share/wine/native/dxvk-nvapi/MANIFEST.json"))
    if len(manifests) != 1:
        raise ValueError("Expected one DXVK-NVAPI manifest")
    manifest_path = manifests[0]
    manifest = json.loads(manifest_path.read_text())
    if manifest["version"] != lock["dxvk-nvapi-source"]["tag"]:
        raise ValueError("DXVK-NVAPI source must match the bundled Wine release")
    relative = "layer/libdxvk_nvapi_vkreflex_layer.so"
    target = manifest_path.parent / relative
    if digest(target) != manifest["files"][relative]:
        raise ValueError("Bundled Reflex layer does not match its upstream manifest")
    sources = work / "reflex-source"
    sources.mkdir()
    for name, destination in (("dxvk-nvapi-source", sources),
                              ("vulkan-headers-source", sources / "headers"),
                              ("vkroots-source", sources / "roots")):
        destination.mkdir(exist_ok=True)
        with tarfile.open(download(lock[name], cache)) as archive:
            archive.extractall(destination, filter="data")
    source = next(sources.glob("dxvk-nvapi-*"))
    copy_tree(next((sources / "headers").iterdir()), source / "external/Vulkan-Headers")
    copy_tree(next((sources / "roots").iterdir()), source / "external/vkroots")
    build_dir = work / "reflex-build"
    run("meson", "setup", build_dir, source / "layer", "--buildtype=release")
    run("meson", "compile", "-C", build_dir)
    copy_tree(build_dir / target.name, target)
    manifest["portableRebuild"] = {
        "component": relative,
        "upstreamSha256": manifest["files"][relative],
        "source": lock["dxvk-nvapi-source"],
        "buildBase": "Ubuntu 24.04",
    }
    manifest["files"][relative] = digest(target)
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
    licenses = app.parents[2] / "usr/share/licenses/dxvk-nvapi-portable"
    for name in ("dxvk-nvapi-source", "vulkan-headers-source", "vkroots-source"):
        copy_tree(cache / lock[name]["sha256"], licenses / (name + ".tar.gz"))
    copy_tree(HERE / "package.py", licenses / "build-recipe.py")


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
    host_names = remove_host_libraries(appdir)
    cache = run("ldconfig", "-p", capture_output=True, text=True).stdout
    paths = {}
    for line in cache.splitlines():
        match = re.search(r"^\s*(\S+) .*x86-64.* => (\S+)$", line)
        if match:
            paths.setdefault(match[1], Path(match[2]))
    host_names.update(name for name in DLOPEN_ROOTS if host_library(name))
    # Only private dlopen roots need to exist in the build's ldconfig cache.
    pending = [paths[name] for name in DLOPEN_ROOTS if not host_library(name)]
    pending += [p for p in appdir.rglob("*") if elf(p)]
    seen = set()
    owned_packages = set()
    while pending:
        source = pending.pop()
        source = source.resolve()
        if source in seen:
            continue
        seen.add(source)
        needed, soname = dynamic_entries(source)
        if host_library(source.name) or host_library(soname):
            host_names.add(soname or source.name)
            continue
        if not source.is_relative_to(appdir):
            target = native / source.name
            copy_tree(source, target)
            # Preserve all loader names, including SONAME vs on-disk filename.
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
        resolved = {}
        for line in output.splitlines():
            match = re.search(r"^\s*(\S+) => (/\S+) ", line)
            if match:
                resolved[match[1]] = Path(match[2])
        # ldd prints the entire transitive tree, including dependencies used only
        # by host libraries. Follow DT_NEEDED edges ourselves and stop at the
        # host boundary instead of copying that unrelated system stack.
        for name in needed:
            if host_library(name):
                host_names.add(name)
            elif name in resolved:
                pending.append(resolved[name])
            else:
                raise RuntimeError(f"Could not resolve {name} required by {source}")
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
    packages = run("dpkg-query", "-W", "-f=${Package}=${Version}\n", *sorted(owned_packages),
                   capture_output=True, text=True).stdout.splitlines() if owned_packages else []
    return packages, sorted(host_names)


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
        build_reflex_layer(lock, cache, work, app)
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
                     "usr/share/glib-2.0/schemas", "usr/share/mime",
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
        packages, host_libraries = bundle_native(appdir, app)
        relocate_fontconfig(appdir)
        # bundle_native adds RUNPATH after rebuilding; record the final ELF hash.
        for manifest_path in (app / "runtimes/winegdk").glob("*/share/wine/native/dxvk-nvapi/MANIFEST.json"):
            manifest = json.loads(manifest_path.read_text())
            relative = manifest["portableRebuild"]["component"]
            manifest["files"][relative] = digest(manifest_path.parent / relative)
            manifest_path.write_text(json.dumps(manifest, indent=2) + "\n")
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
                    "nativePackages": packages, "hostLibraries": host_libraries}
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
