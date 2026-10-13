#!/usr/bin/env bash
# =============================================================================
# install.sh - puts the JUST suite into your user account (no root needed).
#
#   ./install.sh               install / update from the folder this script is in
#   ./install.sh --uninstall   remove everything this script installed
#
# Where things go:
#   ~/.local/opt/justplay/                       the apps (this folder, copied)
#   ~/.local/share/applications/justplay-*.desktop  launcher entries
#   ~/.local/bin/JustPlayCLI                     the command-line tool (a link)
#
# Your settings and library index live in ~/.local/share/JustPlay and
# ~/.local/share/JUST and are NOT touched - not by an update, not by --uninstall.
# (Shipped by build/publish-linux.sh as install.sh inside the release tarball.)
# =============================================================================
set -euo pipefail

SRC="$(cd "$(dirname "$0")" && pwd)"
DEST="$HOME/.local/opt/justplay"
APPS="${XDG_DATA_HOME:-$HOME/.local/share}/applications"
BIN="$HOME/.local/bin"

ENTRIES=(justplay-player justplay-stream justplay-tag)

if [[ "${1:-}" == "--uninstall" ]]; then
    for e in "${ENTRIES[@]}"; do rm -f "$APPS/$e.desktop"; done
    [[ -L "$BIN/JustPlayCLI" ]] && rm -f "$BIN/JustPlayCLI"
    rm -rf "$DEST"
    echo "JUST suite removed. Settings in ~/.local/share/JustPlay were left in place."
    exit 0
fi

if [[ "$SRC" != "$DEST" ]]; then
    # Replace, do not merge: a file the new version dropped must not linger.
    rm -rf "$DEST"
    mkdir -p "$DEST"
    cp -a "$SRC/." "$DEST/"
fi

mkdir -p "$APPS" "$BIN"

entry() {  # $1 file  $2 Name  $3 exe  $4 icon  $5 Comment  $6 extra Exec args  $7 Categories
    cat > "$APPS/$1.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=$2
GenericName=$5
Comment=$5 - part of J.U.S.T. (Just Useful Sound Tools)
Exec="$DEST/$3"$6
Icon=$DEST/icons/$4.png
Terminal=false
Categories=$7
StartupWMClass=$3
EOF
}

entry justplay-player "JUST PLAY"   JustPlay   justplay   "DJ player"            ""    "AudioVideo;Audio;Player;"
entry justplay-stream "JUST STREAM" JustStream juststream "Streaming console"    ""    "AudioVideo;Audio;"
entry justplay-tag    "JUST TAG"    JustTag    justtag    "Audio tag editor"     " %f" "AudioVideo;Audio;"

ln -sfn "$DEST/JustPlayCLI" "$BIN/JustPlayCLI"

command -v update-desktop-database >/dev/null && update-desktop-database "$APPS" 2>/dev/null || true

echo "JUST suite installed to $DEST"
echo "  - JUST PLAY, JUST STREAM and JUST TAG are in your application launcher"
echo "  - JustPlayCLI is linked into $BIN$([[ ":$PATH:" == *":$BIN:"* ]] || echo " (not on your PATH yet)")"
