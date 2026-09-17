# Hosts Manager 1.0.15

- Edit an existing mapping's address, hostnames and comment with F2 or Edit, preserving its
  group, position and enabled state. Docker and Tailscale mappings remain protected.
- Manage retained isolated-browser data from **More actions → Browser preview data**.
  Inspect profile mappings, launch options, timestamps and disk usage, and delete a selected
  profile after closing all processes for that browser. Older profiles show dates and size with
  mappings marked as unknown until reused. Normal browser profiles are not deleted.
- Improved control names, keyboard focus, muted-text contrast and search/edit shortcuts.
- Pinned build tooling and dependency locks, architecture packaging in CI, and downloadable
  SHA-256 checksums. Release assets are verified before publication.

Validation: 220 automated tests passed; desktop build and x64/x86/ARM64 packaging succeeded.
Full screen-reader/DPI/tray testing and install/upgrade/uninstall across architectures remain
unverified. See `docs/a15-a18-implementation.md` and `docs/release-validation.md` in this tag.

Choose the Setup MSI for installation or Portable EXE for standalone use, matching your CPU.
`SHA256SUMS.txt` covers all six downloads.
