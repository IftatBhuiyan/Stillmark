# Contributing to Stillmark

Stillmark welcomes bug reports, focused pull requests, documentation, translations, and forks.
Use GPL-3.0-only for contributions; third-party notices must remain intact. No CLA is required.
You may create and distribute a GPL-compliant spin-off. Give a fork its own name and identity so
people can distinguish it from official Stillmark releases. This does not restrict your GPL rights.

Discuss substantial features in an issue before building them. For a bug, include the platform,
app version, reproduction steps, expected behavior, and actual behavior. Redact personal capture
content and logs. Do not upload license keys, customer data, or account tokens.

## Development

- Mac: `swift build --package-path mac`. Package a universal app with `bash scripts/build-mac.sh`.
- Windows: install the SDK pinned by `app/global.json`, then run `dotnet restore WinShot.sln --locked-mode`,
  `dotnet build WinShot.sln -c Release --no-restore`, and `dotnet test WinShot.sln -c Release --no-build` from `app/`.
- Website: static HTML, CSS, and JavaScript; run its local validation command documented in `website/README.md`.

Keep changes narrow and avoid new dependencies unless they solve a concrete problem. Keep the
existing lockfiles. Screenshots and recordings stay local by default. Explain the change,
verification, and remaining limitations in your PR. App builds must not require a paid license,
an account, or a donation.

The Mac edition is a new preview, not a feature-parity claim. Verify display scaling,
multi-monitor behavior, cancellation, permission denial, recording finalization, redaction export,
and unsaved edits before changing those paths. A successful compile alone is not runtime proof.
