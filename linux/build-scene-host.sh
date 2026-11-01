#!/usr/bin/env bash
# Build the CPU inference ABI for the opt-in scene-trained neural rendering demo.
set -euo pipefail
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DLSS5="${DLSS5_LINUX:-/home/michael/Projects/dlss5-linux}"
TRAIN="$DLSS5/scene-train"
TORCH_ROOT="${TORCH_ROOT:-/home/michael/Projects/ComfyUI/.venv/lib/python3.12/site-packages/torch}"
OUT="$REPO/src/CentreStage/runtimes/linux-x64/native"

if [[ ! -f "$TRAIN/host.cpp" || ! -d "$TORCH_ROOT/include" ]]; then
    echo "error: scene-train or libtorch not found (set DLSS5_LINUX / TORCH_ROOT)" >&2
    exit 1
fi

cmake -S "$TRAIN" -B "$TRAIN/build" -DTORCH_ROOT="$TORCH_ROOT" -DCMAKE_BUILD_TYPE=Release
cmake --build "$TRAIN/build" --target scene_host --parallel 1
mkdir -p "$OUT"
install -m 755 "$TRAIN/build/libscene_host.so" "$OUT/libscene_host.so"
echo "built $OUT/libscene_host.so"
