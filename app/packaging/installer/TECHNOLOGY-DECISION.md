# Installer and updater technology decision

Evidence snapshot: 2026-07-29 00:16 UTC (2026-07-28 EDT)

## Decision

Use **Velopack 1.2.0 as the primary implementation candidate** and **WiX Toolset 7.0.0 as the
fallback**. This is a credential-free technology recommendation, not production approval or
permission to acquire a package: no dependency has been installed, the checked contract remains
`UNSET`, and no installer or updater exists yet.

Implementation update, 2026-07-28 EDT: a package-independent unsigned engineering launcher now
implements the checked schema-2 custom topology—stable root GUI executable, exact atomic
`active-version`, and immutable `versions\{Version}` payloads. It does not install, update,
roll back, or uninstall anything and it refuses unsigned payloads through its production trust
path. This resolves the custom-launcher implementation gap without selecting either candidate.
It also makes the topology decision explicit: adopting Velopack's own root stub/replaceable
`current` model now requires an owner-approved contract revision and revalidation rather than
shipping two competing stable launchers. WiX/custom installer work can consume the checked
versioned layout but must still implement activation health, pointer switch/restore, ownership,
signing, and clean-VM gates.

At the dated evidence snapshot, Velopack `1.2.0` and `vpk` `1.2.0` were the latest stable releases,
published 2026-06-03 and 55 complete days old. WiX Toolset `7.0.0` was the latest stable release,
published 2026-04-06 and 113 complete days old. Newer Velopack `1.2.110-*` and WiX `8.0.0-build.*`
artifacts were prereleases and are not approved candidates. The repository does not define a
numeric `minimumReleaseAge`; an owner or environment policy may exist outside the repository and
must be confirmed before any acquisition. These ages therefore prove eligibility only when that
actual policy is no greater than 55 days for Velopack or 113 days for WiX.

Velopack best matches the resident, per-user application:

- its Windows package installs without elevation under `%LocalAppData%\{packId}`;
- a root execution stub provides a stable shortcut and sign-in-launch target while `current` is
  replaced on update;
- it provides full/delta packages, channels, update download/apply APIs, setup/updater command-line
  automation, and Azure Artifact Signing integration; and
- release `1.2.0` was the current stable reviewed candidate from the same maintainer for both the
  `Velopack` runtime and `vpk` packaging tool at the dated evidence snapshot. The active repository
  and recent stable release are maintenance indicators, but no formal consumer support lifecycle,
  security-response SLA, or repository `SECURITY.md` was found.

Official evidence:

- [Windows layout and stable execution stub](https://docs.velopack.io/packaging/operating-systems/windows)
- [Application integration and explicit update apply](https://docs.velopack.io/integrating/overview)
- [Release channels](https://docs.velopack.io/packaging/channels)
- [Code signing and Azure Artifact Signing](https://docs.velopack.io/packaging/signing)
- [Velopack 1.2.0 package provenance](https://www.nuget.org/packages/Velopack/1.2.0)
- [vpk 1.2.0 package provenance](https://www.nuget.org/packages/vpk/1.2.0)
- [Velopack 1.2.0 release](https://github.com/velopack/velopack/releases/tag/1.2.0)
- [Velopack WPF startup integration](https://docs.velopack.io/getting-started/wpf)
- [NuGet repository-signature resource](https://learn.microsoft.com/en-us/nuget/api/repository-signatures-resource)
- [NuGet signed-package verification](https://learn.microsoft.com/en-us/dotnet/core/tools/nuget-signed-package-verification)

### Supply-chain and security evidence

NuGet catalog metadata binds `Velopack`/`vpk` `1.2.0` to upstream source commit
`f2edcbcafb81da5b3c884aaea330e225ad91d8b6`. NuGet.org reported repository signing for all hosted
packages at review time; that protects repository integrity, not maintainer identity. An approved
trial must acquire only the exact pinned artifacts, compare their catalog SHA-512 values, run
`dotnet nuget verify --all`, and inventory licenses, native tools, and embedded components before
acceptance. This is especially important for the approximately 124.5 MiB `vpk` tool package even
though its NuGet metadata declares no package dependencies.

No advisory matching `Velopack` or `vpk` `1.2.0` was published in the official GitHub advisory or
NuGet metadata queried at the snapshot. This is evidence of no published match, not proof of no
vulnerability, and does not replace inspection of `vpk`'s embedded tools.

## Mandatory gates before adding Velopack

1. Confirm the owner/environment's actual numeric `minimumReleaseAge`; the repository and
   `NuGet.config` do not define one. Version `1.2.0` was published 2026-06-03 and was 55 complete
   days old at the dated evidence snapshot. Proceed only if the actual gate is at most 55 days, or
   after a documented package-specific owner exception; do not override it silently and do not use
   the newer prerelease.
2. Pin the runtime and locally manifested tool to exactly the same reviewed `1.2.0` version. Review
   the resulting lock graph, licenses, hashes, and transitive content before accepting it.
3. Use an installer package ID whose owned installation root is distinct from the existing
   `%LocalAppData%\WinShot` data root. Velopack removes its package root on uninstall; settings,
   history, protected licensing state, and user output must remain outside that root. The checked
   contract now refuses a package/data-directory identity collision and declares both roaming and
   local data roots explicitly.
4. Reconcile the contract's `versions\{Version}` and post-health-check rollback wording with
   Velopack's root-stub/`current` replacement model. Keep signed higher-version forward-fix as the
   customer recovery policy unless a tested health rollback is implemented.
5. Add an explicit WPF `Main`/`StartupObject` integration so Velopack initializes first and exactly
   once, but disable its default auto-apply-on-startup behavior. Do not call an apply path that can
   kill WinShot after a timeout or allow updater-driven lock-holder termination. Check/download may
   run in the background; apply/restart may occur only after WinShot's entitlement, exact-candidate,
   authenticated `Ready -> Commit -> CommitAccepted`, and active-capture/recording/transcode policies
   approve it. Prove these refusal paths in the isolated trial.
6. Target the root execution stub from shortcuts and WinShot's owned HKCU Run value. Do not use an
   installer-created Startup shortcut and never infer consent from a missing startup registration.
7. Preserve the WinShot-owned update-candidate verifier: a strict detached RSA-PSS/SHA-256
   descriptor binds package and release-manifest hashes, exact identity/channel/architecture/paid
   major, staging inventory, and a short-lived single-use handoff capability. Production must add
   the Windows trust-provider adapter for exact publisher and RFC 3161 evidence before the
   capability can be created. Velopack's size/hash behavior alone does not prove this stronger
   checked contract. The current verifier accepts only a platform-authenticated production EXE and
   rejects `.nupkg`; integration must define a separate bounded archive trust/extraction procedure
   and validate the expected publisher-signed PE payloads before activation.
8. Keep VC++ recording support optional. Do not make Velopack's framework bootstrap block the
   screenshot application; any official redistributable action needs explicit disclosure/consent,
   exact source/hash/signer validation, and refusal must leave only recording unavailable.
9. Implement the checked ownership map exactly: remove clipboard backing, logs, and crash-loop
   state on normal uninstall; retain settings, license/trial state, managed History, and recording
   recovery workspaces; never traverse user-selected output; and make complete private-data
   removal a separate unchecked choice with recording-recovery and license-deactivation warnings.
   Startup removal must match the installer-recorded command, and trial cleanup may remove only
   its owned value plus an otherwise-empty key.
10. Prove standard-user install, first run, sign-in launch with enabled/disabled preference,
    background update, locked/interrupted update, active-work deferral, restart, signed
    forward-fix, default uninstall retention, explicit private-data removal, and user-output
    preservation on clean Windows VMs.
11. A non-production trial may use a clearly synthetic package ID only after explicit owner
    approval. The synthetic package root must be collision-checked against both application data
    roots, must never be published or signed as a customer artifact, and must be removed after the
    isolated evidence run. Final identity, signing, channel, legal, and public-distribution approval
    remain separate mandatory gates.

This document does not authorize package/SDK acquisition. Production integration requires the
final product/package/publisher identity and every relevant checked approval. Before final identity,
a credential-free isolated worktree trial is permitted only after the owner explicitly approves the
synthetic package ID and confirms that the environment's numeric `minimumReleaseAge` is satisfied.

## Fallback

Use WiX Toolset 7.0.0 if the Velopack layout or recovery model cannot satisfy the accepted
contract. At the dated evidence snapshot, 7.0.0 was the latest stable version, published 2026-04-06
and 113 complete days old. WiX has a documented annual-major cadence and current/previous-major
security-maintenance policy, which is a stronger formal maintenance signal than Velopack's current
activity evidence.

WiX can author an exact per-user MSI/Burn layout and prerequisite UX, but it does not provide
WinShot's update feed/client, stable forwarding launcher, authenticated application-owned handoff,
or post-launch health rollback. Major-upgrade scheduling must be selected deliberately: the default
`afterInstallValidate` schedule can leave neither version installed when an upgrade fails, while
`afterInstallInitialize` permits transactional rollback of the earlier removal. Neither supplies
WinShot's required post-launch health decision. Burn signing also requires the documented
extract/sign-engine, reattach, and final-sign sequence.

NuGet catalog metadata binds `WixToolset.Sdk`/`wix` `7.0.0` to upstream source commit
`b8977d6f88e7b68e000bac226a2814f236770570`. The official advisories
[GHSA-jx4p-m4wm-vvjg](https://github.com/wixtoolset/issues/security/advisories/GHSA-jx4p-m4wm-vvjg),
[GHSA-rf39-3f98-xr7r](https://github.com/wixtoolset/issues/security/advisories/GHSA-rf39-3f98-xr7r),
and [GHSA-7wh2-wxc7-9ph5](https://github.com/wixtoolset/issues/security/advisories/GHSA-7wh2-wxc7-9ph5)
affect older WiX 3/4 ranges and do not include 7.0.0. This does not waive clean-VM tests or review
of custom actions, `RemoveFoldersEx`, and Burn redirection behavior.

WiX 7 binary releases require explicit OSMF review and acceptance. The checked agreement applies a
maintenance fee to revenue-generating users with annual gross revenue of at least US$10,000, while
users below that threshold or paying separate support/maintenance fees are exempt; source and
self-compiled binaries remain under the OSI license terms. This is implementation evidence, not
legal advice. Do not acquire or adopt the binary fallback until the owner accepts the current terms.

- [WiX release notes](https://docs.firegiant.com/wix/whatsnew/releasenotes/)
- [Per-user package scope](https://docs.firegiant.com/wix/schema/wxs/packagescopetype/)
- [Major-upgrade rollback](https://docs.firegiant.com/wix/schema/wxs/majorupgrade/)
- [Burn bundles](https://docs.firegiant.com/wix/tools/burn/)
- [WiX signing](https://docs.firegiant.com/wix/tools/signing/)
- [WiX licensing and lifecycle](https://docs.firegiant.com/wix/)
- [WiX 7.0.0 release](https://github.com/wixtoolset/wix/releases/tag/v7.0.0)
- [WiX Toolset SDK 7.0.0 provenance](https://www.nuget.org/packages/WixToolset.Sdk/7.0.0)
- [WiX CLI tool 7.0.0 provenance](https://www.nuget.org/packages/wix/7.0.0)
- [WiX 7 binary OSMF agreement](https://raw.githubusercontent.com/wixtoolset/wix/v7.0.0/OSMFEULA.txt)

## Rejected for the current contract

- **MSIX/App Installer:** strong OS-native signing/update enforcement, but packaged startup tasks,
  system-owned physical paths, user-controlled Task Manager opt-out, update timing, and data
  retention would require a different startup/install/update contract.
- **Inno Setup:** viable installer-only contingency, but no integrated updater or post-health
  transactional recovery. It was not re-evaluated in the 2026-07-29 UTC evidence refresh and
  remains unapproved pending the same current version, release-age, security, provenance, and
  commercial-terms review required of any candidate.
- **Squirrel.Windows:** rejected as stale and seeking maintainers; Velopack is the supported
  successor/migration path.

## Decisions that still require the owner

- the numeric owner/environment `minimumReleaseAge` and whether it is no greater than 55 days;
- explicit approval or refusal of a credential-free isolated Velopack trial using a clearly
  synthetic, collision-checked package ID before final identity is available;
- cleared product name, final package ID, startup value name, and legal publisher;
- acceptance of Velopack's root-stub/`current` topology and an adapter that disables auto-apply and
  uncontrolled kill paths, or funding the more custom WiX architecture;
- archive/feed/extracted-PE trust, paid-major stable/beta channel names, HTTPS host, retention, and
  forward-fix versus tested post-launch health rollback policy;
- Azure Artifact Signing account/profile, exact publisher subject/subscriber EKU, and a maintained
  signing toolchain that can service and replace the bundled .NET 10 runtime;
- VC++ consent presentation and official redistributable URI/hash/signer values; and
- if WiX becomes necessary, acceptance of the current OSMF agreement and whether paid FireGiant
  support/maintenance is required.
