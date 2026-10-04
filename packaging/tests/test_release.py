import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("release", ROOT / "packaging/release.py")
release = importlib.util.module_from_spec(spec)
spec.loader.exec_module(release)


class ReleaseTests(unittest.TestCase):
    def fixture(self, root, version, notes="Release notes"):
        (root / "RELEASE_VERSION").write_text(version + "\n")
        if "/" not in version:
            file = root / f"docs/en_US/changelog/release/v{version}.md"
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text(notes)

    def test_version_tag_and_exact_notes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for version in ("1.0.0", "1.1.0-rc.1", "2.0.0-beta"):
                with self.subTest(version=version):
                    self.fixture(root, version)
                    result = release.metadata(root, version)
                    self.assertEqual(result["tag"], "v" + version)
                    self.assertEqual(result["prerelease"], str("-" in version).lower())
                    self.assertEqual(Path(result["notes"]).read_text(), "Release notes")

    def test_rejects_shell_paths_invalid_versions_and_mismatch(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for version in ("v1.0.0", "01.0.0", "1.0", "1.0.0-01", "1.0.0+build", "../secret", "1.0.0\nx=y", "$(whoami)", "999999.0.0"):
                with self.subTest(version=version):
                    (root / "RELEASE_VERSION").write_text(version)
                    with self.assertRaises(ValueError):
                        release.metadata(root)
            self.fixture(root, "1.0.0")
            with self.assertRaises(ValueError):
                release.metadata(root, "2.0.0")

    def test_missing_empty_or_linked_notes_are_not_published(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            self.fixture(root, "1.0.0", "   ")
            with self.assertRaises(ValueError):
                release.metadata(root)
            file = root / "docs/en_US/changelog/release/v1.0.0.md"
            file.unlink()
            with self.assertRaises(ValueError):
                release.metadata(root)
            file.symlink_to(root / "RELEASE_VERSION")
            with self.assertRaises(ValueError):
                release.metadata(root)

    def test_repository_release_is_ready(self):
        self.assertEqual(release.metadata()["tag"], "v" + (ROOT / "RELEASE_VERSION").read_text().strip())


if __name__ == "__main__":
    unittest.main()
