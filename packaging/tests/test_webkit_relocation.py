"""Verify our offset-preserving relocation without needing GTK or a desktop."""
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(shutil.which("cc"), "native C compiler required")
class WebKitRelocationTests(unittest.TestCase):
    def test_exact_strings_only_and_fail_closed(self):
        with tempfile.TemporaryDirectory() as work:
            work = Path(work)
            tool = work / "relocate"
            subprocess.run(["cc", "-Wall", "-Wextra", "-Werror", str(ROOT / "packaging/linux/relocate-webkit.c"), "-o", str(tool)], check=True)
            original = [b"/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1", b"/usr/bin/bwrap", b"/usr/bin/xdg-dbus-proxy"]
            payload = b"\x7fELF\0" + b"\0".join(original) + b"\0" + original[0] + b"/injected-bundle/\0"
            source = work / "input.so"
            source.write_bytes(payload)
            private = Path(subprocess.check_output(["mktemp", "-d", "/tmp/oXXXXXX"], text=True).strip())
            try:
                subprocess.run([str(tool), str(source), str(private)], check=True)
                target = private / "libwebkit2gtk-4.1.so.0"
                result = target.read_bytes()
                self.assertEqual(len(payload), len(result))
                self.assertIn(original[0] + b"/injected-bundle/\0", result)
                for old, suffix in zip(original, (b"/w", b"/b", b"/d")):
                    offset = payload.index(old)
                    new = str(private).encode() + suffix
                    self.assertEqual(result[offset:offset + len(old)], new.ljust(len(old), b"\0"))
                # Never overwrite an existing file, even in the private directory.
                self.assertNotEqual(subprocess.run([str(tool), str(source), str(private)], capture_output=True).returncode, 0)
                target.unlink()
                source.write_bytes(b"\x7fELF\0upstream-changed\0")
                self.assertNotEqual(subprocess.run([str(tool), str(source), str(private)], capture_output=True).returncode, 0)
                self.assertFalse(target.exists())
            finally:
                shutil.rmtree(private)
