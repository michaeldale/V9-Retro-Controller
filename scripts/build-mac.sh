#!/usr/bin/env bash
# Builds "V9 Retro Controller.app" for macOS and zips it. Run on a Mac with the .NET 10 SDK:
#
#   ./scripts/build-mac.sh            # Apple Silicon (osx-arm64)
#   ./scripts/build-mac.sh osx-x64    # Intel
#
# The bundle is ad-hoc signed, which Apple Silicon requires to run at all. It is not notarized,
# so the first launch on another Mac needs right-click > Open (or: xattr -dr com.apple.quarantine).
# For distribution, sign with a Developer ID and notarize instead (see SIGN_IDENTITY below).
set -euo pipefail

RID="${1:-osx-arm64}"
SIGN_IDENTITY="${SIGN_IDENTITY:--}"   # "-" = ad-hoc; or "Developer ID Application: Name (TEAMID)"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
OUT="$ROOT/artifacts/publish/$RID"
DIST="$ROOT/dist"
APP="$DIST/V9 Retro Controller.app"

case "$RID" in osx-arm64|osx-x64) ;; *) echo "runtime must be osx-arm64 or osx-x64" >&2; exit 2 ;; esac
command -v dotnet >/dev/null || { echo "install the .NET 10 SDK first (https://dot.net)" >&2; exit 1; }

rm -rf "$OUT" "$APP"
mkdir -p "$DIST"

dotnet publish "$ROOT/src/V9Retro.App/V9Retro.App.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:UseAppHost=true -p:DebugType=none \
  -o "$OUT"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/V9RetroController"

# Icon: AppIcon.png (1024 px) -> AppIcon.icns
ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
SRC_ICON="$ROOT/src/V9Retro.App/Assets/AppIcon.png"
for size in 16 32 128 256 512; do
  sips -z $size $size "$SRC_ICON" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
  sips -z $((size * 2)) $((size * 2)) "$SRC_ICON" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>V9 Retro Controller</string>
  <key>CFBundleDisplayName</key><string>V9 Retro Controller</string>
  <key>CFBundleIdentifier</key><string>au.com.michaeldale.v9retrocontroller</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleExecutable</key><string>V9RetroController</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.developer-tools</string>
  <key>NSLocalNetworkUsageDescription</key>
  <string>V9 Retro Controller connects to v9x agents and to a V9 Retro Controller host on your local network.</string>
  <key>NSBonjourServices</key>
  <array><string>_v9xremote._tcp</string></array>
</dict>
</plist>
PLIST

# Sign every native library and the main binary, then the bundle (ad-hoc unless SIGN_IDENTITY is set).
find "$APP/Contents/MacOS" -type f \( -name '*.dylib' -o -name 'V9RetroController' -o -name 'createdump' \) -print0 |
  xargs -0 codesign --force --timestamp=none --sign "$SIGN_IDENTITY"
codesign --force --timestamp=none --sign "$SIGN_IDENTITY" "$APP"
codesign --verify --verbose=1 "$APP"

# Release package name the update server expects: <app>-<version>-<family>.zip
ZIP="$DIST/v9-retro-controller-$VERSION-$RID.zip"
rm -f "$ZIP"
ditto -c -k --sequesterRsrc --keepParent "$APP" "$ZIP"
echo "Built $APP"
echo "Zipped $ZIP"
