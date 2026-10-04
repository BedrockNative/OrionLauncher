#!/usr/bin/env bash
set -euo pipefail
# Only test tools and the desktop/GPU-driver base. Never install the app stack
# (.NET, Wine, GTK, WebKit). Mesa provides a software GPU inside the container.
case "${1:?Distribution family required}" in
  debian|ubuntu)
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      python3 ca-certificates file binutils dbus-daemon xvfb xauth x11-utils x11-apps passwd \
      libgl1 libegl1 libgles2 libgbm1 libvulkan1 mesa-vulkan-drivers \
      libwayland-client0 libwayland-server0 libwayland-cursor0 libwayland-egl1
    dpkg-query -W -f='${Package}=${Version}\n' > /opt/orion-tests/packages.txt
    ;;
  arch)
    pacman -Syu --noconfirm --needed python ca-certificates file binutils dbus \
      xorg-server-xvfb xorg-xauth xorg-xwininfo xorg-xwd shadow \
      mesa libglvnd vulkan-icd-loader vulkan-swrast
    pacman -Q > /opt/orion-tests/packages.txt
    ;;
  fedora)
    dnf install -y --setopt=install_weak_deps=False python3 ca-certificates file binutils \
      dbus-daemon xorg-x11-server-Xvfb xorg-x11-xauth xwininfo xwd shadow-utils \
      libglvnd-glx libglvnd-egl libglvnd-gles mesa-dri-drivers mesa-vulkan-drivers vulkan-loader
    rpm -qa --qf '%{NAME}=%{VERSION}-%{RELEASE}\n' | sort > /opt/orion-tests/packages.txt
    ;;
  *) exit 2 ;;
esac
for tool in python3 ldd sha256sum tar Xvfb xwininfo xwd dbus-run-session; do
  command -v "$tool" > /dev/null
done
if grep -Ei '^(dotnet|aspnet|wine|webkit|gtk3|gtk4|libgtk-3|libgtk-4|libwebkit)' /opt/orion-tests/packages.txt; then
  echo 'Test base is contaminated with launcher dependencies.' >&2
  exit 1
fi
useradd --create-home --uid 1000 orion
