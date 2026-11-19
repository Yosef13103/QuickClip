# QuickClip Personal v1.2.1 — Reliable updates and recovery

Maintenance release focused on installed Personal updates.

- Clearer distinction between automatic installation and manual/portable download flows, including reasons and the verified installer location.
- Stronger installation inventory and rollback checks; interrupted or incomplete recovery keeps evidence and refuses to reopen an unverified mixed installation.
- Recovery verifies restored contents and uninstall registration. Unknown files are preserved for safe manual resolution.
- Detect an unsuccessful restart and retain the update backup for troubleshooting.

Installed v1.2.0-to-v1.2.1 upgrades were exercised on disposable Windows runners using the published v1.2.0 installer and embedded worker, at the default location and an alternate drive path. The release gate remains separate from production publication approval.

Download QuickClip-Personal-1.2.1-setup.exe for installation, or QuickClip-Personal-1.2.1-win-x64.zip for portable use. Verify SHA256SUMS.txt. Portable copies continue to use manual updates.