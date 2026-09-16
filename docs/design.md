# Design

## Context

The Windows hosts file was being hand-edited in an elevated text editor to reroute
domains to localhost. That workflow had already caused real damage: the file contained a
paragraph of chat text pasted in by accident. Windows silently ignores lines it can't
parse, so this kind of corruption is invisible until something stops resolving.

The file also has properties a naive editor destroys: a UTF-8 BOM, CRLF line endings, a
trailing newline, ACLs owned by `BUILTIN\Administrators`, and blocks owned by other tools
(Docker Desktop, Tailscale) that get rewritten on their own schedule.

Goal: a desktop app that lists every entry, toggles them on or off, adds and removes
entries with validation, flags anything unparseable — and that structurally cannot leave
the file malformed.

## Why the app writes the file itself, not a CLI

Investigated delegating writes to an existing tool so the app would never touch the file
directly. Conclusion: no safe CLI exists, because no API exists.

- Windows ships nothing for this — no cmdlet, no `netsh` verb, no WMI provider. The hosts
  file is plain text with no supported programmatic interface.
- Every tool that manages it, including this one, rewrites the entire file. There is no
  incremental or append-safe operation, so delegating doesn't reduce risk — it relocates
  the risk into a parser that isn't under this app's control and can't be verified.
- [PsHosts](https://www.powershellgallery.com/packages/PsHosts/1.2.2) — semantically
  ideal (per-domain `Add/Remove/Enable/Disable/Get-HostEntry`, `-WhatIf`) but last
  published August 2017, unmaintained, no documented BOM/CRLF handling, and its C# core
  is not published as a reusable library.
- [hostctl](https://github.com/guumaster/hostctl) — maintained, but profile-based:
  toggles whole named blocks, not individual domains, and would not have adopted the
  file's existing standalone commented-out entries.

Safety comes from the write *pipeline* (backup → render → re-parse and verify → atomic
replace), not from who calls the write. Shelling out to a third party forfeits the
verify step — a write that wasn't performed by this code can't be checked by it. Separately,
`File.Replace` preserves the destination's ACLs; the naive `WriteAllText` most such tools
use can silently alter permissions on a file owned by `BUILTIN\Administrators`.

## Core: the line model

Every physical line maps to one `HostsLine` that preserves its raw text verbatim.
Rendering an unmodified document reproduces the original bytes exactly.

`LineKind`:

- `Entry` — active `IP host [host...] [# comment]`
- `DisabledEntry` — a comment whose body parses as a valid entry (e.g.
  `#127.0.0.1 staging.myapp.local`)
- `Comment` — a real comment
- `Blank`
- `Unparseable` — non-blank, non-comment, doesn't parse as an entry (this is where the
  stray pasted text lands)

Toggling only inserts or removes the leading `#`; the original whitespace, tabs,
alignment and inline comment are stored and restored, so enable → disable → enable is
byte-identical.

**Leading-documentation heuristic.** Contiguous comment lines from line 1 up to the first
blank line are always `Comment`, never `DisabledEntry`. This keeps Microsoft's
boilerplate example mappings (`rhino.acme.com`, `x.acme.com`) out of the toggle list.
Commented entries after that point are genuine disabled entries.

**Managed sections** (`ManagedSections.cs`) — marker pairs, extensible:

- `# Added by Docker Desktop` … `# End of section`
- `# TailscaleHostsSectionStart` … `# TailscaleHostsSectionEnd`

Lines inside are read-only in the model; toggle and delete are disabled for them, and the
write pipeline refuses a save that modifies one.

## Core: the safe-write pipeline

`HostsFileWriter.Save()` prepares and verifies the rendered document before committing.
For the Windows hosts path, an authenticated local named pipe carries the request to a
short-lived elevated helper. Both sides verify the peer process; the helper checks the
initiating identity and process creation time. Backup operations impersonate the initiating
user, preserving the configured backup root even when UAC uses another administrator.
The privileged write accepts only the real Windows hosts path. Custom targets commit locally.

The commit holds a per-target global mutex across baseline read, backup and replacement.
It independently parses proposed bytes, checks save drift, and compares managed sections
against the destination baseline, including delimiters, order, encoding and line endings.
An explicit restore instead requires a matching target manifest and recorded hash of the
exact selected backup bytes; it may intentionally change managed sections.

Windows DACL capture is mandatory. The unique sibling temporary file is flushed, receives
the captured access rules, and is checked before replacement. `File.Replace` uses strict
metadata handling; a fallback move requires unchanged destination bytes and a prepared
file with the original access rules. Final bytes and DACL are verified. Recovery reports
whether the original state was restored, external content was retained, or the state could
not be established, and identifies the recovery backup. It never deliberately rolls back
over detected external edits. Owner, group and audit SACL preservation are not asserted.

Late drift checks narrow races with Docker, Tailscale and other editors; the global lock
only coordinates Hosts Manager writers. An unrelated writer can still race replacement.
Target identity canonicalizes paths and resolves existing links; different hard-link paths
are not unified into one target identity.

### Encoding is not always UTF-8

The file's encoding, BOM, and line-ending style are detected on load and reused on save,
never normalized. UTF-8 is decoded strictly; a file that fails strict UTF-8 decoding
(e.g. one edited long ago by an ANSI-era tool, where a byte like `0xE9` isn't valid
standalone UTF-8) falls back to Latin-1, which maps every byte 0x00–0xFF to exactly one
character and back, so the file still round-trips byte for byte instead of having
unparseable bytes silently replaced with `U+FFFD`.

## Backup & recovery

`BackupManager.cs` stores history under `%LOCALAPPDATA%\HostsManager\backups\targets\<target-key>`.
An explicit backup directory changes the root. Each target has its own original, retention
and restore selection, with target provenance recorded in its manifests. Backup files use
exclusive creation and manifests are published after the bytes are flushed.

A backup precedes each save and restore. The first capture in this target history becomes
`hosts.original.bak`, which is never pruned; up to 50 timestamped backups are retained.
Legacy root files are retained for inspection through Earlier backups, never automatically
assigned to a target. Missing or invalid metadata is unverified and cannot be automatically
restored. Restore reads and verifies the exact byte buffer that will be committed.
Headless Original/Latest recovery reports missing compatible history without manufacturing
an Original first. See README for manual recovery and [P1 implementation](p1-implementation.md)
for verification coverage and remaining Windows integration checks.

## Validation

- **IP** — `IPAddress.TryParse`, with an additional check that IPv4 is a complete dotted
  quad (bare `TryParse` accepts `"1"` as `0.0.0.1`).
- **Hostname** — labels 1–63 chars, alphanumeric/underscore boundaries, hyphens inside,
  total ≤253, optional trailing dot (used by Tailscale). Rejects whitespace, `#`, and
  control characters, since any of those would change how the line is interpreted.
- **Duplicates** — an active hostname defined more than once is flagged; Windows resolves
  the first match, so later duplicates are dead weight.

## UI

- Toolbar: Add, Delete on the left; Revert, Save on the right. It carries document
  actions only. Backups, Flush DNS, and isolated browser preview don't touch the pending
  edit, so they sit behind an overflow menu at the right edge rather than competing with
  the buttons used every session. Reload isn't on the toolbar at all — it's the same
  operation as Revert, and
  re-reading the file only becomes the wanted action once the external-change banner
  fires, so the banner and Ctrl+R own it.
- Grid: toggle switch (not a checkbox — faster to scan in a dense list) | Domain | Maps
  to | Source | Status. Read-only rows show a lock icon instead of a toggle. Unparseable
  rows show their line number so they're findable in an external editor.
- Standard Ctrl/Shift row selection feeds one combined isolated-browser session. Right-click
  preserves an existing multi-selection and exposes the same count-aware action as the
  overflow menu and Ctrl+Shift+O.
- Row states: normal, `Pending` (edited, unsaved), `Disabled`, `Invalid`.
- Explicit Save (not write-on-every-toggle), so a session of edits becomes one elevated
  write with one backup.
- A `FileSystemWatcher` on the hosts file surfaces a banner if another tool rewrites it
  while the window is open.
- Status bar shows the detected encoding as visible, deliberate proof that the file's
  format is being preserved rather than silently normalized.

### Isolated browser preview

One or more entries can be opened in Edge or Chrome without modifying the system hosts
file. The app translates every hostname on the selected lines into Chromium
`host-resolver-rules`, lets the user choose and edit the starting tabs, and uses a rule-keyed
browser profile. That prevents an existing browser process with a different ruleset from
swallowing the new launch arguments. Repeated hostnames are combined only when their
targets agree; conflicting targets stop the launch and identify the hostname.

When the app is explicitly launched elevated, a normal child process would inherit administrator rights.
Browser launch therefore uses the interactive Windows shell token, creates the process
suspended, verifies that the child is not elevated, and only then lets it execute. Failure
to prove that boundary cancels the launch.

Preview lifetime follows the visible browser window as well as the launched process.
Chromium can leave a background process alive after its last window closes, so relying on
process exit alone would leave a stale active-preview banner. Background mode is disabled
for these sessions, and a short window-lifetime monitor clears the session when the last
isolated window disappears.

Visual style: flat surfaces, hairline borders, no gradients or drop shadows. Colour is
reserved for meaning — green for enabled, accent blue for pending, red for invalid,
muted grey for read-only.

## Verification approach

The test suite (`tests/HostsManager.Tests`) uses the real hosts file this app was built
for as a fixture, warts included, rather than a tidy synthetic sample. The load-bearing
guarantee it proves: parsing and re-rendering that fixture — BOM, CRLF, the Microsoft
documentation header, disabled entries, the pasted junk text, and both managed blocks —
produces byte-identical output.

Before the app was ever pointed at the real file, the same operations (add, toggle,
delete, restore) were rehearsed against a throwaway copy via a `--hosts-path` override,
confirming byte-identical round-trips and correct backup creation.

## Out of scope

DNS resolution or port-reachability status, named entry profiles/groups, import/export.
These are natural follow-ups on top of the current core, not requirements it was missing.
