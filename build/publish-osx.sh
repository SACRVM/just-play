#!/bin/zsh
# ─────────────────────────────────────────────────────────────────────────────
# publish-osx.sh — build distributable macOS .app bundles + DMGs for the JUST
# suite (JUST PLAY + JUST STREAM), Intel and Apple Silicon.
#
#   ./build/publish-osx.sh                     # both apps, both archs, ad-hoc signed
#   ./build/publish-osx.sh justplay arm64      # one app, one arch
#   SIGN_ID="Developer ID Application: …" NOTARY_PROFILE=justsuite \
#       ./build/publish-osx.sh                 # signed + notarized release build
#
# Output: dist/<App>-<version>-osx-<arch>.dmg (+ the .app next to it).
#
# Signing modes:
#   - No SIGN_ID: ad-hoc signature ("-"). REQUIRED even for local use — Apple
#     Silicon refuses to load unsigned code, and install_name_tool already
#     invalidated the un4seen dylib signatures. Gatekeeper will still quarantine
#     downloads (testers: right-click → Open once).
#   - SIGN_ID set: Developer ID + hardened runtime + our entitlements.
#   - NOTARY_PROFILE set (with SIGN_ID): xcrun notarytool submit + staple —
#     profile created once via `xcrun notarytool store-credentials`.
#
# Self-contained publish: testers need no .NET install; DOTNET_ROOT games (the
# dev launcher's LSEnvironment) are unnecessary here.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

cd "$(dirname "$0")/.."
ROOT=$PWD
DOTNET=${DOTNET:-$HOME/.dotnet/dotnet}
DIST="$ROOT/dist"
VERSION=$(sed -n 's/.*<Version>\(.*\)<\/Version>.*/\1/p' Directory.Build.props | head -1)
SIGN_ID=${SIGN_ID:--}                    # "-" = ad-hoc
NOTARY_PROFILE=${NOTARY_PROFILE:-}
ENTITLEMENTS="$ROOT/build/osx-entitlements.plist"

APPS=(${=1:-justplay juststream})   # ${=…} forces word-splitting under zsh
[[ "${1:-}" == "all" ]] && APPS=(justplay juststream)
ARCHS=(${=2:-x64 arm64})

# ── per-app metadata ─────────────────────────────────────────────────────────
app_meta() {  # $1 = app key → sets NAME PROJ EXE ICO IDENT MIC DOCTYPES
    case "$1" in
        justplay)
            NAME="Just Play";  PROJ="src/JustPlay.App/JustPlay.App.csproj"
            EXE="JustPlay";    ICO="src/JustPlay.App/Assets/justplay.ico"
            IDENT="tools.justsuite.justplay";  MIC=0; DOCTYPES=1 ;;
        juststream)
            NAME="Just Stream"; PROJ="src/JustPlay.Stream/JustPlay.Stream.csproj"
            EXE="JustStream";   ICO="src/JustPlay.Stream/Assets/juststream.ico"
            IDENT="tools.justsuite.juststream"; MIC=1; DOCTYPES=0 ;;
        *) echo "unknown app '$1' (justplay|juststream)"; exit 2 ;;
    esac
}

# ── icns from the .ico art (largest embedded PNG → HIG geometry) ────────────
make_icns() {  # $1 = .ico  $2 = out.icns
    local png="${2%.icns}-src.png"
    python3 - "$1" "$png" <<'PYEOF'
import struct, sys
data = open(sys.argv[1], 'rb').read()
_, _, count = struct.unpack('<HHH', data[:6])
best = None
for i in range(count):
    off = 6 + i*16
    w = data[off] or 256
    size, imgoff = struct.unpack('<II', data[off+8:off+16])
    if best is None or w > best[0]: best = (w, imgoff, size)
blob = data[best[1]:best[1]+best[2]]
assert blob[:8] == b'\x89PNG\r\n\x1a\n', 'largest .ico entry is not PNG'
open(sys.argv[2], 'wb').write(blob)
PYEOF
    swift "$ROOT/build/make-mac-icns.swift" "$png" "$2"
    rm -f "$png"
}

# ── Info.plist ───────────────────────────────────────────────────────────────
write_plist() {  # $1 = plist path
    cat > "$1" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleName</key><string>$NAME</string>
    <key>CFBundleDisplayName</key><string>$NAME</string>
    <key>CFBundleIdentifier</key><string>$IDENT</string>
    <key>CFBundleShortVersionString</key><string>${VERSION%%-*}</string>
    <key>CFBundleVersion</key><string>${VERSION%%-*}</string>
    <key>CFBundleExecutable</key><string>$EXE</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSMinimumSystemVersion</key><string>13.0</string>
    <key>LSApplicationCategoryType</key><string>public.app-category.music</string>
PLIST
    if [[ $MIC == 1 ]]; then cat >> "$1" <<PLIST
    <key>NSMicrophoneUsageDescription</key>
    <string>$NAME captures the selected audio input to broadcast and record it.</string>
PLIST
    fi
    if [[ $DOCTYPES == 1 ]]; then cat >> "$1" <<PLIST
    <key>CFBundleDocumentTypes</key>
    <array>
        <dict>
            <key>CFBundleTypeName</key><string>Audio File</string>
            <key>CFBundleTypeRole</key><string>Viewer</string>
            <key>LSHandlerRank</key><string>Alternate</string>
            <key>CFBundleTypeExtensions</key>
            <array>
                <string>mp3</string><string>m4a</string><string>aac</string>
                <string>flac</string><string>wav</string><string>aiff</string>
                <string>aif</string><string>ogg</string><string>opus</string>
            </array>
        </dict>
    </array>
PLIST
    fi
    printf '</dict>\n</plist>\n' >> "$1"
}

# ── main loop ────────────────────────────────────────────────────────────────
mkdir -p "$DIST"
for app in "${APPS[@]}"; do
    app_meta "$app"
    for arch in "${ARCHS[@]}"; do
        RID="osx-$arch"
        BUNDLE="$DIST/$RID/$NAME.app"
        MACOS="$BUNDLE/Contents/MacOS"
        RES="$BUNDLE/Contents/Resources"
        echo "── $NAME $VERSION ($RID) ─────────────────────────────"

        rm -rf "$BUNDLE"
        mkdir -p "$MACOS" "$RES"

        "$DOTNET" publish "$PROJ" -c Release -r "$RID" --self-contained true \
            -o "$MACOS" -v quiet /p:DebugType=none
        write_plist "$BUNDLE/Contents/Info.plist"
        make_icns "$ICO" "$RES/AppIcon.icns"

        # Sign inside-out: EVERY file in MacOS/ (Apple Silicon loads NOTHING
        # unsigned, install_name_tool broke the un4seen signatures, and the
        # bundle seal refuses unsigned managed .dlls — codesign stores non-Mach-O
        # signatures in xattrs; same recipe as the dotnet notarization docs).
        # Hardened runtime + entitlements only make sense with a real identity.
        sign_flags=()
        [[ "$SIGN_ID" != "-" ]] && sign_flags=(--options runtime --entitlements "$ENTITLEMENTS" --timestamp)
        find "$MACOS" -type f ! -name "$EXE" -print0 \
            | xargs -0 codesign --force -s "$SIGN_ID" "${sign_flags[@]}" 2> >(grep -v "replacing existing signature" >&2)
        codesign --force -s "$SIGN_ID" "${sign_flags[@]}" "$MACOS/$EXE"
        codesign --force -s "$SIGN_ID" "${sign_flags[@]}" "$BUNDLE"
        codesign --verify --deep "$BUNDLE" && echo "codesign: OK ($SIGN_ID)"

        if [[ -n "$NOTARY_PROFILE" && "$SIGN_ID" != "-" ]]; then
            ZIP="$DIST/$NAME-$VERSION-$RID.zip"
            ditto -c -k --keepParent "$BUNDLE" "$ZIP"
            xcrun notarytool submit "$ZIP" --keychain-profile "$NOTARY_PROFILE" --wait
            xcrun stapler staple "$BUNDLE"
            rm -f "$ZIP"
        fi

        # DMG: app + Applications shortcut, compressed.
        DMG="$DIST/${NAME// /}-$VERSION-$RID.dmg"
        STAGE="$DIST/.dmg-stage"
        rm -rf "$STAGE" "$DMG"; mkdir -p "$STAGE"
        cp -R "$BUNDLE" "$STAGE/"
        ln -s /Applications "$STAGE/Applications"
        hdiutil create -quiet -volname "$NAME $VERSION" -srcfolder "$STAGE" -ov -format UDZO "$DMG"
        rm -rf "$STAGE"
        echo "→ $DMG"
    done
done
echo "done."
