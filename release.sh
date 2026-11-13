#!/bin/bash
# Builds a universal (Apple Silicon + Intel), ad-hoc signed Rewind.app and zips it into ./dist.
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${1:-$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' Resources/Info.plist)}"

# Map the build folder to "." so no local paths (and so no username) end up in the binary.
FLAGS=(-c release --arch arm64 --arch x86_64 -Xswiftc -file-prefix-map -Xswiftc "$PWD=.")
swift build "${FLAGS[@]}"
BIN="$(swift build "${FLAGS[@]}" --show-bin-path)/Rewind"

APP=dist/Rewind.app
rm -rf dist && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Rewind"
strip -S -x "$APP/Contents/MacOS/Rewind" # drop debug symbols, which also hold build paths
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
codesign --force --sign - "$APP"

ditto -c -k --keepParent "$APP" "dist/Rewind-$VERSION-macOS.zip"
echo "dist/Rewind-$VERSION-macOS.zip"
