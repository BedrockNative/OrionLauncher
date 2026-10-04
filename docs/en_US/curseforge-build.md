# CurseForge build credentials

## Security boundary

There is no plaintext API key in the source repository. The official workflow
reads `CURSEFORGE_API_KEY` from the GitHub Actions **environment secret** in
`curseforge-release`, restricted to `main` and requiring maintainer approval.
The ordinary main-branch validation workflow has no reference to this secret or environment.
External contributors require approval for fork workflow runs. Neither workflow
uses `pull_request_target`, untrusted artifact promotion, or privileged PR code.

The build helper generates a binary resource under ignored `obj/`, using fresh
random XOR masks on each build. This only obscures static strings. **Anyone who
obtains the binary can reconstruct the key or observe it in process memory.**
No client-side encryption/obfuscation can prevent this. Use a server-side proxy
if actual credential confidentiality becomes a requirement.

The injected secret is scoped to the generator execution, not restore, tests,
compiler arguments, or the publish environment. No generated C# contains the key.
Do not enable shell tracing, diagnostic environment dumps, or MSBuild binary logs
on a keyed build. Upload only the runtime archive, never `obj/`, generated resource
files, launcher source bundles, or test artifacts from a keyed build. Required
third-party license/source material remains inside the portable packages. The official workflow
does not include PDB files and removes its generated resource afterward.

GitHub masking is defense in depth, not a guarantee: transformed values may not
be masked. Rotate a key if it is exposed. This key was originally supplied through
chat; rotate it too if access to that conversation is not acceptable.

## Maintainer workflow

1. Review and merge the changes into `main`. `CODEOWNERS` identifies sensitive
   paths; enforce required code-owner reviews in branch rules if desired. The
   file alone does **not** enforce review.
2. Change `RELEASE_VERSION` and merge into `main`, or run **Release Linux** manually
   from `main` with that same version. See [RELEASING.md](../../RELEASING.md).
3. Review the exact commit before approving the `curseforge-release` environment.
   Never approve a commit that executes untrusted code with secrets.
4. After validation, the workflow publishes AppImage and tar.gz files, creates the
   version tag at the tested commit and uses the matching English changelog as the
   release description. It never pushes commits or overwrites existing releases.

Rotate the secret through GitHub's environment settings or the CLI's hidden
interactive prompt (do not put the value in a command argument):

```sh
gh secret set CURSEFORGE_API_KEY --repo BedrockNative/OrionLauncher --env curseforge-release
```

Environment approval is currently assigned to `yPerfectBR`. Authorized maintainers
can change that policy; administrator access can also change workflows/policies.
Do not describe this model as protection against repository administrators.

## Local development

Normal `dotnet build` and unit tests need no secret. Local builds and custom forks
can use a developer-owned key **without rebuilding or accessing GitHub Secrets**.
Fully exit Orion first (including its tray/background process). Runtime precedence:

1. `CURSEFORGE_API_KEY` environment variable.
2. `ORION_CURSEFORGE_API_KEY_FILE`, an absolute path to a private UTF-8 text file.
3. The official build's embedded credential, if present.

Use a hidden terminal prompt in Bash; the key itself is not part of shell history
or the process command line:

```bash
read -rs -p 'CurseForge developer key: ' CURSEFORGE_API_KEY
export CURSEFORGE_API_KEY
./artifacts/linux-x64-appearance/OrionLauncher
unset CURSEFORGE_API_KEY
```

Alternatively, use a private file outside your repository. Create it with your
password manager or editor, then give only its owner access (`chmod 600`). The
file contains just the key; a trailing newline is accepted. Symlinks, relative
paths and files larger than 4096 bytes are rejected.

```bash
ORION_CURSEFORGE_API_KEY_FILE=/absolute/private/path/curseforge.key \
  ./artifacts/linux-x64-appearance/OrionLauncher
```

Orion does not save either override in settings, print it, or send it to child
processes. Startup captures the override then unsets these environment variables;
the game process runner also strips them, including instance environment overrides.
The value still exists in launcher memory, and an environment supplied at process
creation can be observable by the same user or administrator. A private file avoids
putting the key itself in that initial environment. Neither mechanism protects
against privileged inspection, shell tracing, debuggers, or malicious local code.
Restart Orion after changing an override. An invalid override reports an error,
rather than silently falling back to a different account's key.

Run offline tests normally, or explicitly opt in to the live API smoke test using
the same private-file variable:

```bash
dotnet test OrionLauncher.slnx
ORION_CURSEFORGE_API_KEY_FILE=/absolute/private/path/curseforge.key \
  dotnet run --project tools/Orion.CurseForgeSmoke
# Add -- --download for a small package integrity/import test in temporary storage.
```

To distribute your own fork, configure its own `curseforge-release` environment
and `CURSEFORGE_API_KEY` secret; forks never inherit this repository's secret.
Review that workflow's environment reviewers and branch restrictions for your fork.
The official workflow also has an explicit repository-name guard; change it to
your fork only after configuring its own protected environment and credential.

For a private keyed build with no runtime override, compile
`tools/Orion.BuildCredentials` first, run its DLL with `CURSEFORGE_API_KEY` supplied
through a private environment only, targeting
`src/Orion.Infrastructure/obj/curseforge.key`, then unset that variable before
publishing. Remove the generated resource after publishing. Never commit it.
Deleting it and rebuilding returns to an unconfigured development build.

The runtime sends the key only in an `x-api-key` header to
`https://api.curseforge.com`. API redirects are not followed; response bodies are
not included in errors. CDN requests have a separate client with no credential.

References: [GitHub Actions security](https://docs.github.com/en/actions/reference/security/secure-use),
[fork workflow approvals](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/approve-runs-from-forks),
[CurseForge API](https://docs.curseforge.com/rest-api/).
