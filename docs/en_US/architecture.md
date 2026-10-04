# Architecture

## Content and discovery

`InstanceContentService` owns GDK content discovery, validated archive imports,
transaction recovery, export and recoverable removal. `InstanceActivity` provides
an exclusive lease shared with `GameLauncher`; no content transaction can overlap
a running game. The content dialog participates in the main window's operation
lifetime and cancels/awaits its work before closing or shutting down.

`CurseForgeClient` is independent of filesystem installation. It authenticates only
the official API; a second HTTP client downloads unauthenticated CDN files and
checks metadata hashes and size. `CurseForgeViewModel` owns the independent browser
and cancellable transfer session. `CurseForgeDestinationsViewModel` snapshots target
choices before downloading and dispatches verified files to local imports or the
shared library. `ContentManagementViewModel` manages installed content only. Details and
limits: [content](content.md), [credential builds](curseforge-build.md).

`ContentArchive` and `ContentMetadata` are shared by instance imports and the
central library. `ContentLibraryService` publishes imported sources by atomic
rename, then distributes validated pack links or independent world copies.
Distribution leases all targets and records a rollback journal before mutation.
`SharedContentLinks` is a narrow leaf-link exception, not a relaxation of archive
or filesystem traversal checks. The main Content page owns its cancellable work;
shutdown waits for it. Existing local content remains accessible through the
page's per-instance section; CurseForge has a dedicated navigation entry and lifetime.

`ManualDownloadRequiredException` is the typed handoff for API distribution
restrictions. It contains refreshed file metadata and an allowlisted website URL.
`BrowserDownloadMonitor` observes one user-selected directory and copies candidate
downloads to staging while hashing. The content view supplies the native browser
and folder picker; cancellation shares the content operation's existing lifetime.

## RTX Studio

RTX follows the same dependency direction: `IRtxCatalog` is the application
contract, `RtxCatalog` owns the public BetterRTX API and generic GitHub release
resolution, and `RtxService` owns instance-scoped shader edits and launch options.
`RtxTransaction` persists rollback data before file replacement; `GameLauncher`
recovers it under the shared `InstanceActivity` lease before launching. Texture
installs reuse `InstanceContentService`. `RtxViewModel` owns cancellation and the
main RTX Studio page; shutdown awaits its work. Preview icons use the bounded
reference-counted image store with a separate provider allowlist and no credentials.
See [RTX contracts, scope and safety](rtx.md).

## Theme resources

`Orion.Desktop/Theming/ThemeManager` owns the semantic brush set and color palettes.
`VisualThemes` defines complete, paired light/dark surface sets separately from
accent palettes. `ThemeManager` composes the selected surface set and accent into
both light and dark resource dictionaries. Views reference
`Orion*Brush` dynamic resources; they do not embed theme-specific colors. Photo
overlays deliberately keep their own high-contrast scrim and white caption.
Fluent control accents are updated from the same palette, including checkboxes,
focus indicators and selection states. System mode delegates variant changes to
Avalonia's desktop theme integration. Detached windows resolve the same resources.

`ThemeSettingsViewModel` applies changes immediately. `SettingsAutoSave` coalesces
and serializes stable mode, visual-theme and accent IDs in `LauncherSettings`;
normal shutdown flushes pending settings. Missing or unknown IDs
fall back to dark/Orion/theme-recommended accent. Explicit older accent choices
remain intact. Reset is saved automatically; the persisted snapshot changes
only after settings storage succeeds. Controls expose localized accessible names.
Adding a theme or palette means adding one definition, not editing individual views.
Contrast tests cover every visual-theme/accent/mode combination for normal text,
muted text, buttons, selections, status, errors and logs. UI tests also check
compact layout at large/small sizes and keyboard selection.

`AppearanceSettings` and paired `AppearanceProfile` records contain normalized
persisted values. `AdvancedAppearanceViewModel` edits immutable snapshots; invalid
partial hex input retains the last valid preview and cannot be saved.
`ThemeClock` evaluates local schedule and solar modes. `WallpaperView` owns the
bounded, cancellable image loader and releases resources on detachment.
See [appearance options and native adaptations](appearance-options.md).

The dependency direction is `Desktop → Infrastructure → Application → Domain`.
Domain and Application do not reference Avalonia, HTTP, or Linux process APIs.

| Project | Responsibility |
| --- | --- |
| `Orion.Domain` | Instances, versions, repository/release values |
| `Orion.Application` | Use cases and contracts for storage, installation, launch, desktop integration |
| `Orion.Infrastructure` | GitHub, downloads, archives, Xodus/WineGDK, JSON storage, Linux IPC and desktop entries |
| `Orion.Desktop` | Avalonia views, view models, localization, application lifetime, composition |
| `Orion.Tests` | Contract, filesystem, process, integration and UI smoke tests |

`LauncherServices` is the composition root. Dependencies are constructor-injected;
there is no service locator in the application or domain layers.

## Releases

`IReleaseClient.GetLatestAsync(new Repository(owner, name))` resolves any repository's
latest stable release through GitHub's `/releases/latest` endpoint. It uses ETags
to revalidate metadata, checks HTTP errors and does not mistake prereleases for stable releases.

`RuntimeDefinition` describes each dependency independently of release resolution.
The runtime manager selects its binary asset, streams the download, checks the declared
size and SHA-256 digest when supplied, extracts into a temporary directory, validates
the executable layout, and atomically replaces its current-version manifest.
Archive traversal, absolute links, device files and writes through archive-created
links are rejected. Old versions stay on disk. Cancellation never promotes a partial install.
Offline use is allowed only with an already validated installed runtime; the status
message explicitly reports that the latest release could not be checked.

The currently published archives are Linux x86_64. Other architectures are rejected
instead of downloading an incompatible binary. Asset ambiguity is an error.

## Isolation and launch

Per-instance `InstanceLaunchOptions` live in the domain and are persisted with the
instance manifest. The application validates edits; the desktop editor parses
literal lines, without a shell grammar. `InstanceLaunchPlan` merges custom game
environment values into a copy of the isolated defaults and constructs an argument
vector. Login, installation, service startup and Wine prefix preparation never use
these overrides. Resolution and fullscreen preferences are persisted placeholders
for a future implementation: they do not change the executable, arguments or
environment, require no display helper, and are not validated during Play.
Native application entries continue to reference the instance ID, so
launch options are read at launch time rather than copied into desktop files.
`LaunchCommandTemplate` supports an argv-only wrapper with one final `%command%`.
The placeholder expands the entire private Xodus launch vector, preserving game
arguments; shell operators, substitution and environment expansion are not evaluated.

Every Xodus command and service receives Orion's private `XODUS_CONFIG_DIR`
and `XDG_RUNTIME_DIR`. Management/installation uses
`orion.xodus-<profile hash>.sock`. Game services use separate sockets derived
from the profile and an opaque hash of the account identity. Orion starts these
services in the background when its window opens and reuses them across launches;
only launcher shutdown, account changes, runtime replacement or a failed service
requires stopping/restarting them. WineGDK and the CLI receive the matching
`XODUS_SOCK_NAME` and `XODUS_SOCKET` for their account.
`AccountId` is an optional opaque saved-account ID. Null resolves the launcher's
current account at Play time. Xodus 0.5.0 pins that identity with
`XODUS_ACCOUNT_ID`, keeping user credentials in memory while retaining the same
profile's device identity and installed-content keys. Missing selected identities
fail closed. Services are shared only by games using the same account. Custom
environment variables cannot override these isolation settings.
Game preparation runs on a worker, including filesystem scans, RTX validation,
process output and diagnostic polling. UI progress is posted through the caller’s
`Progress<T>` context. RTX integrity, compatibility and family conflicts are checked
once per preparation; results are not cached between launches. RTX recovery,
inspection and family detection share one validated game-file listing during that
preparation. Path validation checks each ancestor with a single metadata query,
and the journal includes RTX substage timings.
The launcher never discovers or uses a system Xodus executable, profile, service,
or default socket. The upstream fork scopes keyring entries and WebView storage to
the canonical profile directory. This is account/runtime isolation, not a security sandbox.

The version catalog is fetched over HTTPS. Its official Xbox CDN package URLs are
currently HTTP-only (`assets1.xboxlive.com`, `assets2.xboxlive.com`); only these two
hosts are allowed over HTTP. After Xodus purchase verification, Orion caches encrypted
bytes with range/ETag validation and passes the local package to `install-owned` for
licensed extraction. Runtime release assets require HTTPS and checksum verification.

Each instance has its own `WINEPREFIX`. WineGDK's `wineboot -u` prepares the prefix;
WineGDK owns its bundled libraries and prefix setup. Xodus 0.2.0 or newer is required.
`install-owned` verifies a purchase over the private IPC socket before reading game
packages (remote or local), then installs and stores the acquired content key in
the isolated profile's credential backend. Subscription/trial access and unavailable
checks are not accepted. Saved accounts are confined to that same profile.
`xodus-cli run --offline-license` uses the installed license without a new purchase
or content-license query. Missing local licenses fail without a network fallback.
Game authentication can still require online Xbox services. The purchase API's
live compatibility must be validated before publishing the matching Xodus release.
No login token injection, proxy login, file-picker broker, hardcoded CIK, or DLL patch
is implemented in Orion. Xodus owns Microsoft authentication and entitlement checks.

## Desktop lifetime

`AccountService` uses Xodus's bounded JSON account commands, not direct keyring access.
Only opaque account IDs, usernames and active flags cross into the UI; stdout is kept
in memory rather than diagnostic logs. Mutations stop the launcher-owned service to
invalidate its cached identity, and are blocked while games run. Startup uses only the
installed runtime; explicit refresh can update it and request keyring unlock.

`InstanceJournal` routes operation progress and the owned service's output to one
instance file. `ProcessLog` serializes concurrent appenders without keeping an open
writer per child. `LogTail` reads a bounded snapshot, strips terminal control sequences,
and the read-only Avalonia viewer refreshes at 400 ms while open. It is not a shell.
`ShowLogOnLaunch` is persisted per instance and does not alter the game's arguments.
Creation and subsequent editing share `LaunchOptionsFields` and its view model.
Detached log windows each own a viewer/timer; a path-indexed window registry avoids
duplicate windows and closes all viewers on shutdown. Clipboard copies contain the
bounded, terminal-control-stripped displayed snapshot, not credentials from account commands.

WineGDK itself owns the opt-in `WINEBOOT_HIDE_DIALOG` flag and Xodus's inherited
descriptor-backed executable mapping. Orion does not disable display drivers or write
decrypted executables to disk to work around a runtime mismatch.

A process lock and user-restricted named pipe keep one launcher per configuration root.
`--launch UUID --background` forwards to the existing process. Only UUIDs, never
arbitrary executable paths, are accepted. `.desktop` entries use freedesktop escaping
and are synchronized after create/rename/archive and at startup. Only entries with
Orion's ownership marker are replaced or removed.

Closing the window hides it by default; the tray menu or launching Orion again reopens
it. Explicit quit asks before stopping active games or downloads. Processes are launched
with argument arrays, their output is drained concurrently, and cancellation targets
the owned process tree plus the instance-specific Wine server. Headless child supervisors
watch the owning launcher's PID and kernel start identity, stopping their worker if
that process dies. This does not rely on Linux thread-parent death signals or claim
an unrelated Xodus endpoint. Internal supervisor mode does not open a UI or IPC server.

## Storage

Instance names are display values; stable UUIDs determine filesystem paths.
JSON writes use a temporary file and rename. Installation is staged before exposing
an instance. Archival moves the complete instance directory without deleting saves.
Corrupt instance metadata is surfaced rather than silently overwritten.

`InstallationQueue` serializes installation jobs independently of UI navigation.
`downloads/<uuid>/job.json` is persisted before scheduling; completed/cancelled history
is memory-only for that session. Explicit cancellation is persisted before signalling
the worker, and cleanup happens after the worker exits. Shutdown pauses instead.
The package survives errors/interruption; extraction is rebuilt in `.install-<uuid>`.
The instance manifest is written inside staging before the atomic directory rename,
so recovery after commit is idempotent. Shared installed runtimes and worlds are never
removed by queue cleanup. Runtime staging uses stable asset IDs for crash recovery.

Future providers can implement the application contracts without changing the UI's
instance use cases. Content discovery and distribution are separate from the runtime
installation queue described above.
