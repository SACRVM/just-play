#!/usr/bin/env bash
# =============================================================================
# publish-linux.sh - the JUST suite for Linux x86_64: JustPlay (GUI player) +
# JustStream (streaming) + JustTag (tag editor) + JustPlayCLI, in ONE shared
# folder, packed as a tarball with a per-user installer.
#
#   ./build/publish-linux.sh
#
# Output:
#   publish/linux-x64/                                the run-in-place folder
#   bin/installer/JustPlay-<version>-linux-x64.tar.gz the release artefact
#
# Same shape as publish-win-x64.ps1 and for the same reason: the four tools SHARE
# the .NET runtime, the Avalonia natives, the BASS libraries, the ONNX runtime
# and keymodel.onnx - one flat, self-contained, multi-file folder serves all of
# them. The version comes from Directory.Build.props and nowhere else (see the
# howto-release skill); the artefact name is derived from it, suffix included,
# and deliberately does not start with "JustPlaySetup", so the updater never
# mistakes it for the Windows installer.
#
# Inside the tarball: the four executables, icons/, and install.sh, which puts
# the folder under ~/.local/opt/justplay, the launcher entries into
# ~/.local/share/applications and JustPlayCLI onto ~/.local/bin - no root, no
# system files, `install.sh --uninstall` takes it all back out.
#
# Needs: the .NET 10 SDK and python3 (icon extraction). Runtime needs on the
# target: glibc, PipeWire for per-app capture (pw-record / pw-link / pw-dump),
# gio for the Trash - each one optional, the feature reports itself missing.
# =============================================================================
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' Directory.Build.props | head -1)
[[ -n "$VERSION" ]] || { echo "Version not found in Directory.Build.props" >&2; exit 1; }

RID=linux-x64
OUT="$ROOT/publish/$RID"
DIST="$ROOT/bin/installer"
NAME="JustPlay-$VERSION-$RID"

echo "== JUST suite $VERSION ($RID) =="

# Always from an empty folder - a dirty one leaves stale files in the drop.
rm -rf "$OUT"
mkdir -p "$OUT" "$DIST"

for proj in JustPlay.App JustPlay.Cli JustPlay.Stream JustPlay.Tag; do
    echo "-- publish $proj"
    dotnet publish "src/$proj" -c Release -r "$RID" --self-contained -o "$OUT" \
        -p:DebugType=none -p:DebugSymbols=false -v quiet
done

# The BASS adapter copies every platform's natives next to the app (a plain build
# has no RID to choose by); a Linux drop needs only the .so files.
rm -f "$OUT"/*.pdb "$OUT"/*.dylib
for lib in bass bass_fx bassmix bassenc bassenc_mp3 bassenc_opus bassenc_flac; do
    rm -f "$OUT/$lib.dll"
done

# Launcher icons: the brand mark (Aurora) the .ico files already carry - the same
# art the Windows exe and the macOS .icns use. The window icon itself is still
# rendered per theme at runtime (ThemedWindowIcon); this one is what the launcher
# shows before the app runs.
mkdir -p "$OUT/icons"
extract_icon() {  # $1 = .ico, $2 = out.png (largest embedded PNG)
    python3 - "$1" "$2" <<'PYEOF'
import struct, sys
data = open(sys.argv[1], 'rb').read()
_, _, count = struct.unpack('<HHH', data[:6])
best = None
for i in range(count):
    off = 6 + i * 16
    w = data[off] or 256
    size, imgoff = struct.unpack('<II', data[off + 8:off + 16])
    if best is None or w > best[0]:
        best = (w, imgoff, size)
blob = data[best[1]:best[1] + best[2]]
assert blob[:8] == b'\x89PNG\r\n\x1a\n', 'largest .ico entry is not PNG'
open(sys.argv[2], 'wb').write(blob)
PYEOF
}
extract_icon src/JustPlay.App/Assets/justplay.ico      "$OUT/icons/justplay.png"
extract_icon src/JustPlay.Stream/Assets/juststream.ico "$OUT/icons/juststream.png"
extract_icon src/JustPlay.Tag/Assets/justtag.ico       "$OUT/icons/justtag.png"

install -m 755 build/linux-install.sh "$OUT/install.sh"
chmod 755 "$OUT/JustPlay" "$OUT/JustStream" "$OUT/JustTag" "$OUT/JustPlayCLI"

# The artefact: one top-level folder, named like the file.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
cp -a "$OUT" "$STAGE/$NAME"
tar -C "$STAGE" -czf "$DIST/$NAME.tar.gz" "$NAME"

echo
echo "Shipped to $OUT ($(find "$OUT" -type f | wc -l) files, $(du -sh "$OUT" | cut -f1))"
echo "Artefact:  $DIST/$NAME.tar.gz ($(du -h "$DIST/$NAME.tar.gz" | cut -f1))"
