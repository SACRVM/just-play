#!/bin/zsh
# Builds the per-app audio-capture shim (Core Audio process taps) as a universal
# dylib into the vendored native dir, next to the BASS dylibs (same csproj wildcard
# copies it to every app output). Requires Xcode CLT. Min OS 14.2 = the tap API
# floor; MacProcessAudioCapture additionally gates usage on 14.4 (research doc).
set -euo pipefail
cd "$(dirname "$0")/.."

SRC=src/JustPlay.Audio.Bass/native/osx-src/juststream_capture.m
OUT=src/JustPlay.Audio.Bass/native/osx/libjuststream_capture.dylib

clang -dynamiclib -fobjc-arc -O2 \
    -arch x86_64 -arch arm64 \
    -mmacosx-version-min=14.2 \
    -framework Foundation -framework CoreAudio \
    -install_name @loader_path/libjuststream_capture.dylib \
    -o "$OUT" "$SRC"

codesign --force -s - "$OUT"
lipo -info "$OUT"
echo "→ $OUT"
