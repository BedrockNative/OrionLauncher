#!/usr/bin/env bash
set -euo pipefail
# Deliberately no GTK, WebKit, .NET or Wine packages. These must come from Orion.
# The desktop/driver base may pull common low-level libraries transitively.
apt-get update -qq
apt-get install -y --no-install-recommends \
  ca-certificates file binutils dbus-x11 xvfb xauth \
  libgl1 libegl1 libgles2 libgbm1 libvulkan1 mesa-vulkan-drivers
bash packaging/linux/smoke-test.sh "${1:?Release directory required}"
