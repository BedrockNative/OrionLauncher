#!/usr/bin/env bash
set -euo pipefail
# Run in Ubuntu 24.04, not on the user's host.
apt-get update
apt-get install -y --no-install-recommends \
  ca-certificates curl python3 cmake meson ninja-build build-essential nasm pkg-config zlib1g-dev g++-mingw-w64-x86-64 patchelf binutils \
  file squashfs-tools desktop-file-utils xdg-utils xvfb xauth dbus-x11 \
  libicu74 libssl3t64 libkrb5-3 libgssapi-krb5-2 libfontconfig1 fonts-dejavu-core \
  libx11-6 libice6 libsm6 libxrandr2 libxi6 libxcursor1 libxinerama1 libxrender1 \
  libxext6 libxfixes3 libx11-xcb1 libxcb1 libxcb-shm0 libxcb-render0 \
  libwebkit2gtk-4.1-0 libgtk-3-0t64 libsecret-1-0 glib-networking \
  gsettings-desktop-schemas shared-mime-info libgdk-pixbuf-2.0-0 \
  gstreamer1.0-plugins-base gstreamer1.0-plugins-good gstreamer1.0-libav \
  libasound2t64 libasound2-plugins libpulse0 libudev1 libusb-1.0-0 \
  libgnutls30t64 libgcrypt20 libunwind8 libv4l-0 libsdl2-2.0-0 \
  libpcap0.8t64 libpcsclite1 libxkbregistry0 libosmesa6 libvulkan1 libgl1 libegl1 libgles2 libgbm1 mesa-vulkan-drivers
