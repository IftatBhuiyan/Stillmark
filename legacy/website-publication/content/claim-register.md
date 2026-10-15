# Storefront Claim Register

Last reviewed: 2026-08-03

This register governs factual claims for the storefront using the owner-selected public name
`Relic Screenshot`. It is an internal editorial control, not public policy text. The desktop repository
at `../WinShot/` is the source of product truth; a signed release manifest and approved commercial
terms supersede source-only evidence for a public release.

## Status key

- **Preview-safe:** may be used in the public no-commerce preview with the listed qualification.
- **Release-gated:** implemented in source, but public wording waits for the specified manual,
  hardware, packaging, privacy, or signed-release evidence.
- **Owner input:** no approved value or policy exists; show an honest unavailable state or omit.
- **Do not claim:** contradicted, materially misleading, or unsupported.

Passing unit tests prove the tested logic, not an installed customer workflow. The most recent
integrated desktop evidence records 438/438 Release tests, a Release solution build with zero
warnings and zero errors, and byte-identical independently validated gate24/gate25 unsigned
engineering archives in `../WinShot/Documentation.md`. Manual and signed release gates remain open.

## Identity, platform, and offer

| ID | Candidate public claim | Status | Evidence | Required qualification or next proof |
|---|---|---|---|---|
| I-01 | The selected public product name is Relic Screenshot. | **Preview-safe** | Owner direction recorded in `Documentation.md`; `Prompt.md` | The name replaces WinShot across active public surfaces, but this status does not imply professional trademark clearance or authorize irreversible brand spend. |
| I-02 | This is a Windows desktop application. | **Preview-safe** | `../WinShot/Directory.Build.props`; `../WinShot/src/WinShot.App/WinShot.App.csproj` | Do not imply macOS, browser, mobile, or cross-platform availability. |
| I-03 | Requires 64-bit Windows. | **Release-gated** | App project sets `PlatformTarget` to `x64`. | Confirm the packaged artifact, clean install, and exact system-requirements wording. |
| I-04 | Supports Windows 10 and Windows 11. | **Release-gated** | App manifest declares Windows 10 awareness; target framework is `net8.0-windows10.0.19041.0`. | Resolve the documented mismatch between target/API floor, `SupportedOSPlatformVersion` 10.0.17763.0, and historical Windows 10 1903+ intent. Test the minimum and current supported releases. |
| I-05 | Version 1.0.0 is available now. | **Do not claim** | Version metadata exists in `Directory.Build.props` and `AppInfo.cs`. | Metadata is not a release. Publish a version only from the signed release manifest after installer validation. |
| I-06 | Includes a 14-day full-feature trial. | **Release-gated** | `TrialTracker.TrialDays = 14`; `LicenseStatus`; `LicenseWindow.xaml.cs`; licensing tests. | Confirm final offer/policy wording, first-run behavior in the packaged build, expiry boundary UI, and whether provider activation changes the trial flow. |
| I-07 | Buy once. | **Owner input** | Product prompt describes a planned paid one-time license; offline license code supports major-version entitlement. | Approve price, currency/tax display, edition, receipt, activation/device policy, update entitlement, refund terms, and checkout destination before using a purchase CTA. |
| I-08 | Includes lifetime or perpetual updates. | **Do not claim** | `LicenseService.IsUpdateEntitled` currently gates by entitled major; commercial terms are unapproved. | “One-time purchase” does not mean lifetime updates. Publish only the approved entitlement. |
| I-09 | Works on three devices / one device / unlimited devices. | **Do not claim** | `LicenseStorage` explicitly does not enforce a seat/device limit; online provider flow is unwired. | Founder must approve the device model and the chosen licensing implementation must enforce and test it. |
| I-10 | No account required. | **Do not claim** | Final checkout, order management, recovery, and activation architecture are undecided. | Do not promise an account model until the provider journey is approved and tested. |
| I-11 | No subscription. | **Owner input** | A one-time offer is planned but not configured. | May be published only after the actual provider product and legal terms match it. |

## Capture and delivery

| ID | Candidate public claim | Status | Evidence | Required qualification or next proof |
|---|---|---|---|---|
| C-01 | Capture a selected area. | **Preview-safe** | `CaptureController.StartAreaCapture`; selection overlay/session code. | Use a real sanitized capture visual before public release. |
| C-02 | Capture a window, the current screen, or all monitors. | **Preview-safe** | `CaptureController`; `WindowService`; `MonitorService`; tray actions for fullscreen and all monitors. | Avoid “pixel perfect” or mixed-DPI reliability claims until the release hardware matrix passes. |
| C-03 | Repeat the last selected area. | **Preview-safe** | `AppSettings.LastArea`; `CaptureController.CaptureLastArea`; tray and hotkey bindings. | The remembered area is a local setting and may need reselection after display-layout changes. |
| C-04 | Use a capture timer. | **Preview-safe** | `AppSettings.TimerSeconds`; timed capture methods in `CaptureController`; countdown UI. | Do not advertise timer choices not exposed by the final build. |
| C-05 | Extract text from a selected area. | **Preview-safe** | `OcrService`; `CaptureController.StartTextCapture` and `RecognizeAndCopy`. | State that OCR uses Windows’ installed language packs. Do not promise every language or perfect recognition. |
| C-06 | Copy captures to the clipboard or save them as files. | **Preview-safe** | `CaptureController.Deliver`; `ClipboardService`; `ImageSaveService`; settings matrix. | At least one delivery path remains enabled in settings. Avoid implying cloud sharing. |
| C-07 | Save screenshots as PNG or JPEG. | **Preview-safe** | `ImageFormatKind`; `ImageSaveService`. | Do not claim WebP, HEIF, SVG, PSD, or other formats. |
| C-08 | Drag, copy, save, open, annotate, or pin a recent capture from Quick Access. | **Preview-safe** | `QuickAccessWindow.cs`; `QuickAccessManager.cs`. | Quick Access is configurable; do not imply it is unavoidable or a cloud upload surface. |
| C-09 | Browse recent capture history. | **Preview-safe** | `HistoryService`; `HistoryWindow`. | History currently covers image captures; recording history is not equivalent. Locally managed unsaved copies are bounded to the most recent 40 entries. |
| C-10 | Capture a scrolling region. | **Release-gated** | `ScrollingCaptureController`; `ScrollStitcher`; stitcher tests. | Complete real-page/window, long-content, cancellation, mixed-DPI, and failure-path manual checks before marketing it as dependable. |
| C-11 | Hide desktop icons while capturing. | **Release-gated** | `AppSettings.HideDesktopIconsWhileCapturing`; `DesktopIconsService`; app wiring. | Manually verify restoration after success, cancellation, and failure. |
| C-12 | Add padding, backgrounds, rounded corners, or shadow to window captures. | **Release-gated** | `ImageBeautifier`; window-background settings; window delivery path. | Use truthful before/after media and validate transparent/JPEG output and DPI behavior. |
| C-13 | Capture protected, elevated, DRM, or every application surface. | **Do not claim** | Product non-goals explicitly reject this guarantee. | Document known Windows/UAC/DRM limitations in support content. |

## Annotation, pinning, and editing

| ID | Candidate public claim | Status | Evidence | Required qualification or next proof |
|---|---|---|---|---|
| E-01 | Add arrows, lines, rectangles, ellipses, freehand marks, highlights, text, and numbered steps. | **Preview-safe** | `Editor/Annotations.cs`; `EditorWindow.xaml`; editor command wiring. | Present only controls that are available in the release candidate. |
| E-02 | Blur, pixelate, spotlight, and crop parts of a capture. | **Preview-safe** | `ToolKind`; pixel filters; editor render/export paths. | Do not call redaction irreversible or security-grade without a dedicated export/recovery review. Prefer “obscure” to “securely redact.” |
| E-03 | Undo and redo edits. | **Preview-safe** | `EditorDocument`; `EditorDocumentTests`. | The stack retains a bounded history; do not promise unlimited undo. A manual 50+ action editor smoke test remains open. |
| E-04 | Copy or save the edited result. | **Preview-safe** | editor save/export and clipboard handlers. | Do not imply layered project-file persistence; export is rendered output. |
| E-05 | Pin a capture above other windows. | **Preview-safe** | `PinWindow`; Quick Access pin action. | Validate resizing, opacity, click-through, multi-monitor, and DPI behavior before making stronger claims. |
| E-06 | Automatically detects secrets and redacts them. | **Do not claim** | Smart redaction appears only as a future plan. | No public mention until implemented, tested, and privacy-reviewed. |

## Recording and media

| ID | Candidate public claim | Status | Evidence | Required qualification or next proof |
|---|---|---|---|---|
| R-01 | Record a selected Windows screen area to MP4. | **Release-gated** | `RecordingController`; ScreenRecorderLib integration; MP4 delivery flow. | Run packaged-build hardware checks across supported GPUs/displays and verify output, cancellation, failure recovery, and privacy-safe logs. |
| R-02 | Record an animated GIF. | **Release-gated** | recording-to-GIF flow; `GifExportService`; `GifFrameSchedule`; GIF tests. | Manual gate remains for a recording longer than 40 seconds and an induced conversion failure. Do not claim optimization, tiny files, or arbitrary duration. |
| R-03 | Record microphone and/or system audio. | **Release-gated** | recording settings and ScreenRecorderLib audio options. | Validate permissions, default/selected devices, missing devices, Bluetooth/headset changes, sync, and failure messaging. |
| R-04 | Pause, resume, restart, cancel, or delete a recording. | **Release-gated** | `RecordingBarWindow`; `RecordingController`. | Complete interactive race/startup and saved-file behavior tests. |
| R-05 | Show cursor, click highlights, keystrokes, a countdown, time, and dimmed outside area. | **Release-gated** | recording settings, overlay windows, and controller wiring. | Validate visibility, exclusion from output where intended, keyboard privacy disclosure, DPI, and accessibility. |
| R-06 | Trim, resize, and re-encode recordings. | **Release-gated** | `VideoEditorWindow`; `VideoTranscodeService`; trim-range tests. | Manual gate remains for a 10–45 second trim from a 60-second source. Publish supported output/codec details only from the final build. |
| R-07 | Webcam overlay, drawing while recording, Do Not Disturb, or notification suppression. | **Do not claim** | These appear as parity targets/future plans, not verified release implementation. | Omit until source, tests, UI, and privacy/accessibility evidence exist. |

## Tray, shortcuts, startup, and reliability

| ID | Candidate public claim | Status | Evidence | Required qualification or next proof |
|---|---|---|---|---|
| L-01 | Open capture actions from the system tray. | **Preview-safe** | `TrayIconController`; `App.xaml` explicit shutdown mode. | Public media must show the current tray menu, not a concept. |
| L-02 | Use configurable global shortcuts for capture and recording actions. | **Preview-safe** | `HotkeySettings`; `HotkeyService`; settings UI and tests. | Do not promise conflict-free registration; conflicts can occur with other apps. |
| L-03 | Closing the dashboard keeps the app available in the tray. | **Release-gated** | `ShutdownMode="OnExplicitShutdown"`; intentional tray Quit path. | Complete installed-build closure/tray/hotkey tests and accessibility validation. |
| L-04 | Launch at Windows sign-in. | **Release-gated** | `AppSettings.LaunchAtStartup`; `StartupService`; startup tests. | Current development registration can point into repository output. Public wording waits for a stable installed path, update repair, opt-out preservation, sign-in timing, and uninstall checks. |
| L-05 | Recovers shortcuts after sleep, unlock, and reconnect. | **Release-gated** | power/session handlers and bounded re-registration logic; startup/lifecycle evidence in desktop documentation. | Run repeated real sleep/resume, lock/unlock, console/remote reconnect tests on a packaged build. |
| L-06 | Never quits, never crashes, or is always running. | **Do not claim** | Windows Application Restart is registered, but no native desktop app can guarantee continuous execution. | Describe the intended tray workflow without absolutes. Reliability needs measured release evidence. |
| L-07 | Opens in under a specific number of milliseconds. | **Do not claim** | No reproducible performance measurement supports a launch/capture latency figure. | Measure before publishing any speed claim. |

## Licensing, privacy, and security

| ID | Candidate public claim | Status | Evidence | Required qualification or next proof |
|---|---|---|---|---|
| P-01 | Captures and edits are processed on the user’s PC. | **Release-gated** | Current capture, editor, OCR, recording, and file-delivery paths are local. | Re-audit the final binary and every network path. Explain separately that licensing may contact a provider. Do not collapse this into “the app never uses the internet.” |
| P-02 | OCR works offline. | **Preview-safe** | `OcrService` uses Windows’ built-in OCR and installed language packs. | Qualify language-pack availability; final privacy review must confirm no added cloud fallback. |
| P-03 | Recent-history and clipboard backing files stay local. | **Release-gated** | `HistoryService`; capture/editor clipboard folders; `Setup.md`. | Publish exact locations in support/privacy copy only after retention and deletion behavior is validated. Some configured folders may themselves be cloud-synced by the user. |
| P-04 | Relic Screenshot collects no analytics or telemetry. | **Release-gated** | No analytics/telemetry integration was found in current source; the only current network client is an unwired licensing provider client. | Re-scan the final packaged dependency graph and hosting scripts. State the scope precisely; support/provider/website data are separate processing surfaces. |
| P-05 | The app has zero network access. | **Do not claim** | A Lemon Squeezy client now exists in source, even though it is not wired into the current product. Final licensing/update behavior is undecided. | Describe actual final endpoints and data fields in the privacy policy. |
| P-06 | The locally stored license record is protected at rest for the current Windows user. | **Preview-safe** | `LicenseStorage` protects the record with Windows DPAPI in current-user scope and migrates legacy plaintext records in place. | Explain that this is current-user data protection, not a device or seat limit, and not a boundary against software running as that Windows user. A valid offline key can still be entered on another device. |
| P-07 | License keys are hardware locked or device-limited. | **Do not claim** | Current offline storage checks local machine identity but the same valid key can be entered elsewhere. | Requires approved provider/device model and tested online enforcement. |
| P-08 | Uses keyboard/desktop capture integration. | **Release-gated disclosure** | Global hotkeys, keystroke overlay, screen capture, and local machine/user fingerprinting are implemented. | Privacy/EULA/support disclosures must explain purposes, activation conditions, retained data, and user controls without alarmist or vague language. |
| P-09 | The installer and binaries are digitally signed. | **Do not claim** | Code signing, timestamping, signed installer, and provenance verification are still release blockers. | Publish publisher, signature, checksum, version, size, and immutable URL only from the accepted signed artifact. |
| P-10 | Secure, private, anonymous, or GDPR compliant. | **Do not claim** | No final policy, data map, provider contract, legal review, or compliance evidence exists. | Use specific, verifiable behavior instead of broad assurances. |
| P-11 | Cloud upload or share links are included. | **Do not claim** | No cloud/upload implementation exists in the current source. | File/clipboard delivery is not hosted sharing. |

## Commerce, support, and release

| ID | Candidate public claim | Status | Evidence | Required qualification or next proof |
|---|---|---|---|---|
| O-01 | Buy now. | **Owner input** | App purchase URL is a placeholder; no approved product/variant/checkout exists. | Keep purchase disabled/unavailable until provider test and production journeys pass. |
| O-02 | Download now. | **Do not claim** | Current website URL targets a nonexistent GitHub release; no signed installer manifest exists. | Use “Release in preparation” or equivalent status, never a dead or unsigned download. |
| O-03 | Manage or recover a license online. | **Owner input** | App has a generic provider order URL, but no approved store/account route or tested recovery journey. | Never collect license keys in static website code. Link only to the approved provider-hosted surface. |
| O-04 | Refunds within a stated period. | **Owner input** | No approved refund policy exists. | Founder and qualified legal/commercial review must approve jurisdiction, exclusions, process, and provider consistency. |
| O-05 | Email support with a response-time promise. | **Owner input** | No production support identity or service level exists. | Do not invent an address or response time. |
| O-06 | Latest signed release, size, checksum, or changelog. | **Owner input** | No public signed release exists. | Populate only from an immutable signed release manifest. |
| O-07 | Customer testimonials, ratings, counts, logos, or awards. | **Do not claim** | No attributable, permissioned evidence exists. | Product proof must carry the page until real consented evidence exists. |

## Required release evidence before changing a status

1. Professional clearance for the selected name, publisher/legal identity, production domain, and
   completed brand migration record.
2. Approved price, currency/tax language, edition, device/activation model, update entitlement,
   refund policy, support promise, privacy policy, terms, and EULA.
3. Approved Lemon Squeezy store/product/variant IDs and public checkout/order-management URLs, or
   an approved offline-key-pool alternative with an end-to-end fulfillment test.
4. Signed and timestamped installer/binaries, immutable download URL, version, release date, exact
   supported systems, byte size, SHA-256, release notes, clean install/update/rollback/uninstall
   evidence, and signature verification.
5. Packaged-build manual evidence for startup/tray behavior, power/session recovery, mixed DPI,
   multi-monitor capture, editor 50+ actions, long GIF conversion/failure preservation, media trim,
   audio/device paths, privacy-sensitive overlays, and failure logs.
6. Final source/binary/dependency scan for network destinations, telemetry, stored data, secrets,
   permissions, native payloads, and third-party notices.

Any future copy change that introduces a factual claim should add or update a row here before it is
published.
