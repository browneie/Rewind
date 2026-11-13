#!/bin/bash
# Builds a universal (Apple Silicon + Intel), ad-hoc signed Rewind.app and zips it into ./dist.
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${1:-$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' Resources/Info.plist)}"

swift build -c release --arch arm64 --arch x86_64
BIN="$(swift build -c release --arch arm64 --arch x86_64 --show-bin-path)/Rewind"

APP=dist/Rewind.app
rm -rf dist && mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Rewind"
cp Resources/Info.plist "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
codesign --force --sign - "$APP"

ditto -c -k --keepParent "$APP" "dist/Rewind-$VERSION-macOS.zip"
echo "dist/Rewind-$VERSION-macOS.zip"
