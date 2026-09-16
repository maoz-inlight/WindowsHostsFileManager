# Hosts Manager — corrected review addendum

Originally dated 14 September 2026 · Revised 16 September 2026

Companion: [original review](C:/projects/HostsManager/docs/project-review-2026-09-14.md) · Selection checklist: [action items](C:/projects/HostsManager/docs/action-items.md)

The original review examined version 1.0.12, commit `b19c25e`. Follow-up checks used version 1.0.13, commit `4e9338d`. The core write, parsing and backup paths discussed here are unchanged between those commits. This is not a new full review of the browser changes in 1.0.13.

## Assessment and evidence

The addendum contributes two useful, newly reproduced defects: missing independent managed-section preservation in prepared writes, and backup history shared between different target files. It also identifies plausible performance work. This revision corrects the earlier runtime timeline, removes unsupported timing and severity claims, and separates observations from remedies that still require design and testing.

Evidence labels mean:

- **Reproduced:** observed using isolated test files. This does not imply a live-system or UAC test.
- **Source-confirmed:** the code path exists, but its operating-system trigger was not reproduced.
- **Needs measurement:** allocations or blocking operations are visible in source; their user-visible performance impact has not been quantified.

P1 means address before broader deployment because of file-safety implications or an important blocked Windows configuration. P2 means a meaningful maintenance issue. These labels do not mean every finding is a critical security vulnerability.

## Original findings retained

| Finding | Assessment | Evidence | Action |
| --- | --- | --- | --- |
| 1. Permission fallback can lose original access rules | Retained | Source-confirmed; ACL failures not injected | A02 |
| 2. Drift check leaves a concurrency gap | Retained | Source-confirmed; competing writer not exercised | A04 |
| 3. Alternate-account UAC uses a different request/backup directory | Retained | Source-confirmed; alternate credentials not exercised | A06 |
| 4. Post-write read exception bypasses recovery handling | Retained | Source-confirmed; read failure not injected | A05 |
| 5. Leading disabled entries can be classified as documentation | Retained | Reproduced in the original review | A08 |
| 6. Deleted lines can leave the pending count at zero | Retained | Core behavior reproduced; UI wording traced in source | A07 |
| 7. Duplicate analysis skips aliases and UI flags become stale | Retained | Alias omission reproduced; UI refresh gap source-confirmed | A09 |
| 8. Missing backup metadata can incorrectly imply verified integrity | Retained | Reproduced in the original review | A10 |

The previous version's blanket claim that every finding was independently re-tested was stronger than the documented evidence. Original test and build results remain historical: 148 tests passed on the original baseline. They have not been reissued as results for version 1.0.13.

## Finding 9 — P1: Prepared writes do not independently preserve managed sections

**Evidence: reproduced against a test file through the core commit entry point.**

The ordinary editing model protects recognized Docker and Tailscale lines. However, the elevated helper calls `CommitPrepared`, which parses the proposed content into a fresh document and verifies that document against its own rendering. Parsing marks every line committed. The managed-line modified check therefore cannot establish whether the proposal removed or changed sections from the existing destination.

The follow-up probe supplied a matching baseline hash and replacement content without the existing Docker block. The commit succeeded and the block was absent afterward.

Source: [prepared-write verification](C:/projects/HostsManager/src/HostsManager.Core/HostsFileWriter.cs:182), [managed-line check](C:/projects/HostsManager/src/HostsManager.Core/HostsFileVerifier.cs:81).

### Scope and remedy

This demonstrates an independent validation gap. It does not show a bypass of UAC or establish that ordinary supported editing operations destroy managed sections. A malicious-handoff scenario additionally depends on request tampering and an authorized elevated helper invocation.

For an ordinary save, compare the destination's recognized managed sections with the proposed sections, including delimiters, ordering and line endings. Detect removal as well as content changes. The candidate must be checked against the validated baseline, not just against itself.

**Do not apply that rule unconditionally to restores.** Restoring a whole-file backup intentionally replaces current contents and may legitimately add, remove or change managed blocks. Give save and restore explicit operation semantics, validated through the trusted operation flow. Preserve the current file as a recoverable backup before an intentional restore. A request-supplied boolean alone is not authentication.

The earlier sample patch also omitted line endings and parsed the current text using the candidate's format. It should not be copied into production as written.

Selection item: **A03**. Coordinate with **A04** and **A06**.

## Finding 10 — P1: Backup history can belong to a different target file

**Evidence: reproduced with two test files sharing one backup directory.**

The app accepts a custom `--hosts-path` while defaulting backup storage when `--backups-dir` is omitted. Original-backup creation and ordinary backup listing are scoped to that directory, not to the target's identity.

The follow-up probes observed:

1. Loading the rehearsal file first captured its contents as the shared original.
2. Loading a second target did not replace that original.
3. Saving the rehearsal file added a timestamped backup to the shared history.
4. Restoring the latest backup into the second target copied rehearsal contents there.

Source: [writer/backup setup](C:/projects/HostsManager/src/HostsManager/App.xaml.cs:26), [original backup](C:/projects/HostsManager/src/HostsManager.Core/BackupManager.cs:62), [backup creation and listing](C:/projects/HostsManager/src/HostsManager.Core/BackupManager.cs:80).

### Scope and remedy

Separate **all** backup history by target, and record and validate target identity when restoring. This includes Original, Latest, the backups dialog, headless restores, and explicit shared backup-directory configurations.

Simply skipping original-backup creation for custom paths is incomplete: timestamped backups would still mix. It also removes a useful recovery feature for custom files. A single sibling backup folder is insufficient if multiple target files live in the same directory.

Design a conservative transition for existing backups: retain their bytes, mark unknown provenance, and do not silently assign them to the live system file. Path normalization and aliases need an explicit policy.

The README's actual rehearsal example includes `--backups-dir`. The original addendum omitted that argument when quoting the documentation. The supported argument combination remains a defect even though that particular example is safe.

Selection item: **A01**. Coordinate with **A06** and **A10**.

## Finding 11 — P2: Blocking UI operations need an asynchronous design

**Evidence: source-confirmed blocking; no new desktop freeze reproduction is claimed.**

Saving runs synchronously from the desktop view model and waits for the elevated helper. DNS flushing also waits for a child process on its caller's thread. Those paths can prevent the window from responding promptly.

Source: [helper wait](C:/projects/HostsManager/src/HostsManager/Services/ElevatedHostsFileCommitter.cs:49), [DNS flushing](C:/projects/HostsManager/src/HostsManager/Services/DnsFlusher.cs:11).

Corrections to the earlier addendum:

- DNS flushing is a separate command; it is not invoked on every save or tray toggle.
- The original review already proposed asynchronous operations, so this expands an existing improvement.
- A claim that a native DNS call always completes in less than one millisecond is unsupported. API support, behavior and privilege requirements should be verified before selecting a replacement.
- Replacing the process with a synchronous native call does not by itself guarantee UI responsiveness.
- Cancellation must distinguish waiting/preparation from a commit already in progress. Terminating a helper mid-write is not a safe cancellation design.

Make save, restore and DNS operations responsive, serialize incompatible operations, show progress, and handle timeouts and final states honestly. If retaining the DNS utility, resolve its trusted system location and handle process output and timeout results correctly. A native replacement is an option to evaluate, not a prerequisite.

Selection item: **A12**, after the relevant transaction semantics in **A04–A06**.

## Finding 12 — Investigation: Large-file allocation costs need measurement

**Evidence: allocation-producing paths are source-confirmed; severe stuttering has not been measured.**

`IsDirty` renders and compares the full document. The search predicate assembles searchable text for each row. These operations can create significant work for large files, especially with repeated command and binding reevaluation.

Source: [dirty-state calculation](C:/projects/HostsManager/src/HostsManager.Core/HostsDocument.cs:48), [search predicate](C:/projects/HostsManager/src/HostsManager/ViewModels/MainViewModel.cs:621).

The previous text incorrectly said every toggle invokes `SettleIfUnchanged`; `SetEnabled` changes the line kind directly. UI reevaluation can still call `IsDirty`, but the distinction matters when measuring.

Benchmark representative small, medium and large files before selecting an optimization. Record operation latency and allocation volume for load, toggle, search, import and grouping. Search caching and debouncing may help. Cache invalidation must account for changed groups, comments, status and enabled state.

A modified-line counter alone does not cover deletions, ordering, group markers, import equivalence or add-then-remove returning to the baseline. Any replacement must preserve those semantics. Reuse the work in **A07** where appropriate.

Selection item: **A13**. Treat optimization scope and priority as dependent on measurements.

## Implementation qualifications

### Transaction coordination and Windows replacement

A named lock can coordinate cooperating Hosts Manager processes, including helpers and recovery commands. Its permissions must support the intended users and elevation flow; a global name alone does not establish cross-account access.

Do not assume an exclusive handle held over the target is compatible with replacement. Conversely, the earlier assertion that every handle without both write-sharing and delete-sharing necessarily causes failure was too broad. Windows documents the access rights and sharing used by the replaced and replacement handles; compatibility must be checked against the actual handles used. [Microsoft ReplaceFileW documentation](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew).

A final pre-replacement hash check narrows the external-edit window. It **does not eliminate** a change occurring between that check and replacement. A mutex cannot control Docker, editors or other programs that do not participate. Document this residual risk and test recovery and conflict reporting. Do not blindly roll back over a newer external change.

### Elevation handoff

Secured named pipes are a reasonable alternative to the file handoff. Standard-stream redirection cannot simply be combined with shell-based elevation. [Microsoft ProcessStartInfo.UseShellExecute documentation](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.useshellexecute?view=net-8.0).

A pipe's access list is only one part of the design. Establish the initiating user's identity, connect to the intended helper, validate the operation and target, and define backup ownership. Test same-account UAC, alternate-account credentials, cancellation and helper failure. Do not claim that a pipe alone fixes the different-account backup policy.

### Header and duplicate semantics

Use a narrower, tested sample-header recognition rule or expose ambiguous commented mappings. The claim that the Microsoft header has been identical since Windows NT is unnecessary and unsupported here. Avoid relying solely on fixed line positions or copyright years.

Treat dual-stack mappings and intentional multiple addresses separately from exact duplicates and conflicting mappings. Do not promise that address ordering always follows file order or that a repeated hostname makes an entire line ineffective. [Microsoft DNS API documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.dns.gethostaddresses?view=net-10.0).

### Runtime timeline — corrected

.NET 10 LTS was released on **11 November 2025** and is supported until **14 November 2028**. Both .NET 8 and .NET 9 end support on **10 November 2026**. The previous addendum's November 2026 release date for .NET 10 was incorrect; a migration through .NET 9 offers no support-window extension.

Evaluate the supported .NET 10 release directly, validating WPF, self-contained publishing, CI and MSI packaging. Rebuild and redistribute self-contained artifacts for runtime servicing. [Microsoft .NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

Selection item: **A11**, scheduled independently of optional product work.

## Selection and scope

The [action-item checklist](C:/projects/HostsManager/docs/action-items.md) consolidates all findings and improvements into stable IDs with completion criteria and dependencies. Items remain proposed until the user selects them. This revision changes documentation only; it does not claim that any implementation defect has been fixed.
