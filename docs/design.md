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

`HostsFileWriter.Save()` runs a fixed sequence of gates. Any failure aborts before the
real file is touched:

1. **Drift check** — compare a SHA-256 of the on-disk file against the hash captured at
   load. If it changed (Docker or Tailscale rewrote it), refuse and prompt to reload.
2. **Render** to a string from the line model; unmodified lines are emitted from their
   preserved raw text.
3. **Re-parse the rendered output** and assert the resulting model matches the intended
   model line-for-line, including kind and ownership. This is the central malformation
   guard — a render bug cannot reach disk.
4. **Encoding check** — assert the rendered text survives an encode/decode round trip
   under the file's own codec (see *Encoding is not always UTF-8* below). A character the
   codec can't represent would otherwise be silently written as a substitute.
5. **Structural assertions** — no null bytes, exactly the source's trailing-newline
   behaviour preserved, managed-section regions unmodified.
6. **Backup** the current bytes. A save cannot proceed if the backup fails.
7. **Atomic replace** — write to a process-scoped temp file in the same directory, flush
   to physical disk, then `File.Replace`, which is atomic on NTFS and preserves the
   destination's ACLs. Falls back to `File.Move` only if `Replace` throws.
8. **Post-write readback** — re-read the file from disk and compare its hash to what was
   intended. A mismatch triggers an automatic rollback.

### Encoding is not always UTF-8

The file's encoding, BOM, and line-ending style are detected on load and reused on save,
never normalized. UTF-8 is decoded strictly; a file that fails strict UTF-8 decoding
(e.g. one edited long ago by an ANSI-era tool, where a byte like `0xE9` isn't valid
standalone UTF-8) falls back to Latin-1, which maps every byte 0x00–0xFF to exactly one
character and back, so the file still round-trips byte for byte instead of having
unparseable bytes silently replaced with `U+FFFD`.

## Backup & recovery

`BackupManager.cs`. Location: `%LOCALAPPDATA%\HostsManager\backups` — deliberately
user-scoped, not under `System32`, so backups stay readable and restorable without
elevation, which is exactly the situation you're in when something has gone wrong.

- A backup is taken automatically before every save, and before every restore (so a
  restore is itself undoable).
- `hosts.original.bak` captures the pre-app state on first launch and is never pruned.
- The last 50 timestamped backups are kept, each with a `.json` manifest recording
  timestamp, hash, size, encoding, entry count, and the action that triggered it.
- Recovery works without the app: backups are plain files, restorable with a one-line
  `copy` command from an elevated prompt, or via `--restore-latest` / `--restore-original`
  headless flags.

## Validation

- **IP** — `IPAddress.TryParse`, with an additional check that IPv4 is a complete dotted
  quad (bare `TryParse` accepts `"1"` as `0.0.0.1`).
- **Hostname** — labels 1–63 chars, alphanumeric/underscore boundaries, hyphens inside,
  total ≤253, optional trailing dot (used by Tailscale). Rejects whitespace, `#`, and
  control characters, since any of those would change how the line is interpreted.
- **Duplicates** — an active hostname defined more than once is flagged; Windows resolves
  the first match, so later duplicates are dead weight.

## UI

- Toolbar: Add, Delete, Reload, Backups, Flush DNS, Revert, Save.
- Grid: toggle switch (not a checkbox — faster to scan in a dense list) | Domain | Maps
  to | Source | Status. Read-only rows show a lock icon instead of a toggle. Unparseable
  rows show their line number so they're findable in an external editor.
- Row states: normal, `Pending` (edited, unsaved), `Disabled`, `Invalid`.
- Explicit Save (not write-on-every-toggle), so a session of edits becomes one elevated
  write with one backup.
- A `FileSystemWatcher` on the hosts file surfaces a banner if another tool rewrites it
  while the window is open.
- Status bar shows the detected encoding as visible, deliberate proof that the file's
  format is being preserved rather than silently normalized.

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
