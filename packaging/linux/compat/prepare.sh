#!/usr/bin/env bash
set -euo pipefail
# Only test tools and the documented system/desktop base. Never install the app stack
# (.NET, Wine, GTK, WebKit). Mesa provides a software GPU inside the container.
case "${1:?Distribution family required}" in
  debian|ubuntu)
    apt-get update
    DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      python3 ca-certificates file binutils dbus-daemon xvfb xauth x11-utils x11-apps passwd \
      libgl1 libegl1 libgles2 libgbm1 libvulkan1 mesa-vulkan-drivers \
      libwayland-client0 libwayland-server0 libwayland-cursor0 libwayland-egl1 \
      libstdc++6 libgcc-s1 zlib1g liblzma5 libzstd1 libexpat1 libuuid1 libgmp10 libgpg-error0 \
      libffi8 libpcre2-8-0 libmount1 libblkid1 libcap2 libattr1 libacl1 \
      libdbus-1-3 libudev1 libsystemd0 libusb-1.0-0 libasound2t64 libpulse0 \
      libx11-6 libx11-xcb1 libxcb1 libxau6 libxdmcp6 libice6 libsm6 libxext6 \
      libxrender1 libxfixes3 libxi6 libxrandr2 libxcursor1 libxinerama1 libxss1 libxxf86vm1 \
      libxcb-dri2-0 libxcb-dri3-0 libxcb-glx0 libxcb-present0 libxcb-randr0 \
      libxcb-render0 libxcb-shm0 libxcb-sync1 libxcb-xfixes0
    dpkg-query -W -f='${Package}=${Version}\n' > /opt/orion-tests/packages.txt
    ;;
  arch)
    pacman -Syu --noconfirm --needed python ca-certificates file binutils dbus \
      xorg-server-xvfb xorg-xauth xorg-xwininfo xorg-xwd shadow \
      mesa libglvnd vulkan-icd-loader vulkan-swrast \
      gcc-libs zlib xz zstd expat util-linux-libs gmp libgpg-error systemd-libs \
      libffi pcre2 libcap attr acl \
      libusb alsa-lib libpulse libx11 libxcb libxau libxdmcp libice libsm libxext \
      libxrender libxfixes libxi libxrandr libxcursor libxinerama libxss libxxf86vm
    pacman -Q > /opt/orion-tests/packages.txt
    ;;
  fedora)
    dnf install -y --setopt=install_weak_deps=False python3 ca-certificates file binutils \
      dbus-daemon xorg-x11-server-Xvfb xorg-x11-xauth xwininfo xwd shadow-utils \
      libglvnd-glx libglvnd-egl libglvnd-gles mesa-dri-drivers mesa-vulkan-drivers vulkan-loader \
      libwayland-client libwayland-server libwayland-cursor libwayland-egl \
      libstdc++ libgcc zlib-ng-compat xz-libs libzstd expat libuuid gmp libgpg-error \
      libffi pcre2 libmount libblkid libcap libattr libacl \
      dbus-libs systemd-libs libusb1 alsa-lib pulseaudio-libs \
      libX11 libxcb libXau libXdmcp libICE libSM libXext libXrender libXfixes libXi \
      libXrandr libXcursor libXinerama libXScrnSaver libXxf86vm
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
# Some base images (including Ubuntu) already provide an ordinary UID 1000.
# Reuse it inside this disposable test image; never create a duplicate UID.
existing_user="$(getent passwd 1000 | cut -d: -f1 || true)"
if [[ -n "$existing_user" ]]; then
  usermod --login orion --home /home/orion --move-home "$existing_user"
else
  useradd --create-home --uid 1000 orion
fi
