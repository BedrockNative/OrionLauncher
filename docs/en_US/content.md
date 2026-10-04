# Content library and instance content

Open **Content** in the main navigation. **Shared library** imports `.mcaddon`,
`.mcpack` and `.mcworld` files. Importing adds the source to the library; then
select one destination and choose **Add to instance**. Management operations target
one instance at a time; there is no all-instances selection. CurseForge downloads
to the shared library stay in the library until you explicitly link/copy them into
an instance from Content.

Names, descriptions, icons, versions and minimum engine versions are displayed
when present. Pack name/description tokens resolve from `texts/en_US.lang` when
available. A combined `.mcaddon` shows its behavior and resource packs separately
inside one library item. Missing metadata/icons have a safe fallback. No script is
executed while inspecting a pack. Minimum engine version is informational, not a
guarantee of compatibility.

## Shared packs and independent worlds

- Packs have one source under `content-library/items/<id>/payload/`. Per-instance
  links point **only** into that source. Distribution does not copy the pack bytes.
- The destinations are `Users/Shared/games/com.mojang/behavior_packs` and
  `resource_packs` inside each instance's Wine user **AppData/Roaming** data tree.
  Nothing is linked into the installed game's internal/vanilla pack directories.
- Start each instance once so its game data location exists. Multiple ambiguous
  game data locations are rejected rather than guessed.
- **Remove instance links** unlinks only Orion-owned, validated pack links. It
  preserves library sources, other instances and local packs. The source is retained
  even when every link is removed, including for archived instances that may still
  refer to it. Existing local packs are not silently migrated into the library.
- Shared pack edits affect all linked instances. Versions are not automatically
  replaced; duplicate pack UUIDs are rejected before distribution. Already-correct
  links are left unchanged. Packs are made available, not auto-enabled in worlds.
- Worlds always receive complete, independent directory copies, never symbolic
  links. Select an in-game storage profile per target; these are not Xbox account
  IDs. Repeating distribution creates another world copy. The original template
  remains in the library.

Targets are leased against game/content activity and checked before distribution.
A journal rolls back links and partial world copies after cancellation/failure or
process interruption. Startup recovers interrupted distributions; game launch
refuses a target with an unrecovered transaction. Archive imports still reject
arbitrary symlinks; the sole exception during listing/export is a validated,
Orion-named pack leaf that resolves to its exact central library location.

## Existing local content

Instance management provides **Open instance folder** and **Open com.mojang**.
The latter opens user storage, never the game's built-in packs. If Shared packs
and signed-in profiles have separate folders, choose the desired location in the
dialog; the full path is shown. Fresh instances can prepare Shared storage, but
world-profile IDs are never invented. File-manager failures are reported in the UI.

**Installed by instance → Manage content** exposes installed worlds, addons and
textures, including files that were imported outside the shared library. You can
also open an instance's settings → **Content → Manage content**. The window follows
the launcher's visual theme. Online discovery has its own **CurseForge** page in
the main navigation, independent of any instance.

World rows use `levelname.txt`, the most recent modification timestamp from the
world metadata/LevelDB files, and `world_icon.jpeg`. Unreadable or missing thumbnails
fall back to a monochrome preview from the installed game's assets when available.
Orion does not redistribute those game assets. Worlds are sorted newest-first.

## Local files

- Worlds: import/export `.mcworld` files. Choose the destination game-storage
  profile explicitly when more than one exists. These folder IDs are not Xbox
  account IDs; Orion does not guess which account owns them.
- Addons: behavior/script packs (`.mcpack`) and combined `.mcaddon` bundles.
- Textures: resource packs (`.mcpack`).
- Archive removes an item from the game without deleting its files. Enable
  **Archived** and choose **Restore** to put it back. Archives remain inside the
  instance and consume disk space. Export gives you a portable backup.

Start the instance and sign into Minecraft once before importing. Orion manages
only that instance's WineGDK `Minecraft Bedrock[/ Preview]/Users` data, never a
system Minecraft installation. Shared packs and player-specific worlds remain
separate. Enable installed packs inside Minecraft; Orion does not edit world NBT
or automatically activate packs. World-embedded packs are preserved on export
but not managed as separate shared packs.

Close the game before managing content. Imports stage their files first, validate
paths and manifests, and commit by directory rename. Interrupted commits are
rolled back before the next content operation or game launch. Cancellation clears
the import staging area. Duplicate pack UUIDs are rejected rather than overwritten.
Archive/restore uses atomic renames; restoring never overwrites an existing pack.
Keep separate backups of valuable worlds.

Safety limits: 8 GiB extracted data, 100,000 archive entries, 40 directory levels,
bounded metadata, no archive-provided symbolic links, traversal paths, case-colliding paths, or
Windows device names. Export refuses to overwrite an existing file; choose a new
filename. These checks do not establish that third-party addon scripts are safe.

## CurseForge

Open **CurseForge** in the main navigation. Addons, maps and textures are selectable
immediately; the first visit loads popular addons automatically. **Home** clears the
search and loads popular projects for the selected type. Returning from another
page preserves the current results. Project cards show the API's logo thumbnail;
the detail view expands separately on narrow windows. Missing/invalid artwork uses
a themed placeholder. Public images use a separate, unauthenticated HTTP client
restricted to HTTPS ForgeCDN URLs with redirects disabled, a 2 MiB download limit,
dimension checks, 320px decoded thumbnails and a 256-image disk cache. Decoded images
are shared by visible views and released when hidden.

Content types remain selectable
before any network request, including in builds without a configured credential.
After choosing a file, choose **One instance only** for a local import, or
**Shared library** to store one source for later use in **Content**, without
automatically distributing it. Linking/copying from Content targets one instance
at a time. Packs use validated links; worlds always receive independent copies
and require an existing game-storage profile in the selected instance.
Changing pages does not cancel a transfer; Cancel and launcher shutdown do.

Search Minecraft **Bedrock** projects, filter by category, inspect authors and
summaries, load available versions, and choose a file explicitly. Pagination is
available for both projects and file versions. Install supports `.mcworld`,
`.mcpack`, and `.mcaddon`; other formats link back to the project's instructions.
Check advertised Minecraft versions yourself. Required dependency project IDs
are shown, but dependencies are not installed automatically.

Orion respects author distribution restrictions and does not invent download URLs
when the API withholds them. Instead, Orion opens the selected file's **CurseForge
web page in your browser**, then watches the desktop's Downloads directory (or a
different folder chosen in the waiting dialog). Complete the download on the
website yourself; Orion does not scrape pages, bypass access restrictions or send
its API credential to the browser. A blocked or missing API download URL triggers
this handoff; ordinary API errors and unavailable/deleted files do not.

The monitor scans only the chosen directory, without subfolders. It recognizes
the expected filename and browser duplicates such as `example (1).mcpack`, ignores
temporary downloads and symbolic-link files, and requires a stable size plus the
expected SHA-1 hash. A private copy is verified before import to avoid using a file
that changes between verification and extraction. The original is never moved or
deleted. Wrong-version files are left alone and the dialog continues waiting.
Changing the folder takes effect during the wait. Cancel or close the content
window to stop monitoring and discard only Orion's temporary copy. If the browser
cannot open, the page address can be copied and monitoring still works. Arbitrary
renamed downloads should be renamed back to the expected filename or imported
through the local-file flow. Without an API hash, automatic verification is refused.

Allowed direct downloads go to an unauthenticated ForgeCDN client,
with allowlisted HTTPS hosts, redirect validation, exact size and SHA-1 checks.
SHA-1 here verifies agreement with the API metadata, not author authenticity or
malware safety. Successful downloads pass through the same local import service.
Cancel removes the partial download; content downloads currently restart on retry
(the separate instance-installation queue retains its resumable behavior).

Local builds without a credential keep the local manager available and explain
that CurseForge is unconfigured. See [build credentials](curseforge-build.md).

Manifest interpretation follows the [official Minecraft manifest reference](https://learn.microsoft.com/en-us/minecraft/creator/reference/content/addonsreference/packmanifest?view=minecraft-bedrock-stable).
API integration follows the [CurseForge REST API](https://docs.curseforge.com/rest-api/).
The browser-handoff interaction is inspired by the
[Prism Launcher blocked-download flow](https://prismlauncher.org/news/release-6.0/);
the implementation is native to Orion, without copied Prism code.
