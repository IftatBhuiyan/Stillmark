First free and open-source community preview of Stillmark.

- Windows: commercial activation and account refresh removed; session-safety checks retained.
- Mac: native SwiftUI/AppKit app with area/window/screen capture and a one-row annotation toolbar.
  Tools include shapes, arrows, text, pencil, counters, pixelation, highlights, solid redaction,
  spotlight, and crop, with background framing, zoom, undo/redo, clipboard, and PNG export.
- Mac workflow: local history, Apple Vision OCR, pinning, customizable global shortcuts, persistent
  capture/recording settings, and a floating Quick Access overlay with drag-out actions.
- Mac recording: ScreenCaptureKit H.264 MP4, floating elapsed-time/Stop controls, and local AVKit
  playback. Optional system audio and microphone capture are available but not live-verified.
- Editable `.stillmark` projects save source pixels and annotations. They retain original pixels
  beneath redactions: share a flattened PNG when concealing sensitive information.
- Website: free downloads, GPLv3 contribution paths, no checkout or account client.
- Source: GPL-3.0-only, public contribution/support/security guides, platform CI, and protected source.

Download `Stillmark-Windows-x64.zip` or `Stillmark-macOS-universal.zip`, plus its SHA-256 checksum.

Local verification: a real Mac screenshot and recording stop/save/open flow were exercised;
four export/redaction regression tests pass. Audio and multi-display behavior remain unverified.

Known limitations:

- This is a preview. Mac scrolling capture, region recording, GIF output, and richer annotation
  editing are incomplete. No claim of CleanShot X feature parity or full performance optimization is made.
- Windows binaries are unsigned. Mac binaries are ad-hoc signed, not notarized. OS security warnings
  are expected; follow your security policy and build from source if needed.
- Windows has not been verified live for this release. Automated builds and existing Windows tests
  do not substitute for interactive device validation. Check repository updates for known issues.
- Initial updates are manual through GitHub Releases. The old paid signing/update feed is disabled.
- No accounts, license keys, or donations are required. Donations are not enabled until Sponsors setup completes.
