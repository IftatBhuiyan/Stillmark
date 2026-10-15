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
<key>CFBundleDisplayName</key><string>Stillmark</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>1.1.0</string>
<key>CFBundleVersion</key><string>1</string>
<key>LSMinimumSystemVersion</key><string>15.0</string>
<key>NSHighResolutionCapable</key><true/>
<key>NSMicrophoneUsageDescription</key><string>Stillmark records your microphone only when you enable microphone audio and start a recording.</string>
<key>NSScreenCaptureUsageDescription</key><string>Stillmark captures your screen only when you start a screenshot or recording. Captures stay on your Mac.</string>
<key>NSHumanReadableCopyright</key><string>Copyright © 2026 Iftat Bhuiyan. GPL-3.0-only.</string>
</dict></plist>
PLIST
cp LICENSE "$app/Contents/Resources/LICENSE"
# Ad-hoc signing gives the local bundle a consistent identity; it is not notarization.
codesign --force --sign - --identifier io.eclipsestudios.stillmark "$app"
codesign --verify --deep --strict "$app"
plutil -lint "$app/Contents/Info.plist"
lipo "$app/Contents/MacOS/Stillmark" -verify_arch arm64
lipo "$app/Contents/MacOS/Stillmark" -verify_arch x86_64
ditto -c -k --sequesterRsrc --keepParent "$app" "$output/Stillmark-macOS-universal.zip"
(cd "$output" && shasum -a 256 Stillmark-macOS-universal.zip > Stillmark-macOS-universal.zip.sha256)
echo "Built $output/Stillmark-macOS-universal.zip (ad-hoc signed, not notarized)"
