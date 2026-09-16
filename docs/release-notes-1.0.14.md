# Hosts Manager 1.0.14

This patch improves backup isolation and the safety of saving and restoring hosts files.

- Separate backup history for each target file, including when targets share a backup root.
- Preserve and verify Windows access rules before replacement, after writing and during recovery.
- Independently protect Docker and Tailscale managed sections during ordinary saves.
- Coordinate overlapping Hosts Manager writes and detect late external changes.
- Replace elevation request files with authenticated local IPC; backup operations retain the
  initiating user's identity when elevation uses another administrator account.
- Verify backup provenance and content before restore, and report recovery outcomes accurately.

## Existing backups

New backups live beneath `backups/targets/<target-key>`. Older backups remain in the root
and are available through **Earlier backups** for inspection and intentional import.
They are not automatically assigned to a target. Original means the first capture in the
new target-specific history.

## Validation

204 automated tests passed, including Windows permission checks, competing writer processes
and authenticated pipe save/restore. The Release solution build passed with no warnings or
errors. The user also exercised the fixture application successfully.

Real alternate-account UAC remains a manual acceptance check. Coordination only covers
Hosts Manager processes; unrelated editors can still race the final replacement.

## Downloads

Choose the architecture matching your PC: x64, x86 or ARM64. **Setup** installs the app;
**Portable** runs without installation. Both include the .NET runtime.
