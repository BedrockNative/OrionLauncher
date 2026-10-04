import importlib.util
from pathlib import Path
import unittest
from urllib.error import HTTPError

spec = importlib.util.spec_from_file_location("classify_issue", Path(__file__).parents[1] / "scripts/classify_issue.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class FakeGitHub:
    def __init__(self, labels=(), state="open"):
        self.issue = {"state": state, "labels": [{"name": name} for name in labels]}
        self.comments = []
        self.label_exists = False
        self.calls = []
        self.race = False

    def request(self, method, path, data=None):
        self.calls.append((method, path, data))
        if path.endswith("/issues/7"):
            return {**self.issue, "labels": list(self.issue["labels"])}
        if "/comments?" in path:
            page = int(path.rsplit("=", 1)[1])
            return self.comments[(page - 1) * 100:page * 100]
        if path.endswith("/comments"):
            self.comments.append({"body": data["body"], "user": {"type": "Bot", "login": "github-actions[bot]"}})
        elif method == "GET" and path.endswith("/labels/needs-classification"):
            if not self.label_exists:
                raise HTTPError(path, 404, "missing", {}, None)
            return {"name": module.PENDING}
        elif method == "POST" and path.endswith("/issues/7/labels"):
            self.issue["labels"].extend({"name": name} for name in data["labels"])
        elif method == "POST" and path.endswith("/labels"):
            self.label_exists = True
            if self.race:
                raise HTTPError(path, 422, "already exists", {}, None)
        elif method == "DELETE":
            self.issue["labels"] = [label for label in self.issue["labels"] if label["name"] != module.PENDING]
        else:
            raise AssertionError((method, path))


class IssueClassificationTests(unittest.TestCase):
    def run_issue(self, api):
        module.classify_issue(api, "BedrockNative/OrionLauncher", 7)

    def test_uncategorized_gets_one_comment_and_preserves_unrelated_labels(self):
        api = FakeGitHub(["documentation"])
        self.run_issue(api)
        self.run_issue(api)
        self.assertEqual({"documentation", module.PENDING}, module.labels(api.issue))
        self.assertEqual(1, len(api.comments))
        self.assertIn("a equipe", api.comments[0]["body"])

    def test_each_category_removes_only_pending_label(self):
        for category in ["bug", "question", "enhancement", "BUG"]:
            api = FakeGitHub([category, module.PENDING, "documentation"])
            self.run_issue(api)
            self.assertEqual({category.casefold(), "documentation"}, module.labels(api.issue))
            self.assertEqual([], api.comments)

    def test_removing_last_category_marks_again_without_comment_spam(self):
        api = FakeGitHub()
        self.run_issue(api)
        api.issue["labels"].append({"name": "bug"})
        self.run_issue(api)
        api.issue["labels"] = []
        self.run_issue(api)
        self.assertEqual({module.PENDING}, module.labels(api.issue))
        self.assertEqual(1, len(api.comments))

    def test_closed_and_pull_requests_are_ignored(self):
        for api in [FakeGitHub(state="closed"), FakeGitHub()]:
            if api.issue["state"] == "open":
                api.issue["pull_request"] = {}
            self.run_issue(api)
            self.assertTrue(all(method == "GET" for method, _, _ in api.calls))

    def test_locked_issue_can_be_labeled_without_comment(self):
        api = FakeGitHub()
        api.issue["locked"] = True
        self.run_issue(api)
        self.assertEqual({module.PENDING}, module.labels(api.issue))
        self.assertEqual([], api.comments)

    def test_label_creation_race_is_confirmed(self):
        api = FakeGitHub()
        api.race = True
        self.run_issue(api)
        self.assertEqual(1, len(api.comments))

    def test_pagination_and_user_cannot_spoof_bot_marker(self):
        api = FakeGitHub()
        api.comments = [{"body": module.MARKER, "user": {"login": "author", "type": "User"}}] * 101
        self.run_issue(api)
        self.run_issue(api)
        self.assertEqual(102, len(api.comments))
        self.assertTrue(any("page=2" in path for _, path, _ in api.calls))

    def test_invalid_target_never_calls_api(self):
        api = FakeGitHub()
        for repository, number in [("../bad/path", 7), ("org/repo", "7;command"), ("org/repo", -1)]:
            with self.assertRaises(ValueError):
                module.classify_issue(api, repository, number)
        self.assertEqual([], api.calls)


if __name__ == "__main__":
    unittest.main()
