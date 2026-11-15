#!/bin/bash
# Builds Rewind.app into ./build. Set SIGN_IDENTITY to override the signing identity.
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release

APP=build/Rewind.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/Rewind "$APP/Contents/MacOS/Rewind"
cp Resources/Info.plist "$APP/Contents/Info.plist"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

# A stable signing identity keeps the Screen Recording permission across rebuilds: the "Rewind Release"
# certificate if it's in the keychain (same as releases), else an Apple Development one.
# Ad-hoc signing ("-") works too, but macOS will ask for permission again after every build.
IDENTITY="${SIGN_IDENTITY:-$(security find-identity -p codesigning 2>/dev/null | grep -q '"Rewind Release"' && echo "Rewind Release" || true)}"
IDENTITY="${IDENTITY:-$(security find-identity -v -p codesigning 2>/dev/null | grep -m1 'Apple Development' | sed -E 's/.*"(.*)"/\1/' || true)}"
IDENTITY="${IDENTITY:--}"
codesign --force --sign "$IDENTITY" "$APP"
echo "Built $APP (signed with: $IDENTITY)"
