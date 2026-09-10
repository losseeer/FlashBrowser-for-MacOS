#!/usr/bin/env bash
#
# bundle-mac.sh —  pack the dotnet publish output into a macOS .app bundle.
#
# Why this script:
#   Avalonia + CefGlue on macOS suffers from duplicate Objective-C class
#   registration (libAvaloniaNative.dylib vs libcef.dylib both implement
#   ExtensionDropdownHandler etc.) unless CEF's libcef.dylib is loaded as
#   part of a Cocoa/NSBundle, which only happens when the host binary lives
#   inside a real .app bundle.
#
#   Reference: https://ask.csdn.net/questions/9021614

set -euo pipefail

# ---- args ----
PUBLISH_DIR="${1:-./bin/Release/net8.0/osx-arm64/publish}"
APP_NAME="${APP_NAME:-FlashBrowserForMacOS}"
APP_BUNDLE="${APP_BUNDLE:-./dist-${APP_NAME}.app}"
APP_ID="${APP_ID:-io.github.losseeer.flashbrowserformacos}"
VERSION="${VERSION:-0.1.0}"

if [ ! -d "$PUBLISH_DIR" ]; then
    echo "ERROR: publish dir not found: $PUBLISH_DIR" >&2
    echo "Run: dotnet publish -c Release -r osx-arm64 -o <dir>" >&2
    exit 1
fi

if [ ! -f "$PUBLISH_DIR/$APP_NAME" ]; then
    echo "ERROR: expected launcher binary not found: $PUBLISH_DIR/$APP_NAME" >&2
    exit 1
fi

# ---- layout ----
# Overwrite in place instead of `rm -rf` (which trips bulk-delete safety
# guards). The publish dir is the complete output set, so copying over the top
# is sufficient for a rebuild. Set CLEAN=1 for a full wipe-and-rebuild.
if [ "${CLEAN:-0}" = "1" ]; then
    rm -rf "$APP_BUNDLE"
fi
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Frameworks"
mkdir -p "$APP_BUNDLE/Contents/Resources"

# ---- executable ----
cp -R "$PUBLISH_DIR/." "$APP_BUNDLE/Contents/MacOS/"

# ---- Info.plist ----
cat > "$APP_BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>${APP_NAME}</string>
    <key>CFBundleIdentifier</key>
    <string>${APP_ID}</string>
    <key>CFBundleName</key>
    <string>${APP_NAME}</string>
    <key>CFBundleDisplayName</key>
    <string>FlashBrowser for Mac</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSPrincipalClass</key>
    <string>NSApplication</string>
    <key>NSSupportsAutomaticGraphicsSwitching</key>
    <true/>
    <!-- Required so DevTools and downloads work in non-sandbox runs. -->
    <key>NSAppTransportSecurity</key>
    <dict>
        <key>NSAllowsArbitraryLoads</key>
        <true/>
    </dict>
</dict>
</plist>
EOF

# ---- PkgInfo ----
printf 'APPL????' > "$APP_BUNDLE/Contents/PkgInfo"

# ---- ad-hoc sign the inner executable ----
# Avoid Gatekeeper rejecting unsigned bundled binaries when launched from Finder.
codesign --force --deep --sign - "$APP_BUNDLE" || true

echo "✅ Bundled → $APP_BUNDLE"
echo "Launch with: open '$APP_BUNDLE'"
