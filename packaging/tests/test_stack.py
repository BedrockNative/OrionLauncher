import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "linux"))
import package


class StackTests(unittest.TestCase):
    def test_fontconfig_relocation_preserves_binary_layout_and_private_templates(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            library = root / "usr/lib/orion/native/libfontconfig.so.1.12.1"
            library.parent.mkdir(parents=True)
            library.with_name("libfontconfig.so.1").symlink_to(library.name)
            original = b"/usr/share/fontconfig/conf.avail\0"
            data = b"\x7fELF\0before\0" + original + b"after\0"
            library.write_bytes(data)
            templates = root / "usr/share/fontconfig/conf.avail"
            templates.mkdir(parents=True)
            (templates / "49-sansserif.conf").write_text("bundled rule")
            local = root / "etc/fonts/conf.avail"
            local.mkdir(parents=True)
            (local / "57-dejavu.conf").write_text("font package rule")
            package.relocate_fontconfig(root)
            changed = library.read_bytes()
            self.assertEqual(len(changed), len(data))
            self.assertEqual(changed, data.replace(original, b"conf.avail\0".ljust(len(original), b"\0")))
            self.assertEqual((local / "49-sansserif.conf").read_text(), "bundled rule")
            self.assertEqual((local / "57-dejavu.conf").read_text(), "font package rule")

    def test_fontconfig_relocation_fails_closed_on_unknown_library(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            library = root / "usr/lib/orion/native/libfontconfig.so.1"
            library.parent.mkdir(parents=True)
            for data in (b"changed upstream path", b"/usr/share/fontconfig/conf.avail\0" * 2):
                library.write_bytes(data)
                with self.assertRaisesRegex(ValueError, "template path changed"):
                    package.relocate_fontconfig(root)
                self.assertEqual(library.read_bytes(), data)

    def test_reflex_rebuild_rejects_mismatched_runtime_before_download(self):
        lock = {"dxvk-nvapi-source": {"tag": "0.9.2"}}
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "app"
            folder = app / "runtimes/winegdk/wine-test/share/wine/native/dxvk-nvapi"
            folder.mkdir(parents=True)
            manifest = folder / "MANIFEST.json"
            manifest.write_text(json.dumps({"version": "other"}))
            with patch.object(package, "download") as download:
                with self.assertRaisesRegex(ValueError, "source must match"):
                    package.build_reflex_layer(lock, Path(directory), Path(directory), app)
                download.assert_not_called()
            (folder / "layer").mkdir()
            (folder / "layer/libdxvk_nvapi_vkreflex_layer.so").write_bytes(b"changed")
            manifest.write_text(json.dumps({"version": "0.9.2", "files": {
                "layer/libdxvk_nvapi_vkreflex_layer.so": "0" * 64}}))
            with patch.object(package, "download") as download:
                with self.assertRaisesRegex(ValueError, "upstream manifest"):
                    package.build_reflex_layer(lock, Path(directory), Path(directory), app)
                download.assert_not_called()

    def test_driver_wayland_abi_is_supplied_by_host(self):
        for name in ("libwayland-client.so.0", "libwayland-server.so.0",
                     "libwayland-cursor.so.0", "libwayland-egl.so.1", "libEGL.so.1"):
            self.assertTrue(package.host_library(name), name)
        for name in ("libwebkit2gtk-4.1.so.0", "libgtk-3.so.0", "libsecret-1.so.0"):
            self.assertFalse(package.host_library(name), name)

    def test_core_libraries_are_host_owned_but_application_abis_remain_private(self):
        for name in ("libstdc++.so.6", "libgcc_s.so.1", "libz.so.1", "libudev.so.1",
                     "libdbus-1.so.3", "libX11.so.6", "libxcb.so.1", "libasound.so.2",
                     "libpulse.so.0", "libanl.so.1", "libthread_db.so.1",
                     "libmount.so.1", "libblkid.so.1", "libcap.so.2", "libpcre2-8.so.0"):
            self.assertTrue(package.host_library(name), name)
        for name in ("libcoreclr.so", "libSkiaSharp.so", "libHarfBuzzSharp.so",
                     "libglib-2.0.so.0", "libicuuc.so.74", "libssl.so.3",
                     "libavcodec.so.63", "libgssapi_krb5.so.2", "libnss3.so",
                     "libbz2.so.1.0", "libfontconfig.so.1", "libfreetype.so.6",
                     "libharfbuzz.so.0", "libfribidi.so.0"):
            self.assertFalse(package.host_library(name), name)

    def test_upstream_core_library_and_all_aliases_are_removed_by_soname(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            core = root / "renamed-runtime.so"
            core.write_bytes(b"\x7fELF fixture")
            (root / "libstdc++.so.6").symlink_to(core.name)
            (root / "another-alias.so").symlink_to(core.name)
            private = root / "libcoreclr.so"
            private.write_bytes(b"\x7fELF fixture")
            def entries(path):
                return [], "libstdc++.so.6" if path == core else "libcoreclr.so"
            with patch.object(package, "dynamic_entries", side_effect=entries):
                names = package.remove_host_libraries(root)
            self.assertEqual(names, {"libstdc++.so.6"})
            self.assertEqual(list(root.iterdir()), [private])

    def test_dependency_collection_stops_at_host_boundary_and_skips_host_roots(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            appdir = root / "AppDir"
            app = appdir / "usr/lib/orion"
            app.mkdir(parents=True)
            launcher = app / "launcher"
            launcher.write_bytes(b"\x7fELF fixture")
            library = root / "libprivate.so.1.2"
            library.write_bytes(b"\x7fELF fixture")

            def run(*args, **kwargs):
                if args[:2] == ("ldconfig", "-p"):
                    # No host root in ldconfig: it must never be looked up/copied.
                    output = f" libprivate.so.1 (libc6,x86-64) => {library}\n"
                elif args[:2] == ("readelf", "-d"):
                    output = " (NEEDED) Shared library: [libudev.so.1]\n"
                    if args[2] == launcher:
                        output += " (NEEDED) Shared library: [libprivate.so.1]\n"
                    else:
                        output += " (SONAME) Library soname: [libprivate.so.1]\n"
                else:
                    output = ""
                return subprocess.CompletedProcess(args, 0, output, "")

            def subprocess_run(args, **kwargs):
                if args[0] == "ldd":
                    output = (f" libprivate.so.1 => {library} (0x1234)\n"
                              " libudev.so.1 => /host/libudev.so.1 (0x5678)\n"
                              " libhost-only.so.1 => /host/libhost-only.so.1 (0x9012)\n")
                    return subprocess.CompletedProcess(args, 0, output, "")
                return subprocess.CompletedProcess(args, 1, "", "")

            with patch.object(package, "DLOPEN_ROOTS", ["libudev.so.1", "libprivate.so.1"]), \
                 patch.object(package, "run", side_effect=run), \
                 patch.object(package.subprocess, "run", side_effect=subprocess_run):
                packages, hosts = package.bundle_native(appdir, app)
            self.assertEqual(packages, [])
            self.assertEqual(hosts, ["libudev.so.1"])
            self.assertEqual((app / "native" / library.name).read_bytes(), library.read_bytes())
            self.assertEqual(os.readlink(app / "native/libprivate.so.1"), library.name)
            self.assertFalse((app / "native/libhost-only.so.1").exists())

    def test_wine_addons_match_embedded_contract_before_copying(self):
        lock = json.loads((package.HERE / "stack.lock.json").read_text())
        names = ("wine-mono", "wine-gecko-x86", "wine-gecko-x64")
        with tempfile.TemporaryDirectory() as directory:
            app = Path(directory) / "app"
            wine = app / "runtimes/winegdk/wine-test"
            (wine / "bin").mkdir(parents=True)
            (wine / "bin/wine").touch()
            installer = wine / "appwiz.cpl"
            installer.write_bytes(b"\0".join(
                lock[n]["fileName"].encode("utf-16le") + b"\0" + lock[n]["sha256"].encode() for n in names))
            msi = Path(directory) / "fixture.msi"
            msi.write_bytes(b"verified download fixture")
            with patch.object(package, "download", return_value=msi) as download:
                package.bundle_wine_addons(app, lock, Path(directory))
                self.assertEqual(download.call_count, 3)
                for name in names:
                    item = lock[name]
                    self.assertEqual((wine / "share/wine" / item["subdir"] / item["fileName"]).read_bytes(), msi.read_bytes())
            installer.write_bytes(b"upstream changed")
            with patch.object(package, "download") as download:
                with self.assertRaises(ValueError):
                    package.bundle_wine_addons(app, lock, Path(directory))
                download.assert_not_called()

    def test_all_upstream_downloads_are_https_and_sha256_pinned(self):
        lock = json.loads((package.HERE / "stack.lock.json").read_text())
        for item in lock.values():
            self.assertTrue(item["url"].startswith("https://"))
            self.assertRegex(item["sha256"], r"^[0-9a-f]{64}$")
