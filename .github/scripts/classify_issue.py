"""Reconcile issue categories without closing issues or replacing other labels.

Uses only the workflow's scoped GITHUB_TOKEN. No dependency installs, issue-body
parsing, shell evaluation, user-provided code or personal access tokens.
"""
import json
import os
from pathlib import Path
import re
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen

CATEGORIES = {"bug", "question", "enhancement"}
PENDING = "needs-classification"
MARKER = "<!-- orion:issue-classification:v1 -->"


class GitHub:
    def __init__(self, token, base_url="https://api.github.com"):
        self.token = token
        self.base_url = base_url.rstrip("/")

    def request(self, method, path, data=None):
        request = Request(self.base_url + path, method=method,
                          data=None if data is None else json.dumps(data).encode(),
                          headers={"Authorization": "Bearer " + self.token,
                                   "Accept": "application/vnd.github+json",
                                   "Content-Type": "application/json",
                                   "X-GitHub-Api-Version": "2022-11-28",
                                   "User-Agent": "Orion-Issue-Classification"})
        with urlopen(request, timeout=30) as response:
            body = response.read()
        return json.loads(body) if body else None


def labels(issue):
    return {label["name"].casefold() for label in issue.get("labels", [])}


def ensure_pending_label(api, repo_path):
    path = repo_path + "/labels/" + quote(PENDING, safe="")
    try:
        api.request("GET", path)
        return
    except HTTPError as error:
        if error.code != 404:
            raise
        error.close()
    try:
        api.request("POST", repo_path + "/labels", {
            "name": PENDING, "color": "FBCA04",
            "description": "Needs bug, question or enhancement / Precisa de classificação"})
    except HTTPError as error:
        if error.code != 422:
            raise
        error.close()
        # Another issue's workflow may have created the label concurrently.
        # Confirm it exists instead of hiding unrelated validation failures.
        api.request("GET", path)


def remove_pending(api, issue_path, issue):
    if PENDING in labels(issue):
        try:
            api.request("DELETE", issue_path + "/labels/" + quote(PENDING, safe=""))
        except HTTPError as error:
            if error.code != 404:
                raise
            error.close()


def classify_issue(api, repository, number):
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid repository")
    if type(number) is not int or number < 1:
        raise ValueError("Invalid issue number")
    repo_path = "/repos/" + repository
    issue_path = repo_path + "/issues/" + str(number)
    # Fetch current state: queued webhook labels may already be stale.
    issue = api.request("GET", issue_path)
    if "pull_request" in issue:
        return
    if labels(issue) & CATEGORIES:
        remove_pending(api, issue_path, issue)
        return
    if issue.get("state") != "open":
        return
    ensure_pending_label(api, repo_path)
    if PENDING not in labels(issue):
        api.request("POST", issue_path + "/labels", {"labels": [PENDING]})
    # Do not notify if a maintainer classified/closed it while we added the label.
    issue = api.request("GET", issue_path)
    if labels(issue) & CATEGORIES:
        remove_pending(api, issue_path, issue)
        return
    if issue.get("state") != "open" or issue.get("locked"):
        return
    page = 1
    while True:
        comments = api.request("GET", issue_path + f"/comments?per_page=100&page={page}")
        if any(MARKER in (comment.get("body") or "")
               and comment.get("user", {}).get("login") == "github-actions[bot]"
               and comment.get("user", {}).get("type") == "Bot" for comment in comments):
            return
        if len(comments) < 100:
            break
        page += 1
    api.request("POST", issue_path + "/comments", {"body": f"""{MARKER}
### Classificação necessária / Classification needed

Esta issue ainda não tem uma categoria. Informe em uma resposta se ela é:
- **Bug** (`bug`): algo não funciona corretamente.
- **Pergunta** (`question`): dúvida sobre configuração ou uso.
- **Sugestão** (`enhancement`): proposta de recurso ou melhoria.

Se você puder editar as etiquetas, aplique a correspondente. Caso contrário,
a equipe poderá aplicá-la após sua resposta. A sinalização `{PENDING}` será
removida automaticamente quando uma dessas etiquetas for aplicada.

Para bugs, inclua sistema operacional e versão, kernel e versão, gerenciador
de janelas, versão do Orion e passos para reproduzir. Fotos, vídeos e logs
ajudam; remova dados pessoais e credenciais antes de anexá-los.

---

This issue has no category yet. Reply with **bug**, **question** or
**suggestion** (`enhancement`). Apply the label if you have permission;
otherwise a maintainer can do so after your reply. The pending label is
removed automatically once a category is applied. For bugs, include your OS,
kernel, window manager, Orion version and reproduction steps. Screenshots,
videos and sanitized logs are welcome.

For future reports / Para próximos relatos:
[choose a form / escolha um formulário](https://github.com/{repository}/issues/new/choose).
"""})


def main():
    if os.environ.get("GITHUB_EVENT_NAME") != "issues":
        raise ValueError("This script only handles issue events")
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    if event.get("action") not in {"opened", "reopened", "labeled", "unlabeled"}:
        return
    api = GitHub(os.environ["GH_TOKEN"], os.environ.get("GITHUB_API_URL", "https://api.github.com"))
    classify_issue(api, os.environ["GITHUB_REPOSITORY"], event["issue"]["number"])


if __name__ == "__main__":
    main()
