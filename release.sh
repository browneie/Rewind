#!/bin/bash
# Builds a universal (Apple Silicon + Intel) Rewind.app signed with the "Rewind Release" certificate and puts
# dist/Rewind-<version>-macOS.dmg in ./dist. The same .dmg is what new users open and what the in-app updater
# downloads; the updater only installs apps signed with the same certificate as the copy that's running.
# Needs the certificate in the login keychain (override with SIGN_IDENTITY).
set -euo pipefail
cd "$(dirname "$0")"
VERSION="${1:-$(/usr/libexec/PlistBuddy -c 'Print CFBundleShortVersionString' Resources/Info.plist)}"
IDENTITY="${SIGN_IDENTITY:-Rewind Release}"

security find-identity -p codesigning | grep -q "\"$IDENTITY\"" || { echo "Signing identity \"$IDENTITY\" not found in the keychain." >&2; exit 1; }

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
codesign --force --sign "$IDENTITY" "$APP"
codesign --verify --deep --strict "$APP"

# Disk image: Rewind.app next to an Applications shortcut
STAGE=dist/dmg
mkdir -p "$STAGE" && cp -R "$APP" "$STAGE/" && ln -s /Applications "$STAGE/Applications"
hdiutil create -volname "Rewind" -srcfolder "$STAGE" -ov -format UDZO "dist/Rewind-$VERSION-macOS.dmg" >/dev/null
rm -rf "$STAGE"
codesign --force --sign "$IDENTITY" "dist/Rewind-$VERSION-macOS.dmg"

ls -1 dist/Rewind-"$VERSION"-macOS.*
