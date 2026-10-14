# Development

Install .NET SDK 10 and initialize the XvdTool dependency with
`git submodule update --init --recursive`. LibXboxOne is built with the launcher.

```sh
dotnet restore OrionLauncher.slnx
dotnet build OrionLauncher.slnx -c Release --no-restore
dotnet test OrionLauncher.slnx -c Release --no-build
dotnet run --project src/Orion.Desktop
```

.NET SDK 10 is selected by `global.json`. Nullable analysis and warnings-as-errors
apply throughout the solution. Dependency vulnerability checks remain enabled.
Avalonia is pinned across its packages; the explicit D-Bus reference supplies the
security fix missing from Avalonia's transitive default.

## Publish

```sh
dotnet publish src/Orion.Desktop -c Release -r linux-x64 --self-contained true -o artifacts/linux-x64
./artifacts/linux-x64/OrionLauncher
```

Distribute the entire output directory, including `Assets/orion.svg`. Do not copy only
the executable. Official AppImage and tar.gz releases include private native
dependencies and pinned runtimes. Core system libraries and the common desktop
stack (X11/XCB, audio and device/session clients) come from the host;
see [RELEASING.md](../../RELEASING.md) for the baseline and main-only workflow.

## Tests

Tests should target observable behavior at the boundaries: release responses, corrupt
downloads, archive traversal, isolation variables, process cancellation, instance
persistence and desktop file quoting/ownership. Use temporary roots and fake HTTP
handlers; ordinary tests must never sign in or download Minecraft.

The headless Avalonia smoke test loads and renders the actual window with sample
view models without changing the user's launcher profile. Real game validation is a
separate manual step requiring the user's entitlement, graphical session and GPU.

For opt-in tests against downloaded runtime archives, set `ORION_XODUS_ARCHIVE` and
`ORION_WINEGDK_ARCHIVE` to local `.tar.gz` files. Runtime contracts can then be verified
without installing into the user's profile.
Add `ORION_TEST_WINEBOOT=1` to also create and stop a temporary Wine prefix, or set
`ORION_SCREENSHOT_DIR` to export images from the headless UI test.
The Wine archive and prefix require several gigabytes of temporary space. If `/tmp`
is a small memory-backed filesystem, set `TMPDIR` to an existing directory on disk
before running these optional tests. Prefix checks wait for the private Wine server
to exit so registry writes are complete.

## Translations

Edit `src/Orion.Desktop/I18n/en-US.json` and `pt-BR.json`. Every visible UI label uses
the same string keys. English is the fallback. Technical messages from runtime
operations and upstream processes remain in English. The JSON/indexer design credits
JaymeFernandes' translation system; the rewritten localizer is instance-scoped.

## References and provenance

Linux shortcuts and background operation are implemented with Avalonia and .NET.
The MIT license and archived release notes retain their original attribution.
Current maintainers are yPerfectBR and raonygamer.

Runtime forks retain their own upstream authorship and licenses: see
[Xodus](https://github.com/BedrockNative/xodus) and
[WineGDK](https://github.com/BedrockNative/WineGDK), based on xodus-gaming/Xodus and
Weather-OS/WineGDK / Wine respectively. Runtime binaries are downloaded at use time,
not committed into this repository. Their archive license files are retained on extraction.
