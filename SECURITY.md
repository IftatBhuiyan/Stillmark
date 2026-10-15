# Security

Report vulnerabilities privately through this repository's **Security → Report a vulnerability**
button. Do not include credentials, private captures, or customer data in a public issue.
If private reporting is unavailable, contact the maintainer through their GitHub profile.

Security fixes target the latest supported release. Preview builds may have incomplete features;
check release notes before relying on them for sensitive work. Never assume blur is irreversible
redaction: use solid redaction and inspect the exported image.

Releases are built from protected source, include SHA-256 checksums, and are downloadable from
GitHub Releases. The initial community builds are unsigned on Windows and ad-hoc signed on Mac,
without Apple notarization. Checksums detect download corruption; they do not establish publisher
identity. Never disable operating-system security controls to install a build you cannot verify.

Maintainers must not add secrets to source, expose tokens to fork PRs, auto-run untrusted PR code
with write permissions, or reuse the retired commercial licensing and update infrastructure.
