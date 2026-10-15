# WinShot packaged startup bootstrap

`WinShot.Startup.exe` is the dormant, package-only executable intended for a future
`windows.startupTask` manifest extension. It accepts no arguments and, only when Windows confirms
that it is a direct child of the immutable package install root, starts the fixed sibling
`WinShot.exe` with the exact background argument `--tray`.

It is not the unpackaged stable launcher, a service, a watchdog, an updater, or a general-purpose
process launcher. It does not use settings, the registry, networking, shell execution, persistent
logs, or visible UI. The future signed package manifest, package identity, publisher, task ID,
display name, enabled default, and distribution channel remain owner-gated.
