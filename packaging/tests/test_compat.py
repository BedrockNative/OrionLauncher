import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch, Mock

COMPAT = Path(__file__).resolve().parents[1] / "linux/compat"


def load(name):
    spec = importlib.util.spec_from_file_location(name, COMPAT / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class CompatibilityHarnessTests(unittest.TestCase):
    def test_supported_matrix_and_repository_root(self):
        matrix = load("matrix")
        self.assertEqual(matrix.ROOT, COMPAT.parents[2])
        self.assertEqual(set(matrix.TARGETS), {"arch", "debian", "fedora", "ubuntu"})
        self.assertRegex(matrix.harness_hash(), r"^[0-9a-f]{64}$")

    def test_stage_keeps_failure_evidence_and_fatal_stages_stop(self):
        probe = load("probe")
        with tempfile.TemporaryDirectory() as folder, patch.object(probe, "REPORT", Path(folder)):
            with probe.stage("broken"):
                raise RuntimeError("missing dependency")
            with probe.stage("independent"):
                pass
            with self.assertRaises(RuntimeError):
                with probe.stage("checksum", fatal=True):
                    raise RuntimeError("modified artifact")
            results = json.loads((Path(folder) / "stages.json").read_text())
            self.assertEqual([r["status"] for r in results], ["failed", "passed", "failed"])
            self.assertIn("missing dependency", results[0]["error"])

    def test_gui_process_exit_is_not_a_successful_smoke_test(self):
        probe = load("probe")
        process = Mock()
        process.poll.return_value = 0
        process.returncode = 0
        with tempfile.TemporaryDirectory() as folder, patch.object(probe, "REPORT", Path(folder)), \
                patch.object(probe.subprocess, "Popen", return_value=process):
            with self.assertRaisesRegex(RuntimeError, "before showing a window"):
                probe.gui("/fixture/AppRun", "gui", {})
