# Stillmark

Free, local-first screenshots and screen recordings for Windows, with a native Mac preview.
Made by Iftat Bhuiyan at Eclipse Studios. Licensed under [GNU GPLv3](LICENSE).

[Downloads](https://github.com/IftatBhuiyan/Stillmark/releases) · [Contribute](CONTRIBUTING.md) · [Support](SUPPORT.md) · [Security](SECURITY.md)

No account, activation key, subscription, or donation is required. Capture files stay on your computer.
The project welcomes pull requests and GPL-compliant forks and spin-offs.

## Platforms

| | Windows | macOS preview |
|---|---|---|
| Requirements | Windows 10/11 x64 | macOS 15+, Apple silicon or Intel |
| Capture | Area, window, screen, scrolling | Area, window, screen |
| Editing | Existing Windows editor | Arrows, boxes, text, highlights, solid redaction, crop |
| Recording | Existing Windows recorder | ScreenCaptureKit, H.264 MP4, up to 4K, optional audio |
| Other tools | Local history, pin, OCR, shortcuts | Local history, pin, Apple Vision OCR, shortcuts |

The Mac edition is an early implementation, not feature parity or an established CleanShot X replacement.
Scrolling capture, advanced effects, and broader Mac runtime/performance validation remain open work.
Both editions are free; old commercial activation and account calls have been removed from the app host.
The old signed update feed is disabled. Download updates manually from this repository's Releases.

## Download

Use the assets on [GitHub Releases](https://github.com/IftatBhuiyan/Stillmark/releases).
Windows ZIPs are portable and self-contained: extract the folder and run `WinShot.exe`.
Mac ZIPs contain `Stillmark.app`. Initial community binaries are unsigned on Windows and ad-hoc
signed on Mac, without Apple notarization. Operating systems may block them. Follow your system
security policy or build from source. SHA-256 checksums accompany each asset.

## Build

### Mac

With Xcode command-line tools installed:

```sh
swift build --package-path mac
bash scripts/build-mac.sh
```

The packaging script creates an arm64/x86_64 universal app and ZIP in `dist/`.
The app saves captures in `~/Pictures/Stillmark`. It requests screen-recording permission for capture
and microphone permission only when microphone recording is enabled. Audio defaults to off.
Global capture shortcuts are Command-Shift-2 (area), 3 (screen), 4 (window), and 5 (record/stop).
Shortcuts already owned by another app may be unavailable.

### Windows

Use the .NET SDK pinned in `app/global.json`. From `app/`:

```powershell
dotnet restore WinShot.sln --locked-mode
dotnet build WinShot.sln --configuration Release --no-restore
dotnet test WinShot.sln --configuration Release --no-build
```

From the repository root, `./scripts/build-windows.ps1` produces the portable app and checksum.
Windows runtime behavior must be checked on Windows; cross-platform compilation is not runtime proof.

### Website

The free-software site lives in `website/`. Run `npm ci --ignore-scripts` and `npm run build`
from that folder. Deploy only `dist/site`, never the monorepo root.
GitHub Pages can host it without a paid application backend. The proposed branded address is
`stillmark.eclipsestudios.io`, pending verification of domain control and DNS configuration.

## Community and funding

Read [CONTRIBUTING.md](CONTRIBUTING.md) for pull requests and forks. Report bugs in Issues and ask
questions in Discussions. Security vulnerabilities belong in private reports, not public issues.
Support is best effort. GitHub Sponsors enrollment is pending; there is no donation checkout yet.

## Migration and attribution

The Windows source starts from commit `b9b5aa0f0e9659a9b24ef156f46ad98383f22ec9` and website source
from `b459448536f968ea60c89bc65fc85c6f40d52c4a`. Private Git history, credentials, customer records,
production private keys, and assistant/handoff files are excluded. Original backups remain private.
Retired backend source is preserved under `legacy/`; it is not included in the static-site output.
Historical licensing libraries/tools remain for compatibility tests and prior-customer migration;
new app-host paths do not use them. Do not deploy the retired commerce service for new sales.

Third-party packages retain their own terms. See [THIRD-PARTY-NOTICES](app/THIRD-PARTY-NOTICES.md).
The new Mac implementation uses Apple system frameworks without third-party dependencies.

Copyright (C) 2026 Iftat Bhuiyan. Project code: GPL-3.0-only.
