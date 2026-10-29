#!/usr/bin/env bash
# Builds the dlss5-linux native host boundary (vr/native-host.h + .cpp) into
# the shared library the viewer loads for the DLSS seam: an on-demand,
# frame-by-frame CPU pipeline over low-resolution frames. Synthetic weights:
# the transformation is ARBITRARY (the in-band label says so) - this is the
# tested C ABI of the dlss5-linux repository, not vendor upscaling.
#
#   DLSS5_LINUX=/path/to/dlss5-linux  (default /home/michael/Projects/dlss5-linux)
#
# The output is a build artefact (gitignored): src/CentreStage/runtimes/
# linux-x64/native/libdlss5_nr_host.so. The evaluator's arena refuses frames
# whose padded extent exceeds its 320x320 working floor (the documented
# bound of this boundary), which is the seam's 64x72 demo geometry.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DLSS5="${DLSS5_LINUX:-/home/michael/Projects/dlss5-linux}"
OUT="$REPO/src/CentreStage/runtimes/linux-x64/native"
CXX="${CXX:-c++}"

if [[ ! -f "$DLSS5/vr/native-host.cpp" ]]; then
    echo "error: dlss5-linux checkout not found at $DLSS5 (set DLSS5_LINUX)" >&2
    exit 1
fi

mkdir -p "$OUT"
"$CXX" -std=c++20 -O2 -fPIC -shared -Wall -Wextra -Werror -ffp-contract=off \
    -I"$DLSS5/vr" "$DLSS5/vr/native-host.cpp" -pthread \
    -o "$OUT/libdlss5_nr_host.so"
echo "built $OUT/libdlss5_nr_host.so"
