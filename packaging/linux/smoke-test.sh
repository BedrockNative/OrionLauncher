#!/usr/bin/env bash
set -euo pipefail
orion_release_dir="$(realpath -- "${1:?Release directory required}")"
orion_smoke_scripts="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd "$orion_release_dir"
sha256sum --check SHA256SUMS
orion_smoke_root="$(mktemp -d -t orion-package-smoke-XXXXXXXX)"
orion_wineserver=''
cleanup() {
  if [[ -n "$orion_wineserver" ]]; then
    WINEPREFIX="$orion_smoke_root/wineprefix" "$orion_wineserver" -k || true
  fi
  rm -rf -- "$orion_smoke_root"
}
trap cleanup EXIT
export XDG_CACHE_HOME="$orion_smoke_root/cache"
export XDG_CONFIG_HOME="$orion_smoke_root/config"
export XDG_DATA_HOME="$orion_smoke_root/data"
export XDG_RUNTIME_DIR="$orion_smoke_root/runtime"
mkdir -m 700 "$XDG_RUNTIME_DIR"
mkdir -p "$XDG_CACHE_HOME/fontconfig" "$XDG_CONFIG_HOME" "$XDG_DATA_HOME"
shopt -s nullglob
archives=("$orion_release_dir"/*.tar.gz)
images=("$orion_release_dir"/*.AppImage)
[[ ${#archives[@]} == 1 && ${#images[@]} == 1 ]]
tar -xzf "${archives[0]}" -C "$orion_smoke_root"
bundles=("$orion_smoke_root"/OrionLauncher-*)
[[ ${#bundles[@]} == 1 ]]
orion_app="${bundles[0]}/usr/lib/orion"
export FONTCONFIG_FILE="${bundles[0]}/etc/fonts/fonts.conf"
export FONTCONFIG_PATH="${bundles[0]}/etc/fonts"
python3 "$orion_smoke_scripts/fontconfig-smoke.py" "${bundles[0]}"
"${bundles[0]}/AppRun" --help
# Check every ELF (including Wine Unix modules and WebKit helpers), not only the apphost.
while IFS= read -r -d '' binary; do
  if file -b "$binary" | grep -q '^ELF'; then
    result="$(ldd "$binary" 2>&1 || true)"
    if [[ "$result" == *'not found'* || "$result" == *'version `'* ]]; then
      printf 'Unresolved dependencies: %s\n%s\n' "$binary" "$result" >&2
      exit 1
    fi
  fi
done < <(find "$orion_app" "${bundles[0]}/usr/lib/x86_64-linux-gnu" -type f -print0)
mapfile -d '' xodus < <(find "$orion_app/runtimes/xodus" -type f -name xodus-cli -print0)
[[ ${#xodus[@]} == 1 ]]
"${xodus[0]}" --version
"${xodus[0]}" accounts --help
mapfile -d '' wine < <(find "$orion_app/runtimes/winegdk" -path '*/bin/wine' -print0)
[[ ${#wine[@]} == 1 ]]
"${wine[0]}" --version
orion_wineserver="$(dirname -- "${wine[0]}")/wineserver"
WINEPREFIX="$orion_smoke_root/wineprefix" WINEDEBUG=-all,err+all WINEBOOT_HIDE_DIALOG=1 \
  WINESERVER="$orion_wineserver" xvfb-run -a timeout 180 "$(dirname -- "${wine[0]}")/wineboot" -u
WINEPREFIX="$orion_smoke_root/wineprefix" "$orion_wineserver" -k
# Works on runners without a FUSE mount; keep the image executable after artifact download.
if [[ ! -x "${images[0]}" ]]; then chmod +x "${images[0]}"; fi
APPIMAGE_EXTRACT_AND_RUN=1 "${images[0]}" --help
# Exercise WebKit subprocess launch and local HTML rendering, not just linking.
ORION_PACKAGING_VERIFY_WEBKIT=1 dbus-run-session -- xvfb-run -a "${bundles[0]}/AppRun"
# Isolated desktop startup catches P/Invoke and font/rendering failures that --help
# cannot exercise. No real accounts, worlds, keyring or desktop are used.
set +e
dbus-run-session -- xvfb-run -a timeout 15 "${bundles[0]}/AppRun"
orion_gui_result=$?
set -e
if [[ "$orion_gui_result" != 124 ]]; then
  printf 'GUI exited before the startup smoke-test deadline (status %s).\n' "$orion_gui_result" >&2
  exit 1
fi
