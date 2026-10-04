# Appearance options

Orion offers four complete surface themes (Orion, Graphite, Grove and Dune),
with independent controls for colors, layout and wallpaper:

| Area | Options |
| --- | --- |
| Display mode | Dark, light, system, schedule, sunrise/sunset |
| Accent | 49 additional hues, custom RGB, four Orion palettes, inherit shared palette |
| Per-mode profiles | Independent light/dark accent, background base and material |
| Base | Original theme, neutral, accent tint, custom tint; strength 0–100% |
| Layout | Sidebar or top navigation; current Xbox profile in either layout |
| Motion | Reduced control transitions (activity indicators are still functional) |
| Wallpaper | Fixed local image or directory; optional timed rotation (1–1,440 minutes); next image; random/sequential; remove |
| Image fit | Smart fill, center, fit, stretch, tile, top left, top right |
| Image adjustments | Blur 0–50, brightness 20–100%, opacity 0–100% |
| Material | Balanced, clear, solid/reset; opacity 0–100%, diffusion 0–32, veil 0–80%, readability protection |
| Desktop transparency | Off by default; consent-based exception on Umbriel, session detection and compositor-specific help |

Changes apply immediately and save automatically, including both profiles.
Reset restores and saves Orion's original appearance. Disk writes are serialized
and coalesced over 250 ms; normal shutdown waits for the latest valid settings.
Editing the light profile does not itself force the entire launcher into light mode.
Old settings migrate without changing their original shared accent or surfaces.
Unknown values and non-finite/out-of-range numbers are normalized; invalid custom
hex colors are indicated and cannot be saved through the interface.

## Native rendering adaptations

Orion uses native Avalonia controls on Linux. Rendering follows these principles:

- Colors are adjusted for readable accent text and button labels. A custom RGB
  seed is not guaranteed to remain an identical RGB on every light/dark surface.
- Material diffusion softens the wallpaper layer (combined with wallpaper blur),
  not the desktop behind the OS window. It does not blur individual panel backdrops
  independently. Panels, dialogs and inputs can be translucent over Orion's own
  wallpaper, while the underlying native window remains opaque. This works without
  requiring a compositor-specific acrylic API.
- Readability protection enforces at least 96% panel opacity and an 88% neutral
  wallpaper veil for headings outside panels. Disable it explicitly
  for the fully transparent look; arbitrary photographs can then reduce contrast.
- Solar mode uses locally supplied approximate coordinates, not IP geolocation.
  Default coordinates are 0°, 0°. Approximate solar elevation includes polar day
  and polar night. Schedule/solar are reevaluated every 30 seconds while active.
  [NOAA solar calculation background](https://gml.noaa.gov/grad/solcalc/calcdetails.html).
- Reduced motion removes control transitions; useful indeterminate progress
  indicators are not removed. Wallpaper changes do not animate.

## Local wallpaper

For Orion's own wallpaper, **Choose fixed image** keeps the selected file across
restarts and disables folder rotation. **Choose folder** preserves the existing
folder workflow. Enable **Automatically rotate folder wallpapers** to change the
image at the selected interval (five minutes by default). Rotation is off by
default, including for existing settings, and requires at least two images.
Random order avoids immediately repeating the current image; sequential order
follows filenames. **Next image** remains available when automatic rotation is off.
Hidden launcher windows do not advance automatically; closing the window stops
its timer. Only the main window owns rotation; other windows share its decoded image.
All changes apply and save automatically. Missing/invalid files show a status
message and fall back to the theme background.

## Keep the desktop wallpaper out of Orion

**Appearance → Desktop transparency → Keep Orion opaque on the desktop** is off
by default. The app already renders a solid base, and internal material opacity
is separate. X11's `_NET_WM_WINDOW_OPACITY` is advisory: setting it to 100% does
not defeat a compositor's forced rule. Wayland has no universal override either.

Alternative mechanisms are compositor-specific: Hyprland offers runtime window
properties, and niri has a rule-opacity toggle. Their commands are not a universal
protocol, and a blind toggle cannot safely establish or restore an unknown prior
state. Orion does not run these against the currently focused window. Umbriel's
installed command interface has no equivalent opacity setter.

For **Umbriel**, clicking the option asks permission to append a scoped exception
to `$XDG_CONFIG_HOME/umbriel/config.toml` (or `~/.config/umbriel/config.toml`). The
confirmation shows the path: confirm it is the active config if you launch your
compositor with custom arguments. Before writing, Orion validates a temporary file
in the same directory, checks for concurrent edits and keeps a private backup.
Only `OrionLauncher` is matched. No sudo or compositor restart is needed; Umbriel
reloads its config automatically. Disabling removes only Orion's unchanged marked
block, preserving subsequent user edits. Edited/duplicate blocks and symlink paths
are refused, rather than overwritten. Cancelling leaves the option off and files
untouched. The switch reflects a managed block, not an unverified X11 hint saved
by an earlier build. Resetting the color theme does not remove external rules.

For **Hyprland, niri, KDE, GNOME, Zorin OS, Pop!_OS/COSMIC and other sessions**,
the option explains the relevant exception and remains unchecked. Automatic edits
are not offered where they have not been implemented safely. GNOME-based editions
and COSMIC are distinguished; GNOME extension settings are not guessed or disabled.
Manual exceptions remain user-owned and must be removed manually.

Menus use a minimum 94% background opacity so the underlying page's text does not
bleed into menu items. Other panels keep their configured material transparency.
Overlay selectors close when the page scrolls, disappears or loses window focus.
Live palette changes update existing brushes instead of replacing their dictionaries.

References: [EWMH opacity property](https://specifications.freedesktop.org/wm/latest/ar01s05.html),
[Umbriel window rules](https://docs.noctalia.dev/umbriel/window-rules/),
[niri window rules](https://github.com/niri-wm/niri/blob/main/docs/wiki/Configuration%3A-Window-Rules.md),
[Hyprland runtime window properties](https://wiki.hypr.land/0.54.0/Configuring/Dispatchers/),
[KDE window rules](https://docs.kde.org/stable_kf6/en/kwin/kcontrol/windowspecific/index.html).

## Resource and privacy limits

Only the selected local directory is scanned (up to 2,000 entries, no recursion).
Supported images: PNG, JPEG, WebP, BMP and first-frame GIF. Symbolic-link files,
files over 32 MiB, and images over 40 megapixels are rejected. Decoding is off the
UI thread; a single wallpaper is retained at a maximum dimension of 1920 pixels.
Some formats require a larger temporary decode before downsampling. There is no
full-gallery bitmap cache, network download or automatic timed slideshow.
Slider changes are debounced; obsolete loads cannot replace a newer image.
Removing a background or closing the window releases the decoded bitmap.

Local folder paths and optional solar coordinates are stored in local settings,
never sent to a theme service. Missing/corrupt images produce a localized status
and the solid theme remains available. Theme resources are semantic and shared
with detached log windows; they do not depend on the currently visible settings tab.
