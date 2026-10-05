# Contributing

Open feature, fix and documentation PRs against **`development`**, including PRs
from forks. Use focused Conventional Commits (`fix:`, `feat:`, `docs:`, `ci:`).
See [development setup](development.md) and [issue reporting](issues.md).

## Branch policy

- `development` is the working branch. Do not develop or push directly on `main`.
- Only this repository's `development` may open a mergeable PR to `main`.
- `main` requires a PR, the **Main source policy** check from GitHub Actions,
  resolved review conversations, and a merge commit. Force-push and deletion
  are blocked. The ruleset has no bypass actors.
- A fork's branch called `development` is not an allowed source for `main`.
- GitHub cannot forbid opening a PR based on its source branch. Automation closes
  incorrectly targeted PRs with guidance; it does not delete their branches or commits.
  Repository administrators can still edit GitHub settings; these rules are not
  a security boundary against an administrator changing the policy itself.

The lightweight source workflow runs on `pull_request_target`, checking current
PR metadata with trusted **base-branch** code only. Never check out/run PR code,
interpolate PR text into shell commands, or give this workflow release secrets.
Development contributions do not trigger release/build workflows.

The checked-in [.github/rulesets/main.json](../../.github/rulesets/main.json) is
the main ruleset template. Changing it alone does not change GitHub settings.
Install the workflow on `main` before requiring its check; do not require the
post-merge `linux` build as a pre-merge check, because it only runs after a main push.

## Releases and merges without publication

Accumulate future changes in a **new draft** release note on `development`, for
example `docs/en_US/changelog/release/v1.0.1.md`, with the corresponding Portuguese
file and changelog index. Do not add future changes to 1.0.0 notes.

For an authorized release, finish validation, update **`RELEASE_VERSION`** to the
version without `v`, and merge `development` → `main` using a merge commit.
Actions uses the matching English Markdown as the release description and creates
the tag and AppImage/tar.gz artifacts. Keep human approval of `curseforge-release`.

An explicitly authorized integration, including code or documentation, may leave
`RELEASE_VERSION` unchanged. It runs main CI without publishing a new release.
Creating a PR and requesting review do not authorize merging it; wait for an
explicit merge instruction. Release publication also requires explicit authorization.
Neither a changelog filename nor a merge-message marker is a release flag.

Read [RELEASING.md](../../RELEASING.md) and [AGENTS.md](../../AGENTS.md) before
preparing a release. The Arch/Debian/Fedora portability matrix is local and opt-in,
not a GitHub Actions job or a requirement for every documentation commit.

Validate repository automation locally:

```bash
python3 -m unittest discover -s .github/tests -v
actionlint
git diff --check
```
