#!/usr/bin/env bash
# Installs Centre Stage as a desktop application: publishes the viewer to
# ~/.local/share/centrestage, installs the .desktop entry and icon, and makes it the
# default handler for .glb/.gltf files (double-click in Files). Idempotent.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BINDIR="$HOME/.local/share/centrestage"
APPDIR="$HOME/.local/share/applications"
ICONDIR="$HOME/.local/share/icons/hicolor/scalable/apps"

# The native host library is a build artefact of the dlss5-linux checkout;
# the csproj ships it from src/CentreStage/runtimes/..., so build it BEFORE
# the publish. Without it the app still runs, just without the seam.
if [[ -f "$REPO/linux/build-native-host.sh" ]]; then
    echo "building the native host library ..."
    bash "$REPO/linux/build-native-host.sh" || echo "warning: native host build failed - the seam will be unavailable."
else
    echo "warning: linux/build-native-host.sh missing - the seam will be unavailable."
fi

echo "publishing viewer to $BINDIR ..."
dotnet publish "$REPO/src/CentreStage" -c Release -o "$BINDIR" --nologo -v q

# Remove pre-rename installs ("GlbView", "Onstage") so they cannot shadow Centre Stage.
rm -rf "$HOME/.local/share/glbview" "$HOME/.local/share/onstage"
rm -f "$APPDIR/glbview.desktop" "$ICONDIR/glbview.svg"
rm -f "$APPDIR/onstage.desktop" "$ICONDIR/onstage.svg"

install -Dm644 "$REPO/linux/centrestage.svg" "$ICONDIR/centrestage.svg"

# Make the demonstration model search self-contained: the app looks in
# ~/.local/share/dlss5-demo first (then the dlss5-linux checkout). EDSR-base
# x2 is the default (Apache-2.0, openly obtained - no vendor data).
DLSS5="${DLSS5_LINUX:-/home/michael/Projects/dlss5-linux}"
for model in edsr-base-x2.pt RealESRGAN_x2plus.safetensors; do
    if [[ -f "$DLSS5/.tools/models/$model" ]]; then
        install -Dm644 "$DLSS5/.tools/models/$model" "$HOME/.local/share/dlss5-demo/$model"
    else
        echo "warning: $model not found in $DLSS5/.tools/models - the neural demo stays unavailable until it is downloaded."
    fi
done

install -d "$APPDIR"
# Escape the replacement for sed (&, \ and the | delimiter). The template
# quotes the whole Exec path, so a home directory containing spaces still
# parses; a home path containing quotes or backslashes would need
# Desktop-Entry-layer escaping and is not handled.
escaped=$(printf '%s' "$BINDIR" | sed -e 's/[\\&|]/\\&/g')
sed -e "s|@BINDIR@|$escaped|" "$REPO/linux/centrestage.desktop" > "$APPDIR/centrestage.desktop"

if command -v update-desktop-database >/dev/null; then
    update-desktop-database "$APPDIR"
fi

# Claim the glTF MIME types from whatever owns them (e.g. Exhibit).
if command -v xdg-mime >/dev/null; then
    for mime in model/gltf-binary model/gltf+json; do
        xdg-mime default centrestage.desktop "$mime"
    done

    echo "installed. glb/gltf files now open with Centre Stage:"
    xdg-mime query default model/gltf-binary
else
    echo "installed (xdg-mime not available; set the file association manually)."
fi
