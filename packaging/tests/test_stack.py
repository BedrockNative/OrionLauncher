import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "linux"))
import package


class StackTests(unittest.TestCase):
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
            self.assertIsNotNone(package.HOST.fullmatch(name), name)
        for name in ("libwebkit2gtk-4.1.so.0", "libgtk-3.so.0", "libsecret-1.so.0"):
            self.assertIsNone(package.HOST.fullmatch(name), name)

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
