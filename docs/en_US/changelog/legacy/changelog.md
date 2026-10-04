# OrionBE Launcher - Changelog

## Unreleased

### Changed

* **Simpler runtime:** the launcher now always uses the latest [raonygamer/gdk-proton](https://github.com/raonygamer/gdk-proton) release (WineGDK, vkd3d and combase fixes included). Removed our WineGDK overlay download and the combase binary patcher. Newer releases are checked at most every 6 hours on Play (offline uses the installed one), instances are refreshed to the new paths, and older versions are deleted.

### Added

* **Xodus on demand:** `xodus-service` (unmodified upstream) is now offered for download and build when the system has none; administrator permission (`pkexec`) is requested only for build packages, and Rust ≥ 1.98 is installed via rustup in user space if needed.

### Added (content & RTX)

* **Content library:** import `.mcworld`, `.mctemplate`, `.mcpack`, `.mcaddon` and `.zip` once; new Textures, Addons and Worlds pages. Packs are applied to instances by symbolic link (no duplicates); worlds are copied, asking before replacing a world with the same folder name.
* **CurseForge:** the Bedrock site is embedded in the launcher (WebKitGTK) and downloads go straight to the library; no API key needed (an API mode remains optional). Without WebKitGTK it falls back to the default browser plus a Downloads-folder watcher.
* **RTX page (NVIDIA only):** Vanilla RTX pack install/update, Tuner, BetterRTX presets, LUT manager and DLSS swapper, ported from the Vanilla RTX App (GPLv3; the RTX Reactor/Alchitex module is not included). `OrionBE.Rtx` is GPLv3 — see `OrionBE.Rtx/NOTICE.md`.
* **Instance settings:** open the real `com.mojang` (worlds and Shared packs) inside the Wine prefix.

### Fixed

* Export still hung after the FileSavePicker was added: the game queries the picker for `IInitializeWithWindow` (`Initialize(HWND)`), which was missing and became an exception. Implemented.
* Exporting (structures, worlds...) hung on an endless loading screen: the game uses the UWP `Windows.Storage.Pickers.FileSavePicker`, which Wine lacks. It is now implemented in the WineGDK picker DLL (save dialog through the launcher broker; the chosen file is created and returned as a `StorageFile`).
* After choosing a skin the game still crashed: it wraps the picked path with `Windows.Storage.StorageFile`, which Wine lacks (`CLASS_E_CLASSNOTAVAILABLE` → fail-fast). Our WineGDK picker DLL now provides a minimal read-only `StorageFile` (path, name, type; other members log a `FIXME` with their name), registered in the prefix by the launcher.
* The game no longer crashes when it opens a file picker (e.g. Dressing Room → local skin). WineGDK now implements `Microsoft.Windows.Storage.Pickers`; the dialog is opened by the launcher on your real Linux files (not the Wine `C:` drive) with a configurable backend (Automatic, KDialog, Zenity, Portal or a custom command) under Settings → In-game file picker. For the Dolphin/KDE look install `kdialog`.
* The game crashed when Xodus took more than 5 s to return the Microsoft token (slow DNS): timeout raised to 60 s and a NULL dereference fixed.

* Downloads page could not be reopened and several pages leaked event handlers (router disposes page view models).
* The multiplayer safety-warning `options.txt` patch now targets the files the game really reads (inside the Wine prefix).

* Realms: XSTS token now uses the `https://pocket.realms.minecraft.net/` relying party.
* Profile Overview: achievements/userstats/titlehub are fetched without Android title claims.

### Removed

* **ProxyPass:** the built-in ProxyPass relay (and its vendored RakNet library, Microsoft device-code login service, sidebar tab, settings, and terminal log panel) was removed. Native Xbox login via WineGDK is the supported path.

---

## 0.5.0

**Primary focus:** **ProxyPass** is now built into OrionBE, so a signed-in Microsoft/Xbox account can join supported online Bedrock servers through the local proxy.

### Added

* **Built-in ProxyPass:** clean-room, in-process Bedrock proxy with Microsoft device-code login, persisted credentials, destination selection, and online server forwarding.
* **Modern Bedrock protocol support:** updated codec and encrypted transport handling for Bedrock **1.26.42** (protocol `2168`), including RakNet reliability/fragment handling.
* **Account profile sync:** the Xbox gamertag and avatar are applied to OrionBE's local profile after sign-in.
* **ProxyPass terminal:** optional, copyable in-app log panel for connection and authentication diagnostics. Keep it off when it is not needed to avoid UI log buffering.
* **In-app news:** `v0.5.0` release notes under `Updates/<locale>/v0.5.0.md`.

### Changed

* ProxyPass now listens on the conventional local Bedrock endpoint, `127.0.0.1:19132`; legacy temporary ports are migrated automatically.
* AppImage builds accept `APPIMAGE_VERSION` so release artifacts use a stable versioned filename.

### Removed

* The Java/Kas-tle ProxyPass subprocess and its downloaded JAR dependency. The relay now ships as part of OrionBE.

---

## 0.4.2

**Primary focus:** reliable **Bedrock version catalog** loading on Linux and clearer **Add instance** errors when the network fails.

### Added

* **Add instance — version load UX:** indeterminate progress while fetching versions; error panel with **Retry**, **View error**, and **Copy error** (copyable report includes catalog URL, cache path, and exception chain).
* **Timeout hint** in the copyable error report when the failure looks like a connection timeout (IPv6 / proxy guidance).
* **Linux curl fallback** for the Bedrock version JSON when `HttpClient` fails with transient network errors.
* **IPv4-first HTTP handler** with **30s connect timeout** for named launcher `HttpClient`s (`DownloadService`, `GitHub`).
* **In-app news:** `v0.4.2` release notes under `Updates/<locale>/v0.4.2.md` (listed in `versions.json`).

### Fixed

* **Empty version combo on Add instance:** loading and error states replace a silent failure; version list updates on the UI thread.
* **Hub thread violation:** `RefreshPrimaryButtonUi` and post-navigation Hub refresh no longer touch Avalonia controls from a background thread (`InvalidOperationException: Call from invalid thread`).

### Changed

* **Bedrock catalog service:** cache write is best-effort after a successful fetch; clearer logging when fetch fails with no usable cache; rejects empty remote catalogs explicitly.

### Documentation

* README **Troubleshooting** — Bedrock catalog, `curl` test, `DOTNET_SYSTEM_NET_DISABLEIPV6=1`, and `~/OrionBE/cache` permissions.

---

## 0.4.1

**Primary focus:** safer and clearer **Bedrock instance upgrades** (progress, blocked actions, player-data backup).

### Added

* **Instance settings — upgrade progress:** progress bar and step status while updating Bedrock to the latest build in the same channel.
* **Player data backup on upgrade:** copies `game/Minecraft Bedrock/Users/` to `instances/{name}/.upgrade_users_backup` before wiping `game/`, restores after extraction; on failure after wipe, attempts recovery from the backup folder.
* **In-app news:** `v0.4.1` release notes under `Updates/<locale>/v0.4.1.md` (listed in `versions.json`).

### Changed

* **During Bedrock upgrade:** Back, delete instance, mod import, LeviLamina save, and mod toggles are disabled until the operation completes.
* **After a successful upgrade:** OK on the success dialog navigates back to the **Instances** list (updated version on the card, ready to Play) instead of remaining on Instance settings.
* Localized UI strings for backup/restore steps (`launcher.instance_settings.upgrade_status_*`).

### Fixed

* **Launcher closing after upgrade:** post-update flow no longer reloads Instance settings after the success dialog (which could conflict with navigation/disposal); returns to the instance list via `GoBack()`.

### Technical

* `BedrockGameLayout.ResolveUsersDirectory`, `OrionPaths.InstanceUpgradeUsersBackup`.
* `InstallationService.UpgradeInstanceToLatestEligibleAsync` orchestrates backup, deploy, restore, and error recovery.
* Message dialog OK button marked `IsDefault` for more predictable Avalonia modal behavior.

---

## 0.4.0

**Primary focus:** reformulated **visual interface** (sidebar, hub hero, profile).

### Added

* **Multi-language support:** runtime UI language with JSON catalogs (**English** `en-US`, **Portuguese Brazil** `pt-BR`), loaded from `I18n/*.json` next to the executable (with embedded fallback); strings cover shell, launcher views, dialogs, and Hub news paths per locale.
* **Hub:** wallpaper hero with **OrionBE logo** centered at the top (`Assets/logo.png`).
* **GitHub shortcut:** toolbar button (top-right) opens the project repository at `https://github.com/OrionBedrock/OrionLauncher`.
* **Existing instances — Bedrock updates:** from **Instance settings**, update an already-installed instance to the **latest build in the same channel** (`release` vs `preview`) when the catalog offers a newer package; respects channel boundaries and refreshes game files (mods re-applied when mods are enabled).
* **Local profile:** nickname, optional tagline under the nickname (length limits in the editor), and **custom avatar** stored under `~/OrionBE/cache`; dedicated **Profile** screen (pick/remove photo, save, back).
* **In-app news:** `v0.4.0` release notes under `Updates/<locale>/v0.4.0.md` (listed in `versions.json`).

### Removed

* Microsoft / Xbox **MSAL** sign-in and related OAuth-only launcher UI.

### Changed

* Sidebar and profile avatars use **Avalonia `Bitmap`** loaded from disk with **`Stretch.Uniform`** inside rounded clips for consistent rendering on Linux.

### Notes

* **Translation catalogs:** `OrionBE.Launcher/I18n/en-US.json` and `pt-BR.json` are kept **key-aligned** (same flattened keys); files are **embedded** in `OrionBE.Launcher` and **copied** to output, and **linked** into the host app (`OrionBe.csproj`) next to the executable for runtime loads.
* **Migration (temporary):** the repository is undergoing a **gradual** migration of **file structure** and **code layout**. Some parts may still feel inconsistent until that work finishes—not a reflection of the final architecture.

---

## 0.3.3

### Added
- **Add instance:** toggle to keep the add-instance screen open after a **successful** installation so you can read the full log before going back (default remains “return home when installation succeeds”).
- **Instance settings → Bedrock update:** checks the catalog for a **strictly newer** Bedrock build in the **same channel** (`release` vs `preview`). Button **Update to latest in this channel** is only enabled when an upgrade exists; the section uses reduced opacity when no update is available. Stable builds never jump to preview (and vice versa); older versions are never offered.
- **`IBedrockVersionCatalogService.TryGetLatestUpgradeInSameChannelAsync`** and **`IInstallationService.UpgradeInstanceToLatestEligibleAsync`** to refresh game files from the `.msixvc` pipeline (mods re-copied into `game/mods` when mods are enabled).

### Changed
- Linux (Proton/umu): set `SteamAppId` / `SteamGameId` / `STEAM_COMPAT_APP_ID` to a conventional non-Steam placeholder (`480`, Spacewar) and, when `/usr/bin/env` exists, invoke **`env SteamAppId=… SteamGameId=… STEAM_COMPAT_APP_ID=… umu-run …`** so wrappers that drop inherited env still pass a numeric Steam app id where supported.
- **Removed** experimental per-instance Linux compatibility options from **Add instance** (GNOME compatibility profile, X11 fallback, launch diagnostics). Remaining focus/minimize, workspace, or compositor issues on some desktops are **likely limitations or bugs in Proton/Wine** rather than something the launcher can fully paper over; we may revisit mitigations in future releases as upstream improves.
- If `umu-run` exits with a non-zero exit code, the launcher shows an error dialog instead of failing silently.
- User-facing installation logs, online-bootstrap messages, instance settings UI, and related developer comments are now consistently in English.
- Instance cards show **Running…** and disable Play while that instance’s Bedrock process is detected (polls until exit); Settings stays available. Windows detects `Minecraft.Windows.exe` under the instance game folder; Linux keeps `/proc` scanning as before.

### Fixed
- **Play** no longer triggers Avalonia **“Call from invalid thread”** after launch: UI state updates (`IsLaunching` / `IsGameRunning`) are marshalled back to the UI thread after `ConfigureAwait(false)` on the game launch await.

## 0.3.2

### Added
- Added Bedrock online bootstrap during instance installation:
  - downloads and places `ca-bundle.crt` in `etc/ssl/certs`
  - downloads a compatible libcurl package and deploys it as `Content/Xcurl.dll`
- Added deterministic Bedrock executable discovery, prioritizing `Content/Minecraft.Windows.exe`.
- Added automatic copy of `SystemFiles/system32/combase.dll` into the game executable directory at instance creation time.
- Added automatic `options.txt` patching on launch to enforce:
  - `do_not_show_multiplayer_online_safety_warning:1`
- Added first-run dependency verification:
  - runs once on first launcher startup
  - checks required runtime commands/assets used by install/launch flows
  - records a marker file after execution

### Changed
- Improved Linux launch safety:
  - blocks duplicate launch attempts for the same instance while launch is in progress
  - prevents launching an instance that is already running
- Added launch-state UI feedback:
  - Play button now shows `Launching...`
  - launch controls are disabled while launch is being started
- Improved Linux runtime setup for newer Bedrock builds by attempting `GameInputRedist.msi` installation in the Wine prefix.
- Added startup dependency warning UI:
  - shows a dependency report dialog when missing items are detected on first run
  - keeps startup non-blocking even if the check fails unexpectedly

### Technical
- Added `Tmds.DBus.Protocol` explicit dependency override to a fixed secure version.
- Added `SystemFiles/**` to launcher output copy rules for runtime availability.
- Added `IStartupDependencyCheckService` with first-launch marker persistence under `~/OrionBE`.
