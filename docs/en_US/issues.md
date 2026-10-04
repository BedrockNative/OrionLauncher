# Questions, bugs and suggestions

[Open an issue](https://github.com/BedrockNative/OrionLauncher/issues/new/choose)
and select **Bug**, **Question / Pergunta** or **Suggestion / Sugestão**.
English and Portuguese are welcome. Search existing issues first.

## Reporting a bug

Include the Orion version, operating system/distribution and version, kernel and
version, desktop/window manager, X11/Wayland session, reproduction steps, expected
behavior and what actually happened. For graphics problems, include the GPU and
driver version. Attach a screenshot or video and relevant instance/launcher logs
when possible. Remove tokens, credentials and personal information before uploading.

RTX and content management are experimental. Mention the provider, preset/pack,
Minecraft version and whether the problem occurs without that content. Back up
important worlds before experimenting; do not delete your data to file a report.

## Categories and automation

Forms apply `bug`, `question` or `enhancement`. Blank issues are disabled in the
web chooser. API-created issues can still lack a category: **Issue classification**
adds `needs-classification` and one bilingual reminder. Authors without permission
to label can reply with a category for a maintainer to apply. Once categorized,
the pending label is removed. Other labels/comments remain and issues are never
closed automatically. This is triage, not a guarantee that reports are complete.

Maintainers: keep these labels present. Forms live in `.github/ISSUE_TEMPLATE/`
on the default branch. The lightweight workflow does not build the launcher or
access release credentials. Test automation with:

```sh
python3 -m unittest discover -s .github/tests -v
```

For code changes, see [contributing](contributing.md). Issue categories and
pull-request branch routing are separate policies.
