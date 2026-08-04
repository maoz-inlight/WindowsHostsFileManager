# Decisions

A log of the non-obvious choices made after the initial build, and why. Ordered roughly
by when they came up.

## Elevation: manifest-level, not per-action

`app.manifest` declares `requestedExecutionLevel="requireAdministrator"`, so the app
elevates once at launch rather than prompting on every save. The trade-off is a UAC
prompt the user can't avoid even to just browse the list — accepted because the app's
only real function (writing `System32\drivers\etc\hosts`) always needs it, so deferring
elevation would only mean hitting the same prompt on the first save anyway.

A build-time `NoElevation` MSBuild property strips the manifest, producing an unelevated
build for rehearsal against a copy via `--hosts-path`. This is a dev-only escape hatch,
not shipped in `dist`.

## Logo: a drawn glyph, not a font character

The icon renders `#` as hand-drawn strokes (`IconMaker.cs`-equivalent generation script)
rather than setting a font glyph, because a font's `#` gets inconsistent weight and
alignment across the 16–256px range an `.ico` needs to cover, especially at 16px where
subpixel hinting varies by renderer. Stroke width and spacing are computed as a fraction
of the target size instead, so the mark looks the same shape at every size.

The mark itself — `#` — was picked because it's literally the character the app adds or
removes when toggling an entry, not a generic network/globe icon.

## Theming: DynamicResource + a live registry watch, not a restart

Windows' light/dark preference is read from
`HKCU\...\Themes\Personalize\AppsUseLightTheme` and re-read on `SystemEvents.
UserPreferenceChanged`. Palette colours are `DynamicResource`, not `StaticResource`, so
swapping the merged dictionary at index 0 re-resolves every bound brush without
recreating the window. The alternative — restart the app on a theme change — was rejected
because it would drop in-progress edits.

The title bar is separately painted via `DwmSetWindowAttribute` /
`DWMWA_USE_IMMERSIVE_DARK_MODE`, since WPF has no managed API for it and a dark window
with a stock white caption bar reads as broken rather than themed.

Two stock WPF controls ignore `DynamicResource` palette brushes entirely and needed
explicit retemplating: `ComboBox` (default chrome is hardcoded) and `ScrollBar` (draws
its own light-grey track regardless of resources).

## Packaging: self-contained + single-file, three RIDs

.NET cannot produce one binary that runs on every Windows CPU architecture, so each of
`win-x64`, `win-x86`, `win-arm64` gets its own publish. `--self-contained true` plus
`PublishSingleFile` was chosen over framework-dependent deployment so a recipient doesn't
need a matching .NET runtime installed — the whole point of "an executable I can send."

## Versioning: one number, flowing into the exe and the MSI together

The upgrade mechanism (`MajorUpgrade`, a shared `UpgradeCode`) was in place from the
first installer, but was inert: nothing set the exe's version explicitly, so every build
defaulted to `1.0.0.0` and Windows Installer never saw a version increase to react to.

`Directory.Build.props` at the repo root now holds the single `<Version>`, picked up
automatically by every project under it (MSBuild convention — no per-project wiring
needed). `installer/build.ps1` reads that value by default, or accepts `-Version` to
override it without editing a file, and passes the *same* value explicitly to both
`dotnet publish -p:Version=...` and `wix build -d Version=...` rather than letting the
MSI bind to whatever the exe happened to embed. Explicit beats bound here: a build script
that could fail silently if `dotnet publish` and `wix build` ever computed a version
differently is worse than one where both were told the same number.

The version is constrained to exactly three numeric parts (`Major.Minor.Build`) because
Windows Installer only compares the first three fields of `ProductVersion` when deciding
whether an install is an upgrade — a fourth field is accepted but silently ignored, which
would make a `1.0.0.1` → `1.0.0.2` bump look like a no-op to the installer even though it
built a different exe.

**Verified without a live install**, since testing an elevated install/upgrade
non-interactively isn't possible here: built `1.0.0` and `1.0.1` and inspected the raw
MSI tables via the Windows Installer COM API rather than trusting the build succeeded.
Confirmed the three facts that actually drive an upgrade: `UpgradeCode` identical across
both, `ProductCode` different per build (WiX mints a fresh one automatically), and the
generated `Upgrade` table carries the version-range rows `MajorUpgrade` is meant to
produce — one unconditioned row bounded by `VersionMax` for detecting an older install,
one bounded by `VersionMin` for detecting a newer one (the downgrade guard). Also
confirmed `FindRelatedProducts` and `RemoveExistingProducts` are both present and
unconditioned in `InstallExecuteSequence` — the actions that actually act on what
`Upgrade` detects. All four are exactly what a live install/upgrade would depend on;
reading them directly from the compiled MSI is the same check `msiexec` performs at
install time, just done ahead of running it.

## Installer toolchain: WiX 5, not WiX 7

WiX 7's CLI now requires accepting a paid Open Source Maintenance Fee EULA before it will
run (`WIX7015`). That's not a license decision to make on the user's behalf, so the
installer build pins WiX 5.0.2 instead, which is still fully open source. `installer/
build.ps1` installs it explicitly by version rather than `--global` latest, so a future
`dotnet tool update` can't silently reintroduce the EULA gate.

One shared `UpgradeCode` is used across all three architectures. This is deliberately one
product with three builds, not three products — a machine only ever installs the MSI
matching its own CPU, so there's no scenario where two architectures need to coexist on
one machine, and sharing the code means installing a newer build cleanly replaces an
older one via `MajorUpgrade` rather than stacking duplicate Programs-and-Features entries.

## Single instance: hand off, don't just refuse

A second launch doesn't just exit — it signals the already-running instance to surface
itself (`SingleInstance.ActivationRequested`) before exiting. This matters beyond
avoiding a duplicate window: two instances would each hold their own SHA-256 of the file
as it was when *they* loaded it, so the second instance's drift check would have no idea
the first instance's pending edits exist, and a save from either could silently discard
the other's work at the OS level even though each save individually passes its own
verification.

The mutex and signal are named `Local\...`, not `Global\...` — deliberately session-
scoped. The app always runs elevated on the interactive desktop, so there's no cross-
session (e.g. RDP + console) scenario to cover, and `Local\` avoids requiring the broader
namespace permissions `Global\` needs.

## Tray: hide-on-close, quit only from the menu

Closing the window hides it rather than exiting the process (`ShutdownMode.
OnExplicitShutdown`). Nothing is lost by hiding, so unlike a genuine exit this
deliberately does not ask about unsaved changes — the tray's Exit item is the only path
that actually confirms and ends the process, tracked via a `_exiting` flag set on every
path through `ConfirmExit` (including the trivial "nothing pending" return) so a
subsequent `OnClosing` can tell a real shutdown from a user clicking the window's X.

**Tray quick-toggle only saves when the window is clean.** Toggling from the tray context
menu saves immediately, since there's no Save button out there — but if the window has
unsaved edits pending, the tray's toggle entries are replaced with a disabled "unsaved
changes" notice instead of writing. Saving from the tray in that state would silently
commit whatever the user was still mid-edit on in the window, which is not what a tray
click is asking for.

## What the code review changed

A structured review after the initial tray/theme/installer work found and fixed:

- **`AddEntry` on a file without a trailing newline merged two entries onto one line.**
  The terminator fix-up only touched the document's last line, not every line displaced
  by an insert; fixed to normalize every line, and the parser's own re-parse-and-verify
  step (see design.md) is what caught it as a save-time failure rather than a silent
  corruption.
- **Non-UTF-8 bytes were silently rewritten as `U+FFFD`.** This is the reason the write
  pipeline now includes an explicit encode/decode round-trip check (`FileFormat.
  CanRoundTrip`) as its own gate, and why decoding fell back to Latin-1 instead of a
  lossy UTF-8 decode — see *Encoding is not always UTF-8* in design.md.
- **Reload discarded unsaved changes with no confirmation**, the one destructive action
  in the app that didn't ask first. Now routed through the same confirm hook Delete uses.
  Verified interactively: cancelling the resulting dialog preserves the pending edit
  exactly; confirming discards it and returns the grid to the on-disk state.
  **Nuance found only by driving the real UI:** the confirmation is a native `MessageBox`
  — a separate top-level window owned by the main one — so a screenshot of the main
  window's handle via `PrintWindow` doesn't show it even while it's correctly open and
  blocking. Verifying it existed required enumerating top-level windows for the process
  directly, not just screenshotting.
- **A failed tray toggle left a phantom unsaved-change count.** Reverting the toggle
  after a failed save re-marked the line as modified, so the header could claim "1
  unsaved change" that had no corresponding content difference and could never actually
  be saved. `HostsDocument.ClearPendingMarkers()` was added specifically for this revert
  path, guarded so it can only be called when the document's rendered content already
  matches its saved baseline.
- **The single-instance listener thread could fault the process on exit.** `Dispose()`
  disposed its wait handles immediately after signalling cancellation, racing a listener
  thread that might already be past its cancellation check and about to wait on those
  same handles. Fixed by joining the listener thread before disposing anything it might
  still be using.
- **A fixed temp filename could collide with a headless restore.** `--restore-original`
  and `--restore-latest` deliberately bypass the single-instance guard, since a broken
  hosts file is exactly the situation where you don't want to be told "another instance
  is already open." That means a recovery run can legitimately overlap a save from an
  open window; the write pipeline's temp filename now includes the process ID so the two
  can't contend for the same file.
- **The "loopback only" filter matched only the literal string `127.`**, missing `::1`
  and the rest of `127.0.0.0/8`. Switched to `IPAddress.IsLoopback`.

Each of these has a regression test named after the scenario in `tests/HostsManager.
Tests/RegressionTests.cs`, so the fix is pinned rather than just narratively described.
