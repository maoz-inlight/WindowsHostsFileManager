# Hosts manager

A Windows desktop app for managing `C:\Windows\System32\drivers\etc\hosts` — add, remove and
toggle domain overrides without hand-editing a system file in an elevated text editor.

Built because hand-editing had already gone wrong: the hosts file on this machine contained a
paragraph of chat text pasted in by accident. Windows silently ignores lines it can't parse, so
that kind of corruption stays invisible until something stops resolving.

## What it does

- **Toggle any domain on or off** with a switch. Disabling comments the line out; enabling
  uncomments it. Nothing else on the line changes.
- **Add and remove entries** with live validation — you see the exact line that will be written
  before it is written.
- **Flags unparseable lines** that Windows silently ignores, with the line number, so junk is
  visible and one click from being removed.
- **Flags duplicate domains**, which Windows resolves first-match-wins, so later ones are dead.
- **Leaves other tools alone.** Docker Desktop and Tailscale blocks are shown read-only and are
  never rewritten.
- **Backs up before every save**, keeps a permanent copy of the original, and can restore either.
- **Lives in the notification area.** Closing the window hides it there; the tray menu can
  toggle any domain without opening the app at all.
- **Follows your Windows light/dark setting**, including the title bar, and switches live.
- **Runs as a single instance**, so two copies can't fight over the same file.

## Installing

Grab the installer matching your CPU from `dist`:

| CPU | Installer |
| --- | --- |
| 64-bit Intel or AMD | `HostsManager-x64.msi` |
| ARM (Surface, Snapdragon) | `HostsManager-arm64.msi` |
| 32-bit Intel | `HostsManager-x86.msi` |

Each is self-contained — no .NET runtime needed on the target machine. A matching
`HostsManager-<arch>.exe` sits alongside each installer if you'd rather run it without
installing.

Not sure which you need? `echo %PROCESSOR_ARCHITECTURE%` reports `AMD64`, `ARM64` or `x86`.

Uninstalling removes the app but deliberately leaves your backups in place.

## Administrator rights

The hosts file lives in `System32` and is owned by `BUILTIN\Administrators`, so writing to
it requires elevation. The app's manifest declares `requireAdministrator`, which means
**one UAC prompt when it launches** and none afterwards — rather than a prompt on every
save.

Two deliberate consequences:

- Saves use `File.Replace`, which **preserves the hosts file's original ACLs**. A plain
  overwrite from an elevated process can leave the file with different permissions than
  Windows shipped it with.
- Backups go to `%LOCALAPPDATA%`, **not** under `System32`. That keeps them readable and
  restorable *without* elevation, which is exactly the situation you're in when something
  has gone wrong.

To rehearse changes without elevation, build without the manifest and point the app at a
copy:

```bash
dotnet build src/HostsManager/HostsManager.csproj -p:NoElevation=true -o build
```

```bash
build/HostsManager.exe --hosts-path C:\temp\hosts-copy --backups-dir C:\temp\backups
```

## Building from source

```bash
./installer/build.ps1
```

Publishes every architecture and builds the installers into `dist`. Requires the WiX 5
CLI (`dotnet tool install --global wix --version 5.0.2` plus
`wix extension add -g WixToolset.UI.wixext/5.0.2`).

.NET cannot emit one binary that runs on every CPU, so each architecture gets its own
executable and installer.

### Command line

| Flag | Effect |
| --- | --- |
| `--hosts-path <path>` | Use a different hosts file. For rehearsing against a copy. |
| `--backups-dir <path>` | Use a different backup directory. |
| `--theme light\|dark` | Pin the theme instead of following Windows. |
| `--restore-latest` | Restore the most recent backup, no UI. |
| `--restore-original` | Restore the pristine pre-app original, no UI. |

The restore flags bypass the single-instance guard on purpose: they're what you reach for
when something is already wrong, so they must never be refused.

## How the file is protected

There is no Windows API for the hosts file — it is plain text, and every tool that manages it
(including this one) rewrites the whole file. Safety comes from the pipeline, not from who does
the writing. Every save passes through these gates, and any failure aborts before the file
changes:

1. **Drift check** — if the file changed on disk since it was loaded (Docker and Tailscale rewrite
   it on their own schedule), the save is refused rather than clobbering those changes.
2. **Render** from the line model. Lines you didn't touch are emitted from their original text
   verbatim.
3. **Re-parse and verify** — the rendered text is parsed back and compared line by line against
   the model it came from. A render bug fails here, in memory, instead of reaching disk.
4. **Structural checks** — no null bytes, no lost trailing newline, no modified read-only line.
5. **Backup** — if the backup can't be written, the save doesn't happen.
6. **Atomic replace** — written to a temp file, flushed to physical disk, then swapped in with
   `File.Replace`, which is atomic on NTFS and preserves the destination's ACLs.
7. **Read back** — the bytes on disk are hashed and compared to what was meant to be written. A
   mismatch triggers an automatic rollback.

The file's **UTF-8 BOM and CRLF line endings are preserved exactly**, never normalized. The status
bar shows the detected encoding as visible proof.

## Backups

Stored in `%LOCALAPPDATA%\HostsManager\backups` — deliberately user-scoped rather than beside the
hosts file in `System32`, so they stay readable and restorable without elevation, which is exactly
the situation you're in when something has gone wrong.

- A backup is taken **before every save**, and before every restore, so a restore can be undone.
- `hosts.original.bak` captures the state before this app ever ran and is **never pruned**.
- The last 50 timestamped backups are kept; each has a `.json` manifest recording the timestamp,
  SHA-256, size, encoding and what triggered it.

### Recovering without the app

Backups are plain text. From an elevated Command Prompt:

```bash
copy /Y "%LOCALAPPDATA%\HostsManager\backups\hosts.original.bak" "%WINDIR%\System32\drivers\etc\hosts"
```

Or run `HostsManager.exe --restore-original`.

Don't rely on System Restore or Volume Shadow Copy for this file — there's no guarantee it's captured.

## Project layout

```
src/HostsManager.Core     Parsing, validation, backups, the write pipeline. No UI dependency.
src/HostsManager          WPF app, tray icon, theming.
tests/HostsManager.Tests  93 tests, run against a real hosts file as a fixture.
installer                 WiX definition and the build script.
dist                      Installers and standalone executables.
```

```bash
dotnet test
```

The load-bearing test is that parsing and re-rendering the real hosts file — BOM, CRLF, Microsoft
header, disabled entries, pasted junk, Docker and Tailscale blocks — produces **byte-identical**
output.

## Known behaviour

The tray icon starts in Windows 11's hidden-icons overflow, like every other new tray
icon. Drag it onto the taskbar via **Taskbar settings → Other system tray icons** to keep
it visible.

Toggling from the tray saves immediately, since there's no Save button out there. If the
window has unsaved changes the tray toggles are disabled instead — saving from the tray
would also commit whatever is pending in the window, which isn't what you asked for.


Enabling an entry that was written as `#⇥127.0.0.1 host` (with whitespace between the `#` and the
IP) and then disabling it again in a **later session** writes back a plain `#127.0.0.1 host`. Once
the line is enabled on disk that whitespace no longer exists anywhere, so it can't be recovered.
Within a single session, including across saves, the original prefix is preserved exactly.
