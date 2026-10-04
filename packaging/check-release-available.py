"""Fail closed on API errors or an existing tag/release; never overwrite a release."""
import os
import urllib.error
import urllib.request
from release import metadata

repo = os.environ["GITHUB_REPOSITORY"]
tag = metadata()["tag"]
for endpoint in (f"git/ref/tags/{tag}", f"releases/tags/{tag}"):
    request = urllib.request.Request(f"https://api.github.com/repos/{repo}/{endpoint}", headers={
        "Authorization": f"Bearer {os.environ['GH_TOKEN']}", "Accept": "application/vnd.github+json"})
    try:
        with urllib.request.urlopen(request, timeout=30):
            pass
    except urllib.error.HTTPError as error:
        if error.code == 404:
            continue
        raise
    raise SystemExit(f"{tag} already exists. Publish a new version; existing tags/releases are immutable.")
