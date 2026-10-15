#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
output="${1:-dist}"
mkdir -p "$output"
output="$(cd "$output" && pwd)"
for arch in arm64 x86_64; do
  swift build --package-path mac -c release --triple "$arch-apple-macosx15.0" --scratch-path "mac/.build/$arch"
done
app="$output/Stillmark.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
iconset="$output/Stillmark.iconset"
mkdir -p "$iconset"
for points in 16 32 128 256 512; do
  sips -z "$points" "$points" app/src/WinShot.App/Assets/winshot-master.png --out "$iconset/icon_${points}x${points}.png" >/dev/null
  retina=$((points * 2))
  sips -z "$retina" "$retina" app/src/WinShot.App/Assets/winshot-master.png --out "$iconset/icon_${points}x${points}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/Stillmark.icns"
arm_bin="$(swift build --package-path mac -c release --triple arm64-apple-macosx15.0 --scratch-path mac/.build/arm64 --show-bin-path)/Stillmark"
intel_bin="$(swift build --package-path mac -c release --triple x86_64-apple-macosx15.0 --scratch-path mac/.build/x86_64 --show-bin-path)/Stillmark"
lipo -create "$arm_bin" "$intel_bin" -output "$app/Contents/MacOS/Stillmark"
cat > "$app/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleExecutable</key><string>Stillmark</string>
<key>CFBundleIdentifier</key><string>io.eclipsestudios.stillmark</string>
<key>CFBundleName</key><string>Stillmark</string>
<key>CFBundleIconFile</key><string>Stillmark.icns</string>
<key>CFBundleDisplayName</key><string>Stillmark</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>1.1.0</string>
<key>CFBundleVersion</key><string>1</string>
<key>CFBundleDocumentTypes</key><array><dict>
<key>CFBundleTypeName</key><string>Stillmark Project</string>
<key>CFBundleTypeRole</key><string>Editor</string>
<key>LSHandlerRank</key><string>Owner</string>
<key>LSItemContentTypes</key><array><string>io.eclipsestudios.stillmark.project</string></array>
</dict><dict><key>CFBundleTypeName</key><string>Image</string>
<key>CFBundleTypeRole</key><string>Editor</string><key>LSHandlerRank</key><string>Alternate</string>
<key>LSItemContentTypes</key><array><string>public.png</string><string>public.jpeg</string></array></dict></array>
<key>UTExportedTypeDeclarations</key><array><dict>
<key>UTTypeIdentifier</key><string>io.eclipsestudios.stillmark.project</string>
<key>UTTypeDescription</key><string>Stillmark Editable Project</string>
<key>UTTypeConformsTo</key><array><string>public.data</string></array>
<key>UTTypeTagSpecification</key><dict><key>public.filename-extension</key><array><string>stillmark</string></array></dict>
</dict></array>
<key>LSMinimumSystemVersion</key><string>15.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSMicrophoneUsageDescription</key><string>Stillmark records your microphone only when you enable microphone audio and start a recording.</string>
<key>NSScreenCaptureUsageDescription</key><string>Stillmark captures your screen only when you start a screenshot or recording. Captures stay on your Mac.</string>
<key>NSHumanReadableCopyright</key><string>Copyright © 2026 Iftat Bhuiyan. GPL-3.0-only.</string>
</dict></plist>
PLIST
cp LICENSE "$app/Contents/Resources/LICENSE"
signing_identity="${STILLMARK_SIGNING_IDENTITY:--}"
if [[ "$signing_identity" == "-" ]]; then
  codesign --force --sign - --identifier io.eclipsestudios.stillmark "$app"
else
  codesign --force --options runtime --timestamp --entitlements mac/Stillmark.entitlements \
    --sign "$signing_identity" --identifier io.eclipsestudios.stillmark "$app"
fi
codesign --verify --deep --strict "$app"
plutil -lint "$app/Contents/Info.plist"
lipo "$app/Contents/MacOS/Stillmark" -verify_arch arm64
lipo "$app/Contents/MacOS/Stillmark" -verify_arch x86_64
ditto -c -k --sequesterRsrc --keepParent "$app" "$output/Stillmark-macOS-universal.zip"
(cd "$output" && shasum -a 256 Stillmark-macOS-universal.zip > Stillmark-macOS-universal.zip.sha256)
echo "Built $output/Stillmark-macOS-universal.zip (signing identity: $signing_identity; notarization is a separate verification step)"
