# Hosts Manager — action items for selection

Prepared 16 September 2026 · Current checkout: 1.0.13 (`4e9338d`)

Sources: [project review](C:/projects/HostsManager/docs/project-review-2026-09-14.md) and [corrected addendum](C:/projects/HostsManager/docs/project-review-addendum-2026-09-14.md).

**Status: A01, A02, A03, A04 and A06 selected and implemented on `codex/p1-reliability-fixes`. See [implementation and validation](p1-implementation.md); actual alternate-account UAC validation remains outstanding.** Check a box to select an item, not to mark it complete. You can also reply with IDs, for example: “Take A01, A02 and A07; defer the rest.” IDs remain stable if priorities change.

P1 = address before broader deployment. P2 = maintenance correctness or responsiveness. Scheduled = time-sensitive platform work. Investigation = measure before committing to a fix. Optional = product or engineering improvement.

Effort is relative implementation and validation scope, not a delivery estimate: **S** is localized, **M** crosses a few components, **L** crosses process, platform or recovery boundaries. Every selected fix includes relevant regression checks and updates to affected documentation.

**Current batch (17 September): A15, A16, A17 and A18 selected and implemented for 1.0.15. A16 remains partially verified; manual platform checks remain outstanding. See [implementation and validation](a15-a18-implementation.md).**

## Selection checklist

- [x] **A01 — Separate backup history for each target file.** P1 · Effort L
- [x] **A02 — Refuse a save fallback that cannot preserve permissions.** P1 · Effort M
- [x] **A03 — Preserve managed sections independently during ordinary saves.** P1 · Effort M
- [x] **A04 — Coordinate overlapping writes and detect late conflicts.** P1 · Effort L
- [ ] **A05 — Handle verification failures after writing with accurate recovery outcomes.** P2 · Effort M
- [x] **A06 — Support elevation using a different administrator account.** P1 · Effort L
- [ ] **A07 — Correct pending-change counts, including deletions.** P2 · Effort M
- [ ] **A08 — Keep leading disabled entries visible.** P2 · Effort S
- [ ] **A09 — Correct duplicate diagnostics and refresh them while editing.** P2 · Effort M
- [ ] **A10 — Distinguish verified, changed and unverified backups.** P2 · Effort M
- [ ] **A11 — Migrate and validate the app on .NET 10 LTS.** Scheduled · Effort L
- [ ] **A12 — Keep save, restore and DNS operations responsive.** P2 · Effort L
- [ ] **A13 — Measure large-file performance before optimizing.** Investigation · Effort M
- [ ] **A14 — Add a review of pending changes before saving.** Optional · Effort M
- [x] **A15 — Edit an existing mapping without deleting it.** Optional · Effort M
- [x] **A16 — Verify keyboard use, accessibility and display scaling.** Optional · Effort M
- [x] **A17 — Explain and manage retained browser-preview profiles.** Optional · Effort M
- [x] **A18 — Improve repeatable builds and release validation.** Optional · Effort L

**Suggested selection:** A01–A10 form the reliability backlog, not a single required batch. For a smaller initial batch, A02, A07 and A08 are comparatively contained; schedule A01 promptly because it affects recovery. Plan A11 now regardless of which product improvements you choose. A13 is a measurement task; optimization would be scoped from its results.

## Completion criteria and dependencies

### A01 — Separate backup history for each target file

**Why:** Reproduced mixing of both original and timestamped backups between two target files can restore the wrong content. Review/addendum finding 10.

**Work:** Give each target its own recovery history and record target provenance. Cover default and explicit backup directories, the dialog, and Original/Latest commands. Define path normalization and how existing backups without provenance are handled. Keep existing backup bytes; do not silently classify old unknown backups as belonging to the system hosts file.

**Done when:** Two files cannot silently share restore history, including when configured with one backup root. Cross-target recovery requires an explicit, clearly described import/restore flow if supported. Legacy backups remain available with honest provenance information. Tests cover first load, saves, original/latest restore, and existing backup migration.

**Coordinate with:** A06 for initiating-user identity and A10 for unknown metadata.

### A02 — Preserve file permissions or refuse the fallback

**Why:** Source-confirmed fallback proceeds when it cannot read the destination's permissions. Finding 1.

**Work:** Fail safely before a fallback move if permissions cannot be captured, preserve them through recovery, and validate the final permissions. Review ignored metadata errors in the normal replacement path as part of the same guarantee.

**Done when:** Injected permission-read and permission-restore failures never produce a misleading success. Tests and Windows checks verify that an ordinary successful save retains the intended access rules and failures report the actual recovery state.

**Dependency:** Can begin independently; coordinate recovery behavior with A05.

### A03 — Independently protect managed sections on save

**Why:** Reproduced prepared write removed a Docker block despite a matching baseline hash. Finding 9.

**Work:** Distinguish save and restore operations explicitly. For ordinary saves, compare managed content against the validated destination baseline, including boundaries, ordering and line endings. Define deliberate whole-file restore behavior through the trusted operation flow.

**Done when:** Prepared saves that alter, remove or truncate managed blocks are refused; valid saves preserve them. Deliberately restoring an older backup still works under the explicit restore policy, with a backup of the displaced file. Tests cover Docker and Tailscale, content and delimiter changes, and line endings.

**Coordinate with:** A04 and A06; a request field alone must not authorize weaker validation.

### A04 — Coordinate writes and handle concurrent changes

**Why:** Source-confirmed gap between drift checking and replacement; backup allocation also uses check-then-write. Finding 2.

**Work:** Serialize cooperating app writers across UI, helper and recovery processes with suitable cross-account permissions. Create backup files exclusively. Revalidate the target immediately before replacement and define conflict/recovery behavior.

**Done when:** Concurrent app save/restore tests cannot silently overwrite another app transaction or collide on backup names. External edits are detected where checks allow, and residual timing risks are documented. Recovery does not blindly overwrite a detected newer external change.

**Limit:** An application lock and a last-minute hash check do not eliminate races with unrelated tools.

**Coordinate with:** A01, A05 and A06.

### A05 — Handle failures after replacement accurately

**Why:** Source-confirmed readback exceptions bypass the existing rollback path. Finding 4.

**Work:** Handle both read exceptions and hash mismatches as explicit verification outcomes. Distinguish verified success, verified rollback, and unknown final state, with a recovery backup reference. Account for a newer external write before attempting rollback.

**Done when:** Injected failures at replacement, readback and rollback produce truthful outcomes and leave a usable recovery reference. The UI does not report an unverified write as safely committed or an uncertain file as untouched.

**Coordinate with:** A02 and A04.

### A06 — Support alternate-account elevation

**Why:** Source-confirmed Local AppData mismatch when UAC runs the helper as another administrator. Finding 3.

**Work:** Define an authenticated request/response handoff and the initiating user's backup policy. A secured named pipe is a candidate, not a predetermined complete solution. Keep privileged writes limited to the intended operation and target.

**Done when:** Windows integration scenarios pass for same-account elevation, alternate credentials, cancellation, helper failure and malformed requests. Backups remain associated with the initiating user and correct target. Invalid operations cannot widen the helper's write scope.

**Coordinate with:** A01, A03 and A04. Requires appropriate Windows test accounts; any unavailable scenario must remain explicitly unverified.

### A07 — Count all pending changes correctly

**Why:** Reproduced deletion leaves a dirty document with zero modified lines. Finding 6.

**Work:** Use a consistent baseline/change summary for additions, edits and removals, including structural group changes. Reuse it in header text, confirmations and backup reasons.

**Done when:** A deletion never says “No unsaved changes”; undoing changes back to the exact baseline clears the pending state. Tests include deletion-only, toggle-back, add-then-remove, equivalent imports and group changes. Save availability and user-facing text agree.

**Dependency:** Independent. Provides a foundation for A14 and informs A13.

### A08 — Preserve visibility of leading disabled entries

**Why:** Reproduced header heuristic hides three initial commented mappings followed by a blank line. Finding 5.

**Work:** Narrow sample-header recognition without depending only on fixed line numbers or copyright years. Preserve original text and handle ambiguous commented mappings explicitly if necessary.

**Done when:** Leading disabled mappings remain visible after load, import and save/reload, while recognized sample documentation remains documentation. Tests cover the standard sample, custom headers and several leading disabled entries.

**Dependency:** Independent.

### A09 — Make duplicate diagnostics accurate and current

**Why:** Reproduced missed aliases plus source-confirmed stale row flags and overly broad “no effect” wording. Finding 7.

**Work:** Analyze every alias, distinguish exact duplicates from different addresses and dual-stack mappings, and refresh diagnostics after relevant edits. Explain which hostname is repeated without asserting an entire row is ineffective.

**Done when:** Partial-alias cases are found; IPv4/IPv6 pairs are not incorrectly described as shadowed. The banner, row details and Problems filter agree immediately after toggles, additions and deletions. Core and view-model tests cover these transitions.

**Dependency:** Independent; diagnostics semantics should precede cosmetic changes.

### A10 — Report backup integrity and provenance honestly

**Why:** Reproduced reconstructed hash makes a changed backup appear intact when its manifest is missing. Finding 8.

**Work:** Represent verified, changed and unverified integrity separately. Keep unknown-provenance backups recoverable through a deliberate flow. Verify the exact bytes subsequently restored rather than separately reading them for verification and restore.

**Done when:** Missing/corrupt manifests never imply historical integrity; modified bytes fail comparison with a recorded hash; recovery from an unverified backup is clearly identified. Tests cover missing, corrupt and valid metadata plus restore-time byte changes.

**Coordinate with:** A01 for target provenance and migration. Provenance and integrity are separate properties.

### A11 — Move to a supported runtime with packaging checks

**Why:** .NET 8 support ends 10 November 2026. .NET 10 was released 11 November 2025 and is supported through 14 November 2028; .NET 9 has the same support end date as .NET 8. [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy).

**Work:** Evaluate and migrate the core, desktop app, tests, SDK policy and CI to .NET 10 LTS. Validate self-contained builds and installer compatibility for the architectures retained by the project.

**Done when:** Tests and desktop builds pass; representative packaged startup, install, upgrade and uninstall checks pass; runtime servicing/rebuild guidance is documented. Any architecture not executed is identified as unverified. Target completion before 10 November 2026.

**Dependency:** Schedule independently of optional product work; coordinate with A18 to avoid duplicate release changes.

### A12 — Keep operations responsive

**Why:** Source-confirmed synchronous waits can block the desktop UI. Expanded improvement/finding 11; no precise freeze duration is established.

**Work:** Make save, restore and DNS operations asynchronous, show progress, prevent conflicting edits during commit, and handle timeout and cancellation states. Verify any proposed native DNS API before adopting it; replacing the utility is not required for this item.

**Done when:** The UI remains responsive during controlled delays and helper failures. Repeated commands cannot overlap a write. Cancellation is safe before commit and does not imply a committed change was cancelled. Both window and tray flows report the final outcome consistently.

**Dependencies:** Settle relevant transaction/recovery semantics in A04–A06 first.

### A13 — Measure large-file performance

**Why:** Full-document dirty checks and per-row search allocations exist, but severe performance impact remains unmeasured. Finding 12.

**Work:** Benchmark representative small, medium and large hosts files. Record load, toggle, search, import and grouping latency and allocations; identify the costly operations.

**Done when:** A reproducible results summary identifies whether optimization is warranted and proposes measurable targets. Deliver a scoped follow-up recommendation; do not replace change tracking with a simplistic counter as part of this investigation.

**Coordinate with:** A07; proposed caching must preserve undo, deletions, groups and equivalent-import behavior.

### A14 — Review pending changes before saving

**Benefit:** Users can see the effect of an import, group toggle or deletion before applying it.

**Work:** Present added, removed and changed mappings, with an optional file-text comparison. Reuse the change summary instead of inventing another counter.

**Done when:** Preview accurately matches committed content for individual and bulk operations, including grouped entries and preserved comments. Users can return to editing without losing pending work.

**Dependency:** A07.

### A15 — Edit existing entries

**Benefit:** Changing an IP, hostname or comment no longer requires deleting and recreating a row.

**Work:** Add an edit flow using shared validation. Preserve position, group, enabled state and unrelated formatting; keep managed entries protected.

**Done when:** Editing, cancelling and saving behave consistently; invalid input cannot commit; unedited content stays intact. Tests cover grouped, disabled and managed entries.

**Coordinate with:** A07 and A09 for accurate pending state and diagnostics.

### A16 — Verify accessibility and desktop presentation

**Benefit:** Main workflows work reliably with a keyboard, assistive tools, both themes and scaled displays.

**Work:** Audit accessible names, keyboard focus, contrast, dialog navigation, display scaling and actual tray activation. Correct observed issues and refresh the screenshot afterward.

**Done when:** A documented manual matrix covers the main workflows, both themes and representative scaling. Any untested screen-reader or display scenario is identified explicitly.

**Dependency:** Independent, but repeat affected checks after selected UI changes.

### A17 — Manage retained browser-preview profiles

**Benefit:** Users understand which browser data persists and can remove inactive profiles deliberately.

**Work:** Explain profile reuse and retention, show relevant storage, and offer cleanup scoped to inactive app-owned profiles. Inspect current 1.0.13 browser behavior before implementing; the original review predates its options changes.

**Done when:** Active profiles cannot be removed; cleanup targets only the selected app-owned data; normal browser profiles are unaffected; retained-data behavior is clear.

**Dependency:** Independent. Cleanup is a user-initiated product feature, not an action authorized by selecting this planning document.

### A18 — Make builds reproducible and releases verifiable

**Benefit:** Releases can be recreated and checked beyond successful compilation.

**Work:** Add an SDK policy, repository-local WiX configuration and dependency monitoring; publish artifact checksums in the release process. Expand CI/package checks where practical and document manual install/upgrade/uninstall and supported architecture checks. Do not duplicate tests already included with selected fixes.

**Done when:** A documented clean-checkout build produces the intended versioned artifacts; CI runs the agreed checks; a release checklist distinguishes compiled, packaged and actually executed architectures. Artifact verification instructions are available.

**Coordinate with:** A11. Code signing remains a separate decision about publisher identity, certificate/service cost and credential storage; selecting A18 does not authorize purchasing or configuring a signing service.

## Implementation tracking

After selection, record only the chosen IDs in an execution plan and distinguish **selected**, **in progress**, **verified**, and **blocked/unverified**. A successful build alone does not close an item that requires Windows integration evidence. Publishing a release, installing it on the live system, or changing the real hosts file is outside this document-only task.
