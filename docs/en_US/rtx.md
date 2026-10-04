# RTX Studio

Open **RTX Studio** in the main navigation. The catalog loads on first visit;
search filters it locally. Select an instance at the top before installing.
Browsing itself needs no NVIDIA-only hardware check or API key.

Both systems display an experimental-feature notice, including limited community
updates/documentation for newer game versions. Report bugs or suggest improvements
to **yPerfectBR** on Discord or use the notice's GitHub issue button.

## Separate RTX systems

Choose **BetterRTX** or **Vanilla RTX** before browsing or configuring. BetterRTX
owns shader presets, the shader editor, material import/export and shader recovery.
Vanilla RTX owns the official resource-pack catalog and optional DLSS replacement.
Each system has a separate launch-preference file under `rtx/betterrtx/` or
`rtx/vanillartx/`. Legacy preferences are adopted by BetterRTX when custom shaders
exist, otherwise by Vanilla RTX; they are not copied to both systems.

Orion deliberately permits only one of these systems per instance. This is a launcher
policy, not a claim that their combination caused a particular crash. Checks apply
to Studio, local/CurseForge imports, shared-library distribution, archive restoration,
worlds containing official Vanilla RTX packs, and game launch. Recognition uses the
three official pack UUIDs, not names; modified third-party UUIDs cannot be reliably
identified. Unrelated PBR packs remain available for BetterRTX.

Existing mixed installations are reported, never silently deleted. To switch away
from BetterRTX, restore original shaders and **Reset this system's preferences**.
To switch away from Vanilla RTX, archive or unlink its packs (including worlds with
embedded packs), restore any replaced DLSS DLL, and reset its preferences. Archives
and shared-library sources are preserved but cannot be restored/linked into a
conflicting instance. Use separate instances to retain both active setups.

The compatibility notice calls out the Minecraft 26.30 texture-atlas change reported
by BetterRTX's maintainers. It does not establish the current compatibility of every
preset or confirm a crash cause. A catalog version label is not a rendering test;
use presets rebuilt for the installed game, or restore original Minecraft shaders.

## Shaders

- Browse BetterRTX presets with upstream preview icons, target game version and
  release. Refresh fetches a new catalog; network failures can use the last valid
  cached catalog, clearly labelled offline.
- Select a card, review the version warning and confirm installation. Orion now
  compares all numeric target-version components. A newer game requires checking
  **I understand the risks** in the preset details before installing/reinstalling.
  The box starts unchecked and resets when changing presets or instances,
  and after an installation attempt. The installation receipt
  retains consent only for that installed preset and exact instance version;
  changing the game version requires fresh acknowledgement and reinstallation.
  Older and unknown targets remain blocked. Local archives still require an
  exact target match; the checkbox applies to reviewed catalog/editor presets.
  The backend rereads the saved instance version before publishing and checks
  installed presets at launch too. Recovery remains available. Matching labels still do not prove
  correct shader rebuilding, runtime compatibility or successful rendering.
- The selected installed preset shows **Installed** and **Reinstall preset**.
  Receipts now retain the catalog UUID; earlier receipts are recognized by their
  name and target version. Switching instances clears stale state before rereading it.
- A local `.rtpack` requires target metadata (`orion-preset.json`, string
  `gameVersion`). Orion exports preserve the installed preset's target, rather
  than relabelling it as the current instance version. Untargeted archives, raw
  `.material.bin` imports and compiler outputs without a target are blocked.
  Target metadata is a declaration, not publisher authentication or a rendering
  test. No code or scripts from archives are executed.
- **Configuration → Verify installed files** checks managed index redirects,
  SHA-256 hashes and the existence of stock fallbacks. External changes are reported
  and block replacement and launch rather than being silently overwritten.
- **Configuration → Restore original shaders** restores the original material
  paths when the index and stock fallbacks are unambiguous. It keeps textures,
  worlds and launch preferences. Damaged or externally changed managed shaders are
  preserved in `rtx/quarantine`, not deleted. A missing/corrupt receipt is never
  treated as proof of a vanilla setup: the actual index is inspected. Foreign
  redirects must be restored by their original manager.

The integration follows the public [BetterRTX installation contract](https://bedrock.graphics/#setup):
compiled shaders live in a separate subfolder and `materials.index.json` selects
them. Orion uses a uniquely named `orion-rtx-<id>` folder for each installation;
the original `.material.bin` files are never overwritten. Index changes preserve
unrelated entries and fields, though JSON formatting is normalized.

## Textures

The **Vanilla RTX → RTX textures** tab resolves the latest stable
[Cubeir/Vanilla-RTX release](https://github.com/Cubeir/Vanilla-RTX/releases/latest)
through the launcher's generic GitHub release client. Vanilla RTX, Normals and Opus
install into the selected instance's user resource-pack storage, not built-in game
packs. Enable the pack inside Minecraft. Manage/archive it from **Content**.
An existing pack UUID is not overwritten: archive it before installing its replacement.
The three variants are recognized by their official manifest header UUIDs, including
older installs and shared library links, regardless of the pack's display name.
Installed cards show **Installed** and the local version instead of offering another
download. Archived packs are not considered installed. Refresh rereads disk state;
replacing/updating a pack remains an explicit Content archive/import operation, and
shared packs must be managed from the shared library. The installed indicator does
not claim that a pack is active in Minecraft or is the latest upstream version.
These are per-instance installs; this screen does not activate global resource packs
or modify world databases. Studio inspects the downloaded pack's actual
`min_engine_version` before installation, not its release/pack version. Bedrock's
`1.26.40` engine notation is compared with game `26.40`. A newer or unknown minimum
requires explicit risk acknowledgement; refusing leaves the instance untouched.
Changing the instance version while that dialog is open requires a fresh review.
This requirement prompt belongs to the Studio download flow, not generic Content
imports. Acknowledgement does not force Minecraft to accept an unsupported pack.

## Compatibility evidence (reviewed 2026-10-04)

The user-supplied announcement of 2026-06-16 reports Minecraft 26.30's atlas change
breaking existing BetterRTX presets. This is a historical warning, not proof of
permanent incompatibility of all subsequent presets:

- The official presets repository [updated multiple binary links on 2026-06-20](https://github.com/BetterRTX/presets/commit/6861c77e0c06e6250707d3d7c642f50952811150)
  and [corrected heightmap presets on 2026-06-22](https://github.com/BetterRTX/presets/commit/fe3ef33d8adb2821b07ca7dd007136b752a7ca0f).
  These are metadata/link changes; they alone do not establish that all binaries
  were rebuilt correctly. Targets vary between presets, and API/repository
  metadata are not a blanket compatibility certificate.
- [Prizma's source metadata](https://github.com/BetterRTX/presets/blob/main/data/9825590f-7f4e-4592-b000-8174843da724/README.md)
  still declares `brtxVersion: 1.3`. Its three published material URLs target
  `v26.40.26`. The [live API](https://bedrock.graphics/api) does not provide an
  explicit `gameVersion` for this entry; Orion labels the target as URL-inferred
  only if all three material paths agree. There is no evidence here that Prizma
  stopped using BetterRTX. A stale `lastUpdated` field is not its commit date.
- [Vanilla RTX App](https://github.com/Cubeir/Vanilla-RTX-App) still documents a
  BetterRTX manager and combined usage. Orion's one-provider-per-instance policy
  is deliberately stricter, as requested; it is not an upstream technical rule.
- [Vanilla RTX's manifest](https://github.com/Cubeir/Vanilla-RTX/blob/master/Vanilla-RTX/manifest.json)
  declares pack version `1.26.22` but minimum engine `1.26.40` at review time.
  Comparing the former with a Minecraft version would give a misleading result.

Consequently, Prizma's advertised `26.40.26` target does not automatically permit
installation or launch on `26.52.03`. The user must explicitly accept the newer-game
risk and install/reinstall that preset. Restore originals or choose an exact-target
preset to avoid this unsupported-version override.
Neither the Discord warning nor the current metadata establishes the root cause
of the reported Wine null-pointer crash; no real-game rendering fix is claimed.

## Launch configuration

Preferences are per system, per instance and off by default. **Request ray-traced graphics**
writes `graphics_mode:3` and `graphics_mode_switch:1` into existing GDK player
`options.txt` files before launch. **Also disable VSync** additionally writes
`gfx_vsync:0`; otherwise VSync is preserved. Save with **Save instance preferences**.
Unchecking stops enforcing those options, without undoing Minecraft's last saved
settings. Start/sign into the game once if it has not created its storage profiles.
Orion does not invent a profile ID or touch system Minecraft data.
**Advanced video settings** independently requests `show_advanced_video_settings:1`.

Ray tracing still requires compatible hardware, graphics drivers, WineGDK and an
RTX pack/world. Installing shaders does not provide GPU support or make every game
build compatible. The UI does not claim to benchmark or validate rendering support.

### NVIDIA runtime defaults (both providers)

An instance using either RTX provider receives the same process-local DLSS support:
Orion discovers the host's Windows x64 NGX driver libraries, passes
`NVIDIA_WINE_DLL_DIR` to WineGDK prefix setup and the game, and enables
`DXVK_ENABLE_NVAPI=1`. WineGDK 11.18-8-winrt registers the host directory under
`HKLM\Software\NVIDIA Corporation\Global\NGXCore\FullPath`. It preserves existing
registry values and does not copy or redistribute proprietary NVIDIA drivers.
An explicit invalid driver-directory override is not replaced with another driver.
If the bundled/installed runtime predates this support, an NVIDIA RTX launch
requests the newer runtime; offline fallback to an older runtime produces an
actionable error instead of silently claiming DLSS setup succeeded. Other launches
keep the normal runtime-selection policy.

When the proprietary NVIDIA kernel driver is detected, Orion also supplies PRIME
offload defaults for the game, without requiring the `prime-run` shell wrapper.
Without NGX, launch continues with a diagnostic explaining that automatic DLSS
setup is unavailable; AMD/Intel ray tracing is not claimed to support DLSS.
Explicit per-instance environment variables take precedence over these defaults.
An inherited Vulkan restriction (for example, `VK_ICD_FILENAMES` pointing only to
Intel) can still prevent NVIDIA discovery. Remove that restriction from the
launching environment when testing NVIDIA; Orion does not rewrite global driver
selection or assume a distribution-specific NVIDIA ICD filename.
For developer host tests, also check `MESA_VK_DEVICE_SELECT` and
`CUDA_VISIBLE_DEVICES`: an Intel-only Mesa selector or empty CUDA device list
can prevent NVIDIA use (the latter crashed this host's driver during device
creation). Clear test-harness restrictions only in the disposable test process,
not globally and not by silently overriding a user's explicit isolation policy.
No global environment, driver installation, shader compatibility rule, or provider
configuration is changed. Removing/resetting the provider stops applying defaults.

The validation overlay is off by default:
`DXVK_NVAPI_SET_NGX_DEBUG_OPTIONS=DLSSIndicator=0` also clears a persistent NGX
indicator setting when NVAPI initializes. `__NGX_SHOW_INDICATOR=0` is passed too.
Developer overrides can re-enable diagnostics; normal play does not need the
DLSSv2 watermark. A previously running game may need restarting to hide it.

Host validation on 2026-10-04: Minecraft 26.21.01, Vanilla RTX 1.26.14, NVIDIA
RTX 4070 Laptop / driver 615.71.09, WineGDK 11.18-7 plus the NGX discovery setup.
The original game's DLSS 2.1.16 rendered in-world (indicator showed internal and
output resolutions), the upscaling toggle became usable, and the user reported
stable play. This validates that combination, not every BetterRTX preset, GPU,
game version or the root cause of prior crashes. The shared runtime setup applies
to both providers; their independent shader/texture compatibility checks remain.
The follow-up WineGDK 11.18-8 build also passed a short in-world check through
Orion's automatic setup, without `prime-run` or manual NGX links: both host NGX
DLLs and the original game DLSS DLL loaded, NVAPI cubin execution succeeded,
and the ray-traced world rendered without the diagnostic watermark.

## Safety and scope

Game execution, archive operations and RTX edits share an exclusive instance lease.
Close the game first. Downloads support cancellation, size limits and bounded
timeouts; local archives additionally reject traversal, symlinks and duplicate paths.
Preset payloads are limited to four known RTX slots and require a compiled-material
header. This validates format, not the trustworthiness of a shader author.

Before mutations, a durable rollback journal records the old bytes and SHA-256
hashes. Interrupted work rolls back when RTX next accesses the instance or before
launch. Changed external files or corrupt backups block recovery; retain
`<instance>/rtx/transaction` for investigation. Normal cancellation removes temporary
downloads; interrupted shader downloads are discarded on the next safe access.
This RTX flow does not yet resume interrupted network transfers through Downloads.

This implementation uses public endpoints and format contracts. It does not include
third-party application code, a bundled LUT collection or a local texture Tuner.
No proprietary DLSS runtime or downloaded shader is bundled in
the launcher. BetterRTX presets and Cubeir's packs retain their own authorship and
terms; links and provider names are shown in the interface.

## Current BetterRTX integration

The native Linux workflow interoperates with the public catalog and local material
formats used by [BetterRTX Installer](https://github.com/BetterRTX/BetterRTX-Installer).
Reference reviewed: `f1d86827871d261941a721a7ed6acbf675660313`, dated 2025-09-06.
As checked on 2026-10-03, the repository is not archived, but the current website
documents a web installer with no external installer requirement. Lack of recent
main-branch commits alone is not an official abandonment announcement.
Orion implements the supported installation
flows independently; it does not bundle or execute the GPL-3.0 Installer's code,
Windows package discovery, PowerShell scripts or IObit Unlocker.

For GDK writes, the [current official setup](https://bedrock.graphics/#setup) is the
authority: redirect `RTXStub`, `RTXPostFX.Bloom` and `RTXPostFX.ToneMapping` in
`materials.index.json` to separate compiled materials. The unique `orion-rtx-*`
folder serves the same role as the guide's `betterrtx/` folder. The index references
extensionless paths; material files end in `.material.bin`. Original bytes remain
local, so restoration never downloads a potentially mismatched stock shader.

### Native shader editor

The editor reads `/api/build/versions` and each version's `/form` from
`https://bedrock.graphics`. It exposes the upstream categories, toggles, numeric
controls, choices and RGB values, including conditional visibility, macro scaling
and color intensity. Search spans categories. Labels/descriptions remain upstream
English; Orion navigation is localized. Changing/loading a version or resetting
defaults requires confirmation before replacing edited values. Editor values are
session-local, not silently applied to games. **Export settings JSON** keeps a
version-labelled file of the validated macro values without account or path data.

**Compile preset** sends only shader settings to the official `/api/build` service,
polls the returned job, and previews the compiled preset. Installation requires a
separate confirmation, a known game target (with acknowledgement for newer games), and the same validated three-material transaction as
catalog presets. No third-party JavaScript, macros or shell scripts execute locally.
Network responses, job identifiers and values are bounded and validated. Cancellation
requests cancellation of that operation's job. Remote authentication or anti-bot
requirements are respected, never bypassed; if required, use a trusted exported
versioned `.rtpack` instead. Untargeted builds remain previewable but cannot be
installed. This is not an offline shader compiler or an embedded browser.
The site's PBR generator, AI assistant, account publishing and material-upload
administration are not integrated.

### Single-instance operations, backups and links

Every install, configuration change, restore and export applies only to the
instance selected at the top. There is no bulk or all-instances mode. Each operation
retains its activity lease, compatibility checks and transaction handling.

Export current or original shaders as `.rtpack` backups from Configuration.
Exports never overwrite an existing file or write inside the instance. Keep
original Minecraft shader backups private; exporting does not grant redistribution
rights. Textures and worlds are not part of these archives.

`--open brtx://preset/ID`, legacy `brtx://creator/HASH` links and local `.rtpack`
files only open a preview/import prompt, never install automatically. The optional
per-user protocol registration does not replace another application's default
handler. Legacy creator build links depend on upstream retention; new Orion builds
use `/api/build`, not the old builder endpoints.

### Optional DLSS management

[Vanilla RTX App](https://github.com/Cubeir/Vanilla-RTX-App#dlss-swapper) still
provides a DLSS swapper. Orion keeps an optional per-instance equivalent: download
from the official NVIDIA/DLSS repository, import a trusted local x64 DLL, and
restore the original. It does not use BetterRTX's deprecated `/api/dlss` links.
Downloads resolve an immutable NVIDIA commit. Format checks and SHA-256 receipts
detect damaged files; they do not authenticate the publisher of a local DLL.
Only installations already containing `nvngx_dlss.dll` can be changed. The first
original is retained, and externally changed replacements are quarantined on
restore. Hardware/driver/Wine support and game compatibility are not guaranteed.
This is not the full version-library/ZIP-import UI of Vanilla RTX App.

Completed shader transactions also record pending cleanup. After interruption,
cleanup resumes without deleting active shaders; unknown orphaned managed folders
are moved to quarantine. Corrupt rollback backups block unsafe writes.

Texture identities come from the upstream
[Vanilla RTX](https://github.com/Cubeir/Vanilla-RTX/blob/master/Vanilla-RTX/manifest.json),
[Normals](https://github.com/Cubeir/Vanilla-RTX/blob/master/Vanilla-RTX-Normals/manifest.json)
and [Opus](https://github.com/Cubeir/Vanilla-RTX/blob/master/Vanilla-RTX-Opus/manifest.json)
manifests. Unknown future variants are not guessed by display name.

## Validation

```sh
dotnet test OrionLauncher.slnx --filter FullyQualifiedName~Rtx
dotnet run --project tools/Orion.RtxSmoke
# Also exercise the remote compiler and official DLSS download:
dotnet run --project tools/Orion.RtxSmoke -- --compile --dlss
```

The optional live smoke test downloads the public catalog, installs/restores a
preset and imports all three texture variants into a freshly created temporary
fixture. It deletes only that fixture and never accesses real launcher instances.
It also validates the live creator schemas. It tests data flows, not visual
rendering inside Minecraft. `--compile` submits one default build to the official
service and reports authentication/anti-bot limitations without bypassing them.

Live check on 2026-10-03: 26 presets; stable 1.4.4 exposed 231 settings in 19
categories, beta 1.5.0-beta.7 exposed 357 in 24 categories. Three beta fields refer
to a removed parent and are kept inactive (and excluded from compilation), as in
the web form. Compilation was refused by the service's authentication/anti-bot
gate; no successful live custom compilation is claimed. Official NVIDIA download,
three-material preset installation/restoration and all three Vanilla RTX imports
were validated with disposable fixtures, not a real game session.

Live check on 2026-10-04: the catalog still returned 26 presets. Downloaded and
installed the current default and Prizma materials into an isolated fixture,
verified original bytes and restoration, and verified that Prizma cannot launch
after changing the fixture to a newer game version. All three current Vanilla
RTX variants passed manifest inspection and import. No custom compilation was
submitted during this check, and no real-game rendering or crash fix was validated.
