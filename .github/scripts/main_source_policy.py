"""Metadata-only main gate. Executed from the trusted base, never PR code."""
import json
import os
from pathlib import Path

from classify_issue import GitHub

REPOSITORY = "BedrockNative/OrionLauncher"
MESSAGE = """This PR was closed because `main` only accepts this repository's
`development` branch. Please open your contribution against `development`.
Maintainers publish reviewed changes through a `development` → `main` PR.

Este PR foi fechado porque a `main` só aceita a `development` deste repositório.
Abra sua contribuição com destino à `development`. Nenhum commit foi apagado.
"""


def allowed_source(pr, repository):
    head = pr.get("head") or {}
    return (head.get("ref") == "development"
            and (head.get("repo") or {}).get("full_name") == repository)


def enforce(api, repository, number):
    if repository != REPOSITORY or type(number) is not int or number < 1:
        raise ValueError("Invalid repository or PR number")
    path = f"/repos/{repository}/pulls/{number}"
    # Re-read live metadata rather than acting on a stale webhook payload.
    pr = api.request("GET", path)
    if pr.get("state") != "open" or pr.get("base", {}).get("ref") != "main":
        print("PR closed or retargeted; no action.")
        return True
    if (pr["base"].get("repo") or {}).get("full_name") != repository:
        raise ValueError("Unexpected base repository")
    if allowed_source(pr, repository):
        print("Accepted: same-repository development → main.")
        return True
    api.request("PATCH", path, {"state": "closed"})
    api.request("POST", f"/repos/{repository}/issues/{number}/comments",
                {"body": MESSAGE})
    print("Rejected: open contributions against development.")
    return False


if __name__ == "__main__":
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    api = GitHub(os.environ["GH_TOKEN"], os.environ.get("GITHUB_API_URL", "https://api.github.com"))
    raise SystemExit(0 if enforce(api, os.environ["GITHUB_REPOSITORY"], event["number"]) else 1)
