# Setup and usage

## Requirements

- Linux x86_64 desktop session with `XDG_RUNTIME_DIR`.
- .NET SDK 10 for development; published self-contained builds include the runtime.
- Vulkan-capable GPU and working Vulkan drivers for WineGDK's graphics components.
- Xodus's system libraries, including GTK 3, WebKitGTK 4.1, OpenSSL, and Secret Service
  where required by the published build. Xodus requests creation or unlocking of
  the default keyring through the desktop's native password dialog during login.
  Starting the Xodus service also requests that dialog if the keyring is locked;
  enter the keyring password there to continue. Orion waits for the prompt instead
  of terminating the service after a few seconds. Cancelling it aborts startup.
- WineGDK's native dependencies; use your distribution's Wine dependency packages if
  the runtime reports missing libraries. Orion does not modify system packages.
- A Microsoft account that purchased Minecraft for Windows. Subscription/trial access
  is not accepted for installation. Xodus 0.2.0 or newer verifies the purchase through
  Orion's private socket before downloading or decrypting a package.

Actual binary requirements may change with upstream releases. Loader errors are
shown in the UI; inspect `ldd` on the downloaded runtime if a library is missing.

## First launch

1. Run `dotnet run --project src/Orion.Desktop` from the repository.
2. Open **Account → Add Microsoft account**. Complete any native keyring prompt,
   then finish the Xodus login window. No manual Seahorse setup is required when
   a working Secret Service provider is installed.
3. Open **Instances → New instance**. Give it a name and select a release or preview.
4. Select **Install instance**. The dialog closes and **Downloads** shows progress;
   you can keep using the launcher. Once completed, press **Play** in the library.
   The first launch also installs WineGDK and prepares
   the instance's private Wine prefix.

The catalog comes from
[minecraft-windows-gdk-version-db](https://github.com/LukasPAH/minecraft-windows-gdk-version-db).
The new-instance screen uses the catalog only; manual package import is not shown.
Legacy UWP `.appx` packages are not supported.

Downloads and games can continue while the main window is hidden. Use the tray menu,
or launch Orion again, to restore it. Some desktops (notably GNOME without an indicator
extension) do not display tray icons; reopening Orion still works, or disable background
mode in Settings. **Quit Orion** in the tray menu exits fully and asks before stopping active work.

## Appearance

Open **Settings → Appearance**. The three independent choices are:

- **Display mode:** Dark, Light or Follow system.
- **Visual theme:** Orion (cool blue), Graphite (neutral), Grove (forest), or Dune
  (warm earth). Changes backgrounds, panels, input fields, menus and log surfaces.
  The miniature previews show each theme's dark and light surfaces side by side.
- **Accent color:** Match theme uses its recommended accent; Mint, Blue, Lavender
  and Amber override only the highlights, without changing your background theme.

Launcher settings apply immediately and save automatically. Rapid edits are
coalesced into serialized writes; shutdown flushes the latest valid choice.
**Restore Orion appearance** restores and saves dark/Orion/matching accent.
Incomplete custom color input is not saved until it is valid `#RRGGBB`.
Older settings keep explicit saved accent colors; missing visual-theme
IDs default to Orion, and unknown accents fall back to Match theme.
Follow system uses the preference reported by the desktop; availability depends
on the Linux desktop's integration. Instance settings and logs use the same theme.

## Downloads and recovery

The Downloads page keeps the current session's recent jobs, including completed,
failed and cancelled installations. Installations are queued sequentially; navigating
elsewhere does not stop them. Failed jobs offer **Retry** and **Cancel**.

Pending jobs and encrypted package bytes live under `downloads/<uuid>/`. On startup,
unfinished jobs are recovered automatically. HTTP byte ranges use a strong ETag and
`If-Range` to avoid combining different files. If the server cannot safely resume or
returns a replacement file, downloading restarts. A partial extraction is rebuilt
from the retained package, without downloading that package again. Allow space for
both the encrypted package and its extracted installation.

**Cancel** records the cancellation, stops the owned worker, then removes that job's
package, metadata and extraction staging. Cancelling during cleanup is also recovered
after a crash. Committed instances, worlds and shared installed runtimes are never
deleted; diagnostic logs are retained. A completion racing with cancellation wins
once the installation has committed. Closing Orion fully pauses unfinished work;
it does not discard it. Completed history does not carry into the next session.

The Xodus ownership check is required before any game download, including resume;
`install-owned` checks again before extraction. Play still uses the retained license.
Orion's headless child supervisor stops its installation worker and private service
if the owning launcher dies, without claiming or killing another launcher's socket.

## Accounts

The bottom of the sidebar always shows the launcher's current Xbox profile. Its
gamer picture and gamertag stay visible on every page; click it to manage accounts.
This is the launcher's current identity, not an account pinned to an individual game.
Profile lookup requires Xodus 0.6.0 or later; use **Account → Refresh accounts**
to update an older runtime. Metadata refreshes in the background after account changes.
Only public presentation data is cached, with a 64-pixel thumbnail for offline use.
If lookup is unavailable, Orion retains cached metadata for that same account or
shows its saved account name and an initial. No profile photo is carried across sign-out
or account changes, and no Microsoft tokens are sent to the image server.

The Account page shows each saved identity's Xbox gamertag and picture, with its
Microsoft account name below, and marks the active Xbox account. The same metadata
appears in the instance account picker during creation and editing. The follow-current
option previews the active profile while continuing to resolve it at launch time.
These views share one thumbnail per account. Background profile queries are serialized
and never select an account just to retrieve its metadata; cached profiles work offline.
Add another account, select **Use this account**, or remove one with confirmation.
Removing the active account selects the next saved account; removing the last account
signs out. Installed instances, worlds and locally saved installation licenses remain.
Changes are blocked while a game or installation is running so identity cannot change mid-operation.

Account management requires Xodus 0.4.0 or later. **Refresh accounts** updates Xodus
and may request the desktop's native keyring unlock dialog. Startup reads only the
installed runtime and does not download an update just to display an account. An
unavailable/locked store is shown as unknown, not as a successful sign-out.

Purchase verification still checks all saved accounts before installation, independent
of which account is active. Play uses the local installation license without repeating
the purchase check. Displaying a saved account is not an online token-validity check;
Microsoft may require signing in again when credentials expire.

Choose **Game account** when creating an instance or later in **Launch options**.
The default, **Use the launcher's current account**, resolves the active account
each time you press Play. Selecting a specific account pins only that game's
session, without changing the launcher's current account or other games.
If that saved account is removed, choose another explicitly; Orion does not
silently use a different identity. Per-game sessions require Xodus 0.5.0 or newer.

## About and changelogs

Build version, platform and license information are shown in **About**, alongside
the **Changelogs** button. The viewer includes current release notes and archived
release notes, bundled with the application for offline use. Historical notes are
marked clearly: they describe their respective versions, not necessarily the current feature set.

## Instance logs

Use **••• → Instance log** to open the integrated read-only console. Enable **Open the
instance log when playing** in **Launch options → Arguments** to open it automatically.
Launch failures also reveal the console. **Follow output** controls automatic scrolling;
text can be selected and copied using normal keyboard shortcuts. Closing the console
does not stop the game.
**Copy logs** copies the recent output displayed in that console. **↗** moves it
to a separate, resizable window. Open another instance's log and detach it to
watch multiple games independently. Each window has its own copy and follow
controls; closing it only closes the viewer. Quitting Orion closes all viewers.
The trash button **Clear log** erases this instance's journal after confirmation,
including in detached windows. **Settings → General → Clear all journals** clears
the launcher's top-level `.log` files (instances, historical journals and services).
This cannot be undone. Files are truncated under the same lock as appenders;
running sessions keep logging. Game data, original content logs and crash dumps are
not deleted, and linked paths are rejected.
Drag the small handle above the log to change its height. It may cover lower instance
rows, but its upper edge is constrained below the first row. The handle also adjusts
when resizing the window; lower cards remain reachable by scrolling.

Installation, prefix preparation, game output and Xodus service diagnostics emitted
during the operation are appended to `logs/instance-<uuid>.log`. The console displays
only the last 128 KiB to keep memory bounded; the complete history remains on disk.
Each running game owns its service, so its journal contains only that session's
service diagnostics, marked as **Xodus service**. Authentication command logs
remain separate and are not copied into instance journals. Review/redact logs before
sharing: upstream tools can include paths or account-related information.

Before play, Orion configures Wine's crash debugger in the **instance's private prefix**
to send its backtrace to the journal instead of a temporary graphical crash dialog.
Unhandled Wine exceptions are reported even if the launch wrapper returns exit code zero.
Game logging defaults keep Wine errors and route DXVK/VKD3D output to the same journal;
explicit environment overrides in Launch options still take precedence.

Orion enables Minecraft's file-based content log in existing storage profiles without
enabling the in-game overlay. New profiles receive this setting on their next launch.
New output from the game's content logs and `NonAssertErrorLog.txt` is collected every
two seconds and flushed when the session ends. Historical content output is not replayed.
Supplemental collection is limited to 8 MiB per session; original files are preserved.
Crash markers and memory dumps in known crash directories are referenced by location,
not pasted into the journal: they can contain private identifiers or memory, and a
`.crashedsession` marker alone is not a stack trace or proof of a particular crash cause.
Only known locations inside the instance are read; symbolic links are not followed.
The GDK content-log location is documented by [Microsoft](https://learn.microsoft.com/en-us/minecraft/creator/documents/gdkpcprojectfolder?view=minecraft-bedrock-stable).

Historical `game-<uuid>.log` and `install-<uuid>.log` files are preserved, not deleted.
New operations use the unified journal. With WineGDK 11.18-2-gdkcomponents or newer,
Orion sets `WINEBOOT_HIDE_DIALOG=1`: prefix setup continues normally, with progress in
Orion and diagnostics in the journal, but without the external wait window.

## Per-instance launch options

The **•••** button opens one settings screen with a sidebar: **General**, **Launch**,
**Environment**, **Resolution**, **Appearance**, and **Management**. Rename in
General and apply edits with **Save**; **Cancel** discards pending edits.
Management provides the instance folder, a separate live-log window, and confirmed,
recoverable archival. Logs and files remain accessible while the game runs; stop
the game to edit settings or archive it.

### Optional application shortcut

**General → Show in the application menu** is off by default and applies only to
that instance. You can enable it during creation or in its settings. Turning it
off removes the Linux `.desktop` entry, not the game, prefix, or worlds.
Old instances also default to off: the previous global automatic-shortcut setting
is no longer used. At startup Orion removes obsolete entries marked as its own,
leaving unrelated entries and symbolic links untouched. Re-enable individual
shortcuts in their instance settings when wanted.

### Optional covers

Choose **Appearance** during creation, or **••• → Appearance** on an existing
instance. **Color and initial** is the default and does not download or decode
cover images. Three optional Minecraft scenes have a live preview; selecting one
contacts Wikimedia's image host and caches a 320-pixel thumbnail. After caching,
the cover works offline. An unavailable image falls back to the color/initial card
and never blocks creating or playing the instance.

Only displayed covers keep decoded bitmaps; cards with the same cover share one
bitmap. Hiding the library, closing the editor, or choosing the minimal option
releases unused image memory. Thumbnail files stay in the cache for reuse and
contain no game/account data. Source, author and license links are in the selector.
See [image attribution](instance-covers.md).

### Launch settings

Open an instance's **••• → Launch** section while it is stopped. Options are
saved in its `instance.json`. Arguments and environment overrides also apply when
launching from a Linux app shortcut; display preferences are currently inactive.
Old instances keep their existing behavior until options are saved.
The same editor is available during creation; its settings are saved with the
background installation job and survive interruption/resume.

- **Launch command:** `%command%` keeps the default launch. `prime-run %command%`
  wraps the existing command for systems with `prime-run` installed, retaining all
  game arguments, private socket/profile settings and environment overrides. This
  setting does not install NVIDIA software or change system GPU settings. Wrapper
  paths/arguments can be quoted; the single `%command%` must be the final token.
  No shell pipelines, redirections, variable expansion or command substitution occur.
- **Arguments / flags:** one literal argument per line, without enclosing quotes.
  A flag and its separate value occupy two lines; a value containing spaces stays
  on one line. Blank lines are ignored. No shell commands, `$VARIABLE` expansion, or command substitutions
  are evaluated. Only flags supported by your Minecraft version have an effect.
  Requires Xodus 0.3.0 or newer; an older runtime reports an error instead of silently
  ignoring arguments.
- **Environment:** one `NAME=value` per line, for example `WINEDEBUG=-all`.
  The UI also shows NVIDIA offload examples (`__NV_PRIME_RENDER_OFFLOAD=1` and
  `__GLX_VENDOR_LIBRARY_NAME=nvidia`); placeholders are not applied automatically.
  Empty values are supported. Values may contain spaces or additional `=` signs.
  These overrides apply to the game launch,
  not the launcher, login, background service, or prefix preparation. Account,
  socket and prefix variables remain managed by Orion. This is not a security
  sandbox. Values are stored as ordinary instance settings: do not put secrets here.
- **Resolution / fullscreen (coming soon):** these controls are placeholders.
  Their values are saved for a future implementation but do not affect game launch,
  screen resolution, fullscreen mode, or environment variables. No additional
  display tool is required or launched. Use Minecraft's own video settings for now.

**Reset defaults → Save** clears these settings. **Cancel** discards edits.

## Paths

| Content | Default location |
| --- | --- |
| Instances and Wine prefixes | `~/.local/share/orion-launcher/instances/<uuid>/` |
| Archived instances | `~/.local/share/orion-launcher/archives/` |
| Runtime versions | `~/.local/share/orion-launcher/runtimes/` |
| Logs | `~/.local/share/orion-launcher/logs/` |
| Settings | `~/.config/orion-launcher/settings.json` |
| Isolated Xodus profile | `~/.config/orion-launcher/xodus/` |
| Metadata cache | `~/.cache/orion-launcher/` |
| Application entries | `~/.local/share/applications/io.bedrocknative.orion.<uuid>.desktop` |
| Service socket | `$XDG_RUNTIME_DIR/orion.xodus-<profile hash>.sock` |

The corresponding `XDG_DATA_HOME`, `XDG_CONFIG_HOME`, and `XDG_CACHE_HOME` overrides are
respected. Keep the Xodus profile at a stable location: its canonical path scopes the
credential backend. Existing system Xodus credentials are not imported.

## Recovery

Archiving preserves the game, prefix and worlds. With Orion closed, move the archived
folder back into `instances/`, naming it with the UUID from its `instance.json` without
hyphens. Restart Orion to rebuild the application entry. Do not overwrite another instance.
Worlds live in the instance's Wine prefix; back up the whole instance before manual edits.

Runtime versions are retained under numeric GitHub asset IDs. `current.json` records
the selected version. A network failure can use an installed, valid runtime; first-time
setup needs network access. Cancelled runtime installs do not replace the previous version.
An interrupted process may leave an `.install-*` directory; these are never considered
installed instances or runtimes and can be removed while Orion is closed.

To transfer worlds from a backup, create an instance from the catalog and use
the content manager's world import workflow.

## Troubleshooting

- Open **••• → Instance log**, or **Settings → Open logs** for files. Preserve the relevant error and redact account details
  before sharing logs; upstream tools control their own output.
- GitHub rate limiting is reported explicitly. Retry after the limit resets.
- If a keyring prompt is cancelled, retry **Sign in** to open it again. Xodus never
  falls back to plaintext storage. If no Secret Service provider is installed,
  install/enable GNOME Keyring or a compatible KWallet through your distribution's
  software manager and restart the desktop session if required. Do not run Orion
  or Xodus as root. Missing system packages are not installed automatically.
- If login fails, check Xodus/WebKit native dependencies and the account log.
- If extraction reports success but the package is incomplete, Orion rejects it;
  see `instance-<uuid>.log`.
- If launching fails, include the Xodus/WineGDK release tags, game version and game log.
  Upstream Microsoft services and game compatibility still require real-account testing.
- If the development checkout or published executable moves, reopen Orion to refresh
  application entries with the current executable path.
