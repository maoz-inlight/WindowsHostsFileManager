# P1 implementation and validation

16 September 2026 · Branch: `codex/p1-reliability-fixes` · Base: `4e9338d`

The user selected all P1 items from [action items](action-items.md): A01, A02, A03,
A04 and A06. Their implementation is complete. Actual UAC integration using a
different administrator account remains a manual acceptance check.

| Item | Implemented behavior | Verification |
| --- | --- | --- |
| A01 | Canonical target paths get independent original, latest and retention histories beneath the configured backup root. Manifests record provenance. Legacy root files remain available for inspection and are not adopted automatically. | Target separation, equivalent paths, cross-target rejection, legacy retention, exclusive original creation and concurrent backup creation. |
| A02 | Windows DACL capture, application to the temporary file and verification are mandatory. Replacement uses strict metadata handling; fallback and recovery preserve the captured access rules. | Injected capture/application/final-verification failures and a real Windows fallback retaining a protected custom DACL. |
| A03 | Ordinary saves compare managed sections with the actual destination baseline. Explicit restore requires the exact bytes of a verified backup for the target and can deliberately change managed sections. | Docker/Tailscale changes, removal, delimiter changes, ordering and line endings; valid save and intentional restore. |
| A04 | A global per-target mutex covers the transaction. Backup creation is exclusive. Late hash checks precede replacement and fallback; recovery retains detected external content. | Competing writer processes, a child blocked by the existing transaction lock, simultaneous backups and external edits at preparation, fallback and readback. |
| A06 | An authenticated local pipe replaces temporary request/result files. Peer process identity, creation time and initiating user are checked. The helper restricts privileged writes to the system hosts target; backup I/O impersonates the initiating user. | Fixture save/restore through the actual helper source, wrong client PID, wrong process creation time, wrong target, malformed/truncated/oversized requests and simulated UAC cancellation. Actual alternate-account UAC remains unverified. |

## Completed checks

- Release test project: **204 passed, 0 failed, 0 skipped**, no build warnings.
- Release solution build: **0 warnings, 0 errors**, including the WPF application.
- Tests write disposable fixture files. They do not modify the live hosts file or create accounts.
- README and design documentation now describe target-specific recovery, permission checks,
  explicit restore policy, coordination and the elevation handoff.

The integration host links the production elevation code and refuses to operate outside
marked fixture directories or on the live system hosts path. It tests local IPC without
requesting elevation. This does not substitute for real credential-prompt testing.

## Remaining manual acceptance checks

On a disposable Windows VM, preserve the initial hosts bytes and DACL, then exercise:

1. Same-account administrator consent: save, restore Original and restore Latest.
2. Standard user entering another administrator's credentials: repeat those operations,
   confirming backups stay under the initiating user's configured root and remain readable.
3. Repeat with a custom backup root accessible to the initiating user, and with one that
   is inaccessible; the latter must refuse the write before replacing the target.
4. Cancel UAC and terminate a helper during handoff; confirm accurate failure reporting,
   retained recovery files where applicable, and no misleading success.

The user manually exercised the launched fixture application and reported that it works.
Live alternate-account UAC scenarios remain unverified. Release packaging is tracked in
the v1.0.14 release notes.

## Limits and scope

The mutex coordinates Hosts Manager processes only. Hash checks narrow the race with
unrelated writers but cannot eliminate it. Windows access-rule checks cover the DACL;
they do not assert preservation of owner, group or audit SACL. Canonical path identity
resolves existing links but does not merge distinct hard-link paths into one history.

Original means the first capture in the new target-specific history. Legacy backups must
be inspected before import or manual recovery. Missing or corrupt manifests cannot qualify
for automatic restore, and headless recovery does not create an Original before selection.

Recovery reporting and backup-integrity safeguards overlap A05 and A10 because they are
necessary for the selected P1 fixes. The remaining P2, scheduled and optional items have
not been selected or declared complete. The user approved merging and releasing this P1 batch.
