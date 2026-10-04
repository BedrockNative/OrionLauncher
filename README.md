# Orion Launcher

A Minecraft Bedrock launcher for Linux.

Independent instances, an isolated Xodus account profile, WineGDK runtime updates,
optional per-instance Linux application shortcuts (off by default), and a launcher that can stay in the background.
Available in English and Brazilian Portuguese.

## Run

Building requires Linux x86_64, .NET SDK 10, CMake and MinGW-w64 C++.
Playing requires Vulkan drivers and a Minecraft for Windows license.

```sh
dotnet run --project src/Orion.Desktop
```

Sign in on **Account**, then create an instance from the version catalog.
Follow background installations on **Downloads**; interrupted work resumes next time.
Official portable packages include a pinned, tested BedrockNative runtime stack;
runtime updates remain available from the launcher. Development builds download
the latest stable runtimes when needed.

Manage worlds, addons and textures from **Content** in the main navigation.
Share packs between instances without duplicating them; worlds stay independent.
Official keyed builds also offer CurseForge discovery and installation;
[local builds can use your own key](docs/en_US/curseforge-build.md#local-development).

**RTX Studio** browses BetterRTX presets and Vanilla RTX packs, installs them per
instance, and offers reversible shader changes and ray-tracing launch preferences.

Automated checks cover the launcher; compatibility
with Microsoft services and individual game versions also depends on Xodus and WineGDK.

[Setup and usage](docs/en_US/getting-started.md) ·
[Architecture](docs/en_US/architecture.md) ·
[Development](docs/en_US/development.md) ·
[Releases and packaging](RELEASING.md) ·
[Content](docs/en_US/content.md) ·
[RTX](docs/en_US/rtx.md) ·
[Appearance](docs/en_US/appearance-options.md) ·
[Changelog](CHANGELOG.md)

## Questions, bugs and suggestions

[Open an issue](https://github.com/BedrockNative/OrionLauncher/issues/new/choose)
and choose **Bug**, **Question / Pergunta** or **Suggestion / Sugestão**.
Portuguese and English are welcome. The forms apply the existing `bug`,
`question` and `enhancement` labels respectively.

For bugs, include the Orion version, operating system/distribution and version,
kernel and version, desktop/window manager, display session, steps to reproduce
and expected behavior. Screenshots, videos and relevant logs are encouraged;
remove credentials and personal information before uploading them.

Maintainers: forms live in `.github/ISSUE_TEMPLATE/` and become available when
merged into the repository's default branch. Keep the three labels above present
in GitHub; templates reference labels but do not create them.

The lightweight **Issue classification** workflow handles issues opened/reopened
or relabeled without one of those categories, including issues submitted through
the API. It adds `needs-classification` (creating that label if necessary) and
posts one bilingual reminder per issue. Authors without label permissions can
reply with the category for a maintainer to apply. Once a category is applied,
the pending label is removed automatically; other labels and comments are kept.
Issues are never closed automatically. This is a triage safeguard, not a block
on API submission or validation of the report's contents. It does not run on
development commits and does not build the launcher or access release secrets.

Local automation tests: `python3 -m unittest discover -s .github/tests -v`.

## Credits

Maintained by [yPerfectBR](https://github.com/yPerfectBR) and
[raonygamer](https://github.com/raonygamer).

- [BedrockNative/Xodus](https://github.com/BedrockNative/xodus) and
  [BedrockNative/WineGDK](https://github.com/BedrockNative/WineGDK): runtime forks.
- [JaymeFernandes](https://github.com/JaymeFernandes): interface translation system.
- [BetterRTX](https://bedrock.graphics) and
  [Cubeir](https://github.com/Cubeir/Vanilla-RTX): RTX catalog, formats and texture packs.

[MIT license](LICENSE).

<sub>Orion Launcher is not affiliated with or associated with Mojang or Microsoft.</sub>
