# Hosts Manager — current state and improvement report

Reviewed 14 September 2026 · Source version **1.0.12** · Commit **b19c25e**

**Revision note — 16 September 2026:** This report retains its original build/test baseline. The corrected [addendum](C:/projects/HostsManager/docs/project-review-addendum-2026-09-14.md) records two further reproduced issues and qualifies the proposed remedies. The [action items](C:/projects/HostsManager/docs/action-items.md) are the consolidated selection checklist. The checkout is now version 1.0.13 at `4e9338d`; the affected core write and backup paths are unchanged, but the original 148-test result is not a fresh result for that version. No implementation items have been selected or completed.

## Assessment

Hosts Manager is a substantial Windows utility with a well-separated core, useful desktop features, and a strong foundation of regression tests. It already goes beyond a basic hosts editor: it preserves file formatting, protects recognized tool-owned sections, creates backups, detects external edits, supports groups and imports, and launches isolated browser previews.

**The next investment should be reliability and accurate feedback.** The normal workflows are well represented in tests, but some advertised safety guarantees are stronger than the implementation. Permission handling, overlapping writes, and elevation across different Windows accounts need attention before broader distribution. Several smaller, reproducible defects also undermine the information users rely on while editing.

This is a source review with local build and behavioral checks, not a certification of production safety. No application source was changed, and no checks wrote to the live Windows hosts file.

## Current state

| Area | Observed state |
| --- | --- |
| Product | Add, delete and toggle mappings; search and sorting; groups; import and merge; backups and restore; tray controls; isolated Edge/Chrome previews. |
| Architecture | Three projects: platform-neutral .NET 8 core, WPF desktop application, and xUnit core tests. Windows services are separated from parsing and document mutations. |
| File preservation | Original line text, individual line endings, UTF-8 BOM, and non-UTF-8 bytes are preserved through a line model. Saves perform rendering verification and encoding checks. |
| Write protection | Expected-content hash, backup-before-write, replacement, readback and rollback logic. Normal UI operation elevates only the write helper. Exceptions to these guarantees are detailed below. |
| Test baseline | **148 passed, 0 failed, 0 skipped** in Release on Windows ARM64 with SDK 8.0.424. |
| Desktop build | **Succeeded with 0 warnings and 0 errors** in Release. |
| Automation | A Windows GitHub Actions workflow defines tests, a desktop build and an x64 installer build. Its hosted execution status was not checked. |
| Distribution | WiX scripts produce versioned MSI and portable builds for x64, x86 and ARM64. Release tooling checks branch and working-tree state before building and publishing. |
| Documentation | Extensive README, design notes and decision history. Some guarantees need qualification, and the checked-in screenshot predates the current groups and split-elevation UI. |

The existing tests provide useful evidence for byte-preserving parsing, toggles across saves, import/merge, group operations, validation, drift detection before saving, backup retention and ordinary restore behavior. The tests reference the core project only; they do not exercise the desktop view models, UAC helper, tray interactions, browser lifecycle or actual MSI installation.

## Findings, in priority order

**P1** means address before broader deployment because the issue concerns file safety or blocks an important Windows configuration. **P2** means a meaningful correctness or recovery issue for an upcoming maintenance release. “Reproduced” refers to isolated local probes; “source-confirmed” describes a definite code path whose operating-system trigger was not exercised.

### 1. P1 — Permission preservation can silently fail in the fallback

**Evidence: source-confirmed; ACL failure was not injected.**

If `File.Replace` fails, `MovePreservingPermissions` tries to read the destination access rules before moving the replacement. However, `TryReadAccessRules` catches access and I/O failures and returns null. The move still proceeds, and the method returns successfully when the captured rules are null. Consequently, a reported successful save can leave the destination with the temporary file's inherited permissions.

This contradicts the README's assurance that permission-preservation failures are reported. It is conditional on both replacement failure and inability to read the original ACL, but the consequence matters for a system file.

**Improve:** On Windows, refuse the move fallback unless the original permissions were captured. Preserve them through the recovery path and verify the final security descriptor. Add failure-injection coverage for unreadable ACLs and failed ACL reapplication, alongside a Windows integration check.

Source: [fallback implementation](C:/projects/HostsManager/src/HostsManager.Core/HostsFileWriter.cs:321), [swallowed ACL-read errors](C:/projects/HostsManager/src/HostsManager.Core/HostsFileWriter.cs:343).

### 2. P1 — The drift check does not protect the whole write transaction

**Evidence: source-confirmed concurrency gap; a simultaneous-write scenario was not executed.**

The writer reads the current file and checks its hash, creates a backup and prunes old backups, then replaces the file. There is no lock covering these operations and no final drift check immediately before replacement. An external editor or tool can update the file during that interval; the replacement overwrites that update, and the resulting hash still matches the app's intended bytes.

The app's single-instance guard does not close this gap: restore commands and elevated helpers deliberately bypass it, and the normal guard is session-local. Backup names also use a check-then-create pattern, allowing overlapping processes to choose the same name and overwrite a backup. The original backup has a similar existence-check/write race.

**Improve:** Serialize the application's save and restore transactions per target file, including helpers and recovery commands. Create backup names atomically with exclusive creation. Revalidate the destination immediately before replacement and test external replacement during the transaction. An application mutex alone cannot exclude unrelated tools; document that remaining limitation and design conflict handling around it.

Source: [check, backup and replace sequence](C:/projects/HostsManager/src/HostsManager.Core/HostsFileWriter.cs:205), [backup allocation](C:/projects/HostsManager/src/HostsManager.Core/BackupManager.cs:80), [instance guard](C:/projects/HostsManager/src/HostsManager/Services/SingleInstance.cs:17), [recovery bypass](C:/projects/HostsManager/src/HostsManager/App.xaml.cs:32).

### 3. P1 — Saving from a standard account breaks when UAC uses another account

**Evidence: source-confirmed identity-dependent path mismatch; not tested with another account's credentials.**

The ordinary process places its request under its own Local AppData folder. The elevated process accepts requests only under `RequestDirectory`, which it independently computes from its own Local AppData. When a standard user supplies a different administrator account at UAC, those directories differ. The helper rejects the request before writing a result. Its backup-directory validation has the same identity dependency.

Same-account elevation does not encounter this mismatch. The documented ordinary-user workflow needs to cover both cases.

**Improve:** Design the handoff around an authenticated initiating identity, with an explicit policy for that user's request and backup storage. A secured IPC channel is one option. Do not solve this by permitting arbitrary privileged output directories. Verify same-account approval, alternate-account credentials, cancellation and missing-result behavior.

Source: [request directory and process launch](C:/projects/HostsManager/src/HostsManager/Services/ElevatedHostsFileCommitter.cs:20), [request-path rejection](C:/projects/HostsManager/src/HostsManager/Services/ElevatedHostsFileCommitter.cs:82), [backup scope validation](C:/projects/HostsManager/src/HostsManager/Services/ElevatedHostsFileCommitter.cs:123).

### 4. P2 — A readback exception bypasses rollback

**Evidence: source-confirmed; post-replacement read failure was not injected.**

`CommitPrepared` catches replacement failures and rolls back. It also rolls back when a successful readback produces the wrong hash. But `File.ReadAllBytes` for that readback sits outside the replacement try/catch. If the read itself throws after replacement—for example because another process obtains an incompatible lock—the method exits without invoking rollback or clearly distinguishing a committed write from an untouched file.

**Improve:** Include readback failures in the transaction's explicit recovery handling. Return or report whether the new content was verified, rollback succeeded, or the final state is unknown, and retain the relevant backup path. Test an exception at each post-write stage.

Source: [post-write verification](C:/projects/HostsManager/src/HostsManager.Core/HostsFileWriter.cs:224).

### 5. P2 — Three disabled entries at the top can disappear from the list

**Evidence: reproduced.**

The documentation-header heuristic treats any leading block of at least three comment lines followed by a blank line as documentation. That includes genuine disabled mappings:

```text
#127.0.0.1 a.test
#127.0.0.2 b.test
#127.0.0.3 c.test

127.0.0.4 d.test
```

The probe found only **one entry and zero disabled entries**, although the file contains four mappings. The bytes remain in the file, but the three disabled entries cannot be enabled from the table and can be skipped when importing such a file.

**Improve:** Recognize the actual Windows sample header more narrowly, or expose ambiguous commented mappings with an explicit classification option. Add tests for a custom file with three or more initial disabled entries and for a save/reload cycle that creates that shape.

Source: [header classification](C:/projects/HostsManager/src/HostsManager.Core/HostsFileParser.cs:56).

### 6. P2 — Deletions can be reported as “No unsaved changes”

**Evidence: reproduced in the core; UI wording traced in source.**

Deleting the first entry from a two-entry file produced `IsDirty = true` but `ModifiedCount = 0`. The counter counts only modified lines still in the document, so it cannot count a removed line. `PendingText` consequently says “No unsaved changes,” while Save remains enabled. Exit and reload confirmations reuse that wording, and a save can label the backup “0 changes.”

**Improve:** Derive pending changes from a baseline comparison or track additions, modifications and deletions explicitly. Use the same change summary for the header, confirmations and backup reason. Test a deletion alone, multiple deletions, and add-then-remove returning to the original state.

Source: [counter](C:/projects/HostsManager/src/HostsManager.Core/HostsDocument.cs:50), [removal](C:/projects/HostsManager/src/HostsManager.Core/HostsDocument.cs:277), [display text](C:/projects/HostsManager/src/HostsManager/ViewModels/MainViewModel.cs:181).

### 7. P2 — Duplicate diagnostics miss aliases and become stale

**Evidence: alias omission reproduced; stale UI state and diagnostic wording confirmed in source.**

For these three entries, the duplicate scan flags line 2 but misses the repeated `b.test` on line 3:

```text
127.0.0.1 a.test
127.0.0.2 a.test b.test
127.0.0.3 b.test
```

The scan stops processing the aliases on a line as soon as it finds one duplicate, so it never records `b.test` from line 2. Separately, editing calls `OnDocumentChanged`, which refreshes rows without recalculating their `IsShadowed` flags. The banner recalculates its count, but the Problems filter uses those old row flags. Disabling or deleting a conflicting entry can therefore make the banner and filtered rows disagree until a save or reload.

The explanation also overstates the conclusion: it says a whole line has no effect when just one alias repeats, and assumes that later addresses are always ineffective. Microsoft documents that Windows-backed host resolution can return the address or addresses in the hosts file. A repeated hostname alone is insufficient evidence that another mapping is useless. [Microsoft DNS API documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.dns.gethostaddresses?view=net-10.0).

**Improve:** Analyze all aliases, report the specific repeated names and differing targets, distinguish IPv4/IPv6 mappings, and avoid claiming a whole line is ineffective. Recompute diagnostics consistently after every edit and notify the affected bindings. Add core cases for partial aliases and dual-stack mappings, plus view-model tests for live Problems filtering.

Source: [duplicate scan](C:/projects/HostsManager/src/HostsManager.Core/HostsDocument.cs:301), [edit refresh](C:/projects/HostsManager/src/HostsManager/ViewModels/MainViewModel.cs:495), [problem filter](C:/projects/HostsManager/src/HostsManager/ViewModels/MainViewModel.cs:624), [tooltip claim](C:/projects/HostsManager/src/HostsManager/ViewModels/EntryViewModel.cs:95).

### 8. P2 — Missing backup metadata turns an unknown integrity state into “Yes”

**Evidence: reproduced.**

A modified backup correctly failed verification while its original manifest existed. Removing the manifest caused `List()` to reconstruct metadata and calculate a fresh hash from the already-modified file. The same file then passed `Verify()`, and the backups dialog would show “Intact: Yes.” A corrupt JSON manifest follows the same reconstruction path.

Keeping old or manually copied backups recoverable is useful. The defect is presenting a newly calculated hash as evidence that the file matches its historical contents.

**Improve:** Preserve the distinction between a recorded hash and a reconstructed one. Show missing or unusable metadata as “Unverified,” allow deliberate recovery from it, and verify the exact byte buffer subsequently restored rather than reading it separately for verification and restoration.

Source: [manifest fallback](C:/projects/HostsManager/src/HostsManager.Core/BackupManager.cs:100), [reconstructed hash](C:/projects/HostsManager/src/HostsManager.Core/BackupManager.cs:192), [intact column](C:/projects/HostsManager/src/HostsManager/Views/BackupsDialog.xaml.cs:30).

## Findings added on 16 September 2026

**9. P1 — Prepared saves do not independently preserve managed sections.** An isolated probe submitted a prepared write with a matching baseline hash but without the existing Docker block. `CommitPrepared` returned success and removed that block from the test file. Parsing the candidate establishes its own baseline, so checking its modified flags cannot establish preservation against the destination. This is a validation gap; the probe did not demonstrate a UAC bypass. Normal saves should compare the existing managed sections with the proposed content, including boundaries and line endings. Restores need a separate, explicit policy because restoring an older whole-file backup can legitimately change those sections. See action **A03**.

**10. P1 — Different target files can share the wrong recovery history.** Isolated probes reproduced both an original backup belonging to a rehearsal file and Restore Latest copying rehearsal contents into a second target. The application allows a custom hosts path without requiring separate backup storage. Separate all backup history by target and validate target identity during restore; disabling only original-backup creation for custom paths is insufficient. Existing backups with no recorded target need a conservative migration path that retains their bytes. The README's rehearsal command already includes a separate backup directory, so the defect concerns the permitted argument combination, not that example. See action **A01**.

These probes used files under the ignored build directory, not the live hosts file. The corrected addendum contains the source references and implementation constraints. UI responsiveness remains an improvement already identified below; large-file performance is a measurement task, not a demonstrated severe regression.

## Noticeable improvements worth building

| Improvement | User benefit | Suggested scope |
| --- | --- | --- |
| Review changes before saving | Makes imports, group toggles and deletions understandable before a system-wide change. | Show added, removed and toggled mappings, with a file-text comparison available. Reuse this summary for pending counts. |
| Edit an existing mapping | Avoids deleting and recreating a row just to change an IP or comment. | Reuse the add dialog's validation; retain the row's group, enabled state and placement. The current table is read-only and exposes no edit command. |
| Responsive save and recovery | Keeps the window responsive while waiting on elevation and disk operations. | Replace synchronous UI-thread waits with an asynchronous operation and clear progress state. Serialize commands and define safe cancellation before the commit boundary. |
| Clearer diagnostics | Helps users understand exactly which hostname needs attention. | Show conflict details on rows, keep the problem list current, and avoid equating every repeated hostname with a broken mapping. |
| Keyboard and accessibility pass | Makes custom controls usable without relying on mouse position or color. | Verify accessible names for icon buttons and toggles, visible keyboard focus, contrast in both themes, and screen-reader announcements. No explicit automation names were found in the XAML search; actual accessibility behavior needs testing. |
| Browser-preview storage controls | Makes persistent isolated-browser data understandable and manageable. | The profile directory is retained per resolver-rule hash. Explain reuse and offer cleanup of inactive profiles; do not remove an active browser's data. |

The checked-in screenshot has a clear table hierarchy and separates invalid and managed entries visually. It is a historical image, not evidence of the current live layout. Refresh it after the diagnostic changes and verify both themes, keyboard use and scaled displays through the actual application.

## Engineering and release priorities

**Plan the runtime upgrade now.** The app, core, tests and CI target .NET 8. Its support ends **10 November 2026**, less than two months after this review. Evaluate and validate migration to .NET 10 LTS, supported until **14 November 2028**. The release builds bundle the runtime, so servicing also requires rebuilding and redistributing the application. [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

**Extend tests across the boundaries where the risks now live.** Add desktop view-model tests for pending counts and diagnostics, controlled filesystem failures for the writer, and Windows integration scenarios for UAC, tray interaction, browser start/end, and MSI install/upgrade/uninstall. Compiling WPF does not validate runtime data bindings. CI currently builds the x64 package; add ARM64 packaging and representative execution where infrastructure permits.

**Make releases easier to reproduce and trust.** Add an SDK selection policy, repository-local WiX tool configuration, and dependency update monitoring. Consider signing the executable and MSI, and publish checksums for release artifacts. These are distribution improvements; signing does not replace the write-path fixes.

**Keep refactoring targeted.** `MainViewModel.cs` is 706 physical lines and combines editing, grouping, filtering, importing, saving and watching the file. Extract change summaries and diagnostics first because those responsibilities already produce inconsistencies. Introduce a small testable filesystem/commit boundary for failure injection. The existing core/UI separation is worth retaining.

**Update the claims alongside the fixes.** In particular, qualify concurrent-edit protection, correct the permission fallback promise, distinguish verified and unverified backups, and replace the blanket first-match-wins description. Keep operational guidance in the README and historical investigation detail in the decision log.

| Order | Work package | Completion evidence |
| --- | --- | --- |
| 1 | Permission fallback, transaction coordination, readback recovery and elevation identity | Failure-injection checks plus Windows tests for same-account and alternate-account elevation; an overlapping save/restore cannot silently overwrite another app transaction. |
| 1 | Target-specific backup history and independent managed-section checks | Custom-file backups cannot be silently restored into another target; ordinary saves preserve managed sections while deliberate restores follow an explicit policy. |
| 2 | Header classification, pending counts, duplicate diagnostics and backup integrity labels | Each reproduced case becomes a regression test; view-model tests confirm accurate feedback before saving. |
| 3 | Runtime migration and release validation | Supported runtime, green core and desktop checks, verified packaged startup and upgrade on representative target systems. Complete before .NET 8 support ends. |
| 4 | Change preview, entry editing, asynchronous operations and accessibility | User can review and edit mappings, understand progress, and complete the main workflow using a keyboard. |

## Verification record and limits

- Ran the existing Release test suite: **148 passed, 0 failed, 0 skipped**. NuGet emitted **NU1900** because its vulnerability-data endpoint was unavailable; dependency vulnerability status remains unverified.
- Built the WPF app in Release: **0 warnings, 0 errors**. The initial restricted attempt could not access the Windows SDK directory; the build succeeded with the required access.
- Ran isolated probes against the core for deletion counts, duplicate aliases, initial disabled mappings and missing backup manifests. The observed results are recorded in findings 5–8. The probes used disposable files under the ignored build directory.
- Reviewed the core, view models, application startup, Windows services, dialog flows, XAML, test sources, installer/release scripts, workflow and documentation. Inspected the existing screenshot.
- Did not run live-system saves, UAC prompts, another user's account, browser sessions, tray automation, installers or fault injection against operating-system permissions. Did not verify remote CI runs or published release artifacts. Those checks remain required to close the integration findings.
- The original review added this report. The 16 September follow-up updates the review documents and adds a selection checklist; application source remains unchanged. Existing untracked `.claude/` content was left untouched.
