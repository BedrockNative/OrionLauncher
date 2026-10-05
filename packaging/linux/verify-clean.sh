#!/usr/bin/env bash
set -euo pipefail
# Deliberately no GTK, WebKit, .NET or Wine packages. These must come from Orion.
# The desktop/driver base may pull common low-level libraries transitively.
apt-get update -qq
apt-get install -y --no-install-recommends \
  ca-certificates file binutils dbus-x11 xvfb xauth \
  libgl1 libegl1 libgles2 libgbm1 libvulkan1 mesa-vulkan-drivers \
  libwayland-client0 libwayland-server0 libwayland-cursor0 libwayland-egl1 \
  libstdc++6 libgcc-s1 zlib1g liblzma5 libzstd1 libexpat1 libuuid1 libgmp10 libgpg-error0 \
  libffi8 libpcre2-8-0 libmount1 libblkid1 libcap2 libattr1 libacl1 \
  libdbus-1-3 libudev1 libsystemd0 libusb-1.0-0 libasound2t64 libpulse0 \
  libx11-6 libx11-xcb1 libxcb1 libxau6 libxdmcp6 libice6 libsm6 libxext6 \
  libxrender1 libxfixes3 libxi6 libxrandr2 libxcursor1 libxinerama1 libxss1 libxxf86vm1 \
  libxcb-dri2-0 libxcb-dri3-0 libxcb-glx0 libxcb-present0 libxcb-randr0 \
  libxcb-render0 libxcb-shm0 libxcb-sync1 libxcb-xfixes0
bash packaging/linux/smoke-test.sh "${1:?Release directory required}"
