import copy
from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "scripts"))
from main_source_policy import REPOSITORY, enforce


class Api:
    def __init__(self, pr):
        self.pr, self.calls = pr, []

    def request(self, method, path, data=None):
        self.calls.append((method, path, data))
        return copy.deepcopy(self.pr) if method == "GET" else None


class MainSourcePolicyTests(unittest.TestCase):
    def setUp(self):
        self.pr = {"state": "open", "base": {"ref": "main", "repo": {"full_name": REPOSITORY}},
                   "head": {"ref": "development", "repo": {"full_name": REPOSITORY}}}

    def test_same_repository_development_is_read_only(self):
        api = Api(self.pr)
        self.assertTrue(enforce(api, REPOSITORY, 2))
        self.assertEqual([c[0] for c in api.calls], ["GET"])

    def assert_rejected(self):
        api = Api(self.pr)
        self.assertFalse(enforce(api, REPOSITORY, 2))
        self.assertEqual([c[0] for c in api.calls], ["GET", "PATCH", "POST"])
        self.assertEqual(api.calls[1][2], {"state": "closed"})

    def test_other_branch_closed(self):
        self.pr["head"]["ref"] = "feature/example"
        self.assert_rejected()

    def test_fork_named_development_closed(self):
        self.pr["head"]["repo"]["full_name"] = "someone/OrionLauncher"
        self.assert_rejected()

    def test_deleted_source_closed(self):
        self.pr["head"]["repo"] = None
        self.assert_rejected()

    def test_retargeted_pr_not_closed(self):
        self.pr["base"]["ref"] = "development"
        api = Api(self.pr)
        self.assertTrue(enforce(api, REPOSITORY, 2))
        self.assertEqual(len(api.calls), 1)

    def test_closed_pr_not_commented_again(self):
        self.pr["state"] = "closed"
        api = Api(self.pr)
        self.assertTrue(enforce(api, REPOSITORY, 2))
        self.assertEqual(len(api.calls), 1)

    def test_invalid_inputs_never_call_api(self):
        for repo, number in [("other/repo", 2), (REPOSITORY, True), (REPOSITORY, 0),
                             (REPOSITORY, "../../issues/1")]:
            api = Api(self.pr)
            with self.assertRaises(ValueError):
                enforce(api, repo, number)
            self.assertFalse(api.calls)

    def test_wrong_base_repository_fails_closed(self):
        self.pr["base"]["repo"]["full_name"] = "other/repo"
        with self.assertRaises(ValueError):
            enforce(Api(self.pr), REPOSITORY, 2)
