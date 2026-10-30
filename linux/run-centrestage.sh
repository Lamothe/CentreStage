#!/usr/bin/env bash
# Launch Centre Stage forced onto X11/XWayland.
#
# Under native Wayland the compositor ignores placement hints and decides at
# map time where the window lives and which monitor a double-click maximise
# targets. On the two-display setup that showed up as the window landing /
# full-screening on the wrong screen. X11 placement is deterministic and
# honours CENTRESTAGE_WINDOW_POS="x,y" when set.
#
# Forcing X11 takes both halves: XDG_SESSION_TYPE=x11 steers the toolkit's
# platform selection to X11, and WAYLAND_DISPLAY=wayland-none makes any
# Wayland connect fail. (Unsetting WAYLAND_DISPLAY alone does NOT work -
# libwayland falls back to the default socket "wayland-0"; and a bogus
# WAYLAND_DISPLAY alone CRASHES - Silk.NET selects Wayland from the session
# type and hard-fails instead of falling back.)
#
# Usage: run-centrestage.sh [args...]   (same args as the CentreStage binary,
# e.g. run-centrestage.sh /path/to/model.glb)
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
bin="$here/src/CentreStage/bin/Release/net10.0/CentreStage"

if [[ ! -x "$bin" ]]; then
  echo "error: $bin not built - run: dotnet build -c Release --project src/CentreStage" >&2
  exit 1
fi

exec env XDG_SESSION_TYPE=x11 WAYLAND_DISPLAY=wayland-none "$bin" "$@"
