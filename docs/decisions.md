# Decisions

A log of the non-obvious choices made after the initial build, and why. Ordered roughly
by when they came up.

## Elevation: per-write helper, not the desktop UI

`app.manifest` declares `requestedExecutionLevel="asInvoker"`. The long-lived WPF process,
tray icon, and isolated browser therefore share the desktop user's ordinary token. Saving
or restoring the real `System32\drivers\etc\hosts` file launches the same executable with
`runas` and a one-use request; that elevated process performs one write and exits.

The first implementation elevated the whole app once at launch. That avoided repeated UAC
prompts, but it made Chromium launch through a synthetic downgraded token. Edge and Chrome
both rejected that token in the field with `0x80000003` breakpoint crashes. Running the UI
normally fixes the root cause and also reduces the amount of code that holds administrator
rights.

The helper is not a general elevated file copier: it accepts only the canonical Windows
hosts path and the app's default backup directory. It independently decodes, parses, and
verifies the handed-off bytes, repeats the drift check using the UI's loaded SHA-256, takes
the backup, atomically replaces the file, reads it back, and rolls back on failure. A custom
`--hosts-path` remains entirely in-process and does not ask for elevation.

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
  be saved. Patched at the time with a `ClearPendingMarkers()` call on the revert path;
  see *Pending is a comparison, not a flag* below for why that patch was later replaced.
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

## Pending is a comparison, not a flag

The phantom-unsaved-change bug above came back through the front door: disable an entry in
the window, change your mind, enable it again, and the row sat there reading "Pending"
with the header claiming an unsaved change — while Save was correctly greyed out, because
the document matched the file and there was genuinely nothing to write. Two parts of the
UI disagreeing about whether there was work to do, with no way to resolve it but a reload.

The tray had already hit this and been patched locally, which is what gave it away: the
fault was never in either call site but in `HostsLine.IsModified` being a flag that
mutations latched to `true` and only a save could clear. A flag records that something
*happened*; the question the UI actually asks is whether the line *differs* from the file.
Those come apart the moment an edit is undone, and every mutation path had to remember to
un-latch — the tray remembered, the grid didn't.

So each line now keeps its rendered text as of the last load or save, and `IsModified`
compares against it. Undo becomes self-correcting everywhere at once: toggling back
restores the line's exact original bytes — including a captured disable prefix like
`#\t`, which is why the round-trip is byte-exact and not merely equivalent — so the line
stops differing and stops being pending, with no call site involved. `ClearPendingMarkers`
existed only to un-latch the flag and was deleted with it.

The comparison is per line, not per document, which is the property the document-level
`IsDirty` check could not have given: undoing one of two pending toggles clears that row
and leaves the other one pending, rather than clearing both or neither.

There is exactly one thing a line cannot settle on its own: a line that was *inserted* has
no committed text to compare against, so it is pending by construction — right up until
the inserts and removals add back up to the file already on disk, which importing a file
identical to the current one does. Only the document can see that, so the mutations that
change the line list end with `SettleIfUnchanged()`, which re-baselines everything when
`IsDirty` says there is nothing left to write. That is the invariant the whole section is
really about, and it is worth stating plainly: **the header may never claim unsaved
changes that Save is disabled for.** Those two came from different sources, and the bug
was visible precisely where they disagreed.

## The install experience: two things that looked fine and weren't

Feedback after shipping `v1.0.0`: the installer visibly did nothing on completion, and
the installed app was hard to find afterward. Both traced back to the same shortcut.

- **The Start Menu shortcut used `Advertise="yes"`.** Advertised shortcuts resolve their
  target through Windows Installer at launch time rather than pointing at the exe
  directly, and Windows Search's app index doesn't reliably surface them as a launchable
  app — the entry that *did* show up was Programs and Features, which reads as "only the
  uninstaller is there." Switched to a plain shortcut with a real `Target`.
- **The exit dialog had nothing to confirm.** `WixUI_InstallDir`'s stock Finish page is
  just "click Finish to exit the wizard," with no indication anything was installed.
  Added a checked-by-default "Launch Hosts manager" box, wired to a `WixShellExec`
  custom action. Getting the reference right took a real build-and-inspect loop: the
  natural-looking `<CustomActionRef Id="WixShellExec" />` doesn't resolve — the util
  extension exposes the shell-exec entry point as a `DllEntry` on a versioned binary
  (`Wix4UtilCA_$(sys.BUILDARCHSHORT)`), not as a standalone referenceable custom action,
  so the fix defines a local `CustomAction` pointing at that `DllEntry` and publishes
  *that* action's Id on the Finish button instead.

Also fixed while in there: the shipped exe's `FileVersionInfo.ProductVersion` carried a
`+<git-sha>` suffix the .NET SDK appends by default whenever the project builds inside a
git repo, so the exe's own version disagreed with the clean `1.0.0` WiX puts in the MSI's
`ProductVersion`. `Directory.Build.props` now sets
`IncludeSourceRevisionInInformationalVersion=false` repo-wide, so every surface — the
About dialog, Explorer's file Properties, the MSI — reports the same three-part number.

## About dialog: reachable from the window and the tray

Added a small info-glyph button in the header (next to the unsaved-changes indicator)
and an "About" item in the tray menu, both opening the same dialog: name, version (read
from the running exe's own `ProductVersion`, so it can never drift from what's actually
installed), and a shortcut to the backups folder. The tray path has no window to own the
dialog, so it re-uses `ShowMainWindow` first — clicking About from the tray brings the
main window forward too, rather than popping a dialog with no visible owner.

The dialog's height is `SizeToContent="Height"` rather than a fixed number — an earlier
fixed-height version silently clipped the last line of the install-path box by a few
pixels, invisible without owner-window screenshots since the containing `Border` doesn't
report a layout error, it just runs out of room.

## Row selection: a border, not a background swap

Selecting a row swapped its background to `Surface1`, which in the dark palette sits only
a few RGB points from the default `Surface2` — close to invisible. Worse, on an Invalid or
Pending row the swap *replaced* the red/blue state colour outright, so selecting a flagged
row could read as losing its warning rather than gaining a selection. Switched to a 2px
`Accent`-coloured border on the row instead: a different property than `Background`, so it
layers on top of whatever state colour is already there instead of competing with it.
Confirmed visually against both a Normal and an Invalid row before shipping.

The same pass added a real delete affordance to unparseable rows — the warning icon was a
static glyph with nothing clickable on the row, so removing one meant knowing the toolbar
Delete button existed and worked on whatever was selected. It's now a button wired through
`MainViewModel.DeleteRowCommand`, which shares the same confirm-before-delete path as the
toolbar (`DeleteEntry`, extracted from the old `Delete()` so both call sites use it).

## The "broken installer" that wasn't: .msi vs. portable .exe

Reported twice: the installer doesn't put the app in Program Files, and never shows a
wizard. Re-reading `Package.wxs` and querying the compiled `dist/HostsManager-x64.msi`
directly via the Windows Installer COM API (`Directory`, `Dialog`, `InstallUISequence`,
`Property` tables — the same read-only technique used earlier to verify the upgrade
mechanism) showed nothing wrong: `INSTALLFOLDER` resolves through
`ProgramFiles6432Folder` to the real Program Files, `ALLUSERS=1`, and the full
`WixUI_InstallDir` wizard is correctly sequenced. No Group Policy or Windows Installer
registry setting on the machine was suppressing UI either, and the `.msi` file
association was the untouched Windows default.

The actual file involved — found by checking Downloads — was `HostsManager-arm64.exe`,
not an `.msi`: the **portable build**, downloaded from the stale `v1.0.0` GitHub release
(its `ProductVersion` still carried the pre-fix `+<git-sha>` suffix). Running it was never
going to show install UI, because it isn't an installer — no Program Files entry, no
Start Menu shortcut, nothing to wizard through, by design. Confirmed once the right file
(`HostsManager-arm64.msi`) was run: it showed the full wizard correctly.

The artifact names later followed from the same incident: they are now
`HostsManager-<version>-<arch>-Setup.msi` and `HostsManager-<version>-<arch>-Portable.exe`
rather than `HostsManager-<arch>.msi`/`.exe`. A file in Downloads is separated from the
release page that explained it, so the name has to carry both facts on its own — which
build it is, and whether it installs anything. The version also makes a stale download
self-evident, which the `v1.0.0` case above was not.

Two changes followed from this, neither a code fix: the README's Installing section now
states the `.msi`/`.exe` distinction up front rather than mentioning the portable build
as an aside, and calls out that an unsigned, un-code-signed build will trigger a
SmartScreen prompt on first run — expected, not a sign of a broken build. And `v1.0.1`
was cut specifically so the GitHub release matches what's actually been fixed, rather
than leaving `v1.0.0` — already known to be stale — as the only thing to download.

## Tray menu: a themed WPF popup, not ContextMenuStrip

The tray icon's right-click menu was `System.Windows.Forms.ContextMenuStrip` — stock OS
chrome, since `NotifyIcon` only exists in WinForms and the menu came bundled with it,
untouched by the app's own `DynamicResource` palette used everywhere else. Replaced with
a borderless WPF popup ([Views/TrayMenu.xaml](../src/HostsManager/Views/TrayMenu.xaml)),
styled the same way as the `FlatComboBox` dropdown already in `Controls.xaml`: rounded
`Surface2` border, `DropShadowEffect` for elevation, and the same `ToggleSwitch` style the
main grid uses for the domain rows, so toggling from the tray looks like the same control
instead of a checkmark on a menu item.

Rows are five small record types (`TrayHeaderRow`, `TrayActionRow`, `TrayToggleRow`,
`TrayTextRow`, `TraySeparator`) rendered via per-type `DataTemplate`s, built fresh by
`TrayIcon.BuildRows()` on every open — same data and same close-over-`QuickToggle` logic
as the old `BuildMenu()`/`AddToggleSection()`, just emitting these records instead of
`ToolStripMenuItem`s. `NotifyIcon` keeps only the icon; `MouseUp` with the right button
now triggers the popup directly, replacing the free auto-open `ContextMenuStrip` gave.

**Ambiguous-type risk, avoided by construction.** `UseWindowsForms` and `UseWPF` together
mean `Button`, `CheckBox`, and several other names exist in both namespaces and are
ambiguous in plain C#. Rather than fully-qualifying or aliasing every one in code (as
`TrayIcon.cs` already does for its handful of WinForms calls), `TrayMenu`'s rows are
declared entirely as XAML `DataTemplate`s — XAML resolves against the WPF `presentation`
namespace only, so the ambiguity never comes up. The one place C# constructs UI-adjacent
values is `ShowNear`'s positioning math, which uses `System.Drawing.Point`/`Rectangle`
(passed in from `TrayIcon.cs`, which already deals in WinForms screen types) — no WPF
control types touched there either.

### Two bugs the first round of "verification" completely missed

Windows 11's tray overflow flyout doesn't respond to synthetic `Invoke`/mouse input (UI
Automation's own `InvokePattern` and a real `SendInput`-level click at the flyout
chevron's exact reported coordinates both did nothing), so the popup was first "verified"
via a temporary `F9` hook wired straight to `ShowMenu()`, screenshotted with `PrintWindow`
on the popup's handle. It looked perfect in both themes. It was also completely broken.

**`PrintWindow` renders a window that isn't on screen.** It draws from the window's own
device context, so a window positioned far outside the desktop bounds screenshots exactly
like a visible one. The capture proved the popup's *styling* was right and proved nothing
at all about whether it ever appeared — and it hadn't been appearing. The `F9` hook also
sidestepped the real trigger path entirely, so it couldn't have caught the second bug
either. Both only surfaced when the app was actually driven by hand from the tray.

1. **Right-click never reached the app.** `NotifyIcon.MouseUp` doesn't reliably receive
   `WM_RBUTTONUP` for an icon living in the hidden-icons overflow — the shell proxies that
   click differently than one on a directly-visible icon. `ContextMenuStrip.Opening` *is*
   forwarded correctly through the overflow, so a `ContextMenuStrip` is still attached
   purely as a right-click signal, immediately cancelled (`e.Cancel = true`) and never
   shown, with the real popup opened in its place. Showing it must also be deferred to the
   next dispatcher cycle: doing it synchronously inside `Opening` races Explorer's own
   teardown of the overflow flyout.

2. **The popup opened off-screen on any scaled display.** `Cursor.Position` and
   `Screen.WorkingArea` are physical pixels; WPF's `Left`/`Top` are device-independent
   units. Assigning one to the other silently works at 100% scaling and fails everywhere
   else. On the 150%-scaled monitor this was found on, the working area is 3840px wide but
   only 2560 DIP — a click at physical X=3323 became DIP 3323, which Windows scaled to
   ~4985 physical, roughly 1100px past the screen edge. Positioning now goes through
   `GetWindowRect`/`SetWindowPos` so every value in the calculation is a physical pixel.

Confirmed by adding temporary file tracing to the popup's lifecycle, right-clicking the
real tray icon, and reading back the actual numbers (`area={3840x2088} size=345x236 ->
3323,1623 IsVisible=True IsActive=True`) rather than trusting another screenshot. The
lesson worth keeping: a UI screenshot taken through a path the user never exercises tests
the rendering, not the feature.

## Second review pass: the write path

### Windows 11 as the floor, enforced by the installer

The MSI allowed `VersionNT >= 601` — Windows 7 — while the app bundles .NET 8, which
[doesn't run on Windows 7 or 8.1 at all](https://learn.microsoft.com/en-us/dotnet/core/install/windows#supported-versions).
The install would succeed and the app would then fail to start. The floor is now Windows
11, which is higher than .NET 8 strictly requires (it supports Windows 10 1607+) and is a
deliberate choice: this is only developed and tested against Windows 11.

**Do not use `VersionNT` or `WindowsBuild` for this.** `msiexec.exe` is not manifested for
Windows 10/11, so `GetVersionEx` shims it: on a real Windows 11 build-26200 machine it
reports `VersionNT = 603` and `WindowsBuild = 9600` — Windows 8.1's numbers. The build
number comes from `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\CurrentBuildNumber`
instead, which isn't shimmed. That key is shared rather than WOW64-redirected, so the same
search works from the x86 package (verified, not assumed).

It's compared as a string, because MSI evaluates any string-to-integer comparison as
false. That's safe only because every Windows 10 (10240–19045) and Windows 11 (22000+)
build number is five digits, so lexicographic order matches numeric order — worth
revisiting if build numbers ever reach six digits. An empty result fails the comparison,
so a failed search refuses the install rather than waving it through.

**How the first attempt shipped broken.** The original condition was
`VersionNT >= 1000 AND WindowsBuild >= 22000`, "verified" by opening the built MSI through
the `WindowsInstaller` COM API from PowerShell and evaluating the condition there. That
returned the *true* `1000/26200` and passed — because an in-process COM call runs under
**PowerShell's** manifest, which is Windows 10+ aware, while the actual install runs under
**msiexec's**, which isn't. v1.0.3 shipped a gate that rejected every machine on earth,
including the Windows 11 box it was "tested" on.

Exactly the same failure shape as the `PrintWindow` tray-menu bug above: a check performed
through a path the real user never takes. The fix both times was to drive the genuine path
— here, `msiexec /i … /qn /l*v` and reading `Property(S):` out of the verbose log, which is
now how any change to this condition gets checked.

### One write path, so save and restore can't diverge

`Restore()` claimed to run "the same verified pipeline" as `Save()` but had drifted: no
post-write hash comparison, and its failure handler called `TryRestore` while discarding
the result, then reported "The hosts file is unchanged" — a message that would be a lie
precisely when the rollback had also failed and the user most needed the truth.

Both now go through one private `Commit`, which backs up, replaces, reads back, and rolls
back on failure. `RolledBack` reports what actually happened, naming the backup file and
the `--restore-latest` flag when the rollback itself failed.

Restore opts out of exactly one gate, the drift check, via `refuseOnDrift: false`. That's
deliberate rather than an oversight: a save writes a whole file built from a model captured
at load time, so unrelated external edits would be destroyed silently — refusing is the
safe answer. A restore overwrites *on purpose*, the user asked for it explicitly, and
`Commit` backs up the current bytes first, so nothing is lost. Refusing there would only
ever fire in the exact situation restore exists to get you out of.

### The ACL guarantee applied to the fallback too

`ReplaceFile` used `File.Replace` (which preserves the destination's ACL) and silently fell
back to `File.Move` when that threw. A move keeps the *source* file's permissions, so the
hosts file would quietly inherit whatever the temp file picked up from the `etc` directory
— while the method's own doc comment and the README both promised ACL preservation.

The fallback now captures the destination's access rules first and reapplies them after the
move, and raises if it can't. Silently loosening permissions on a file in System32 as a
side effect of an apparently successful save is not an acceptable outcome. The catch was
also narrowed from `catch (Exception)` to the three types `Replace` actually throws when a
filesystem or security product refuses it.

`HostsManager.Core` targets plain `net8.0` with `TreatWarningsAsErrors`, so the ACL calls
are behind `OperatingSystem.IsWindows()` guards — without them CA1416 fails the build.

### Backup manifests describe their own contents

The entry count and encoding were parameters, which let them disagree with the bytes they
were filed against: a save stamped the backup of the *pre*-save file with the *post*-edit
entry count, and a pre-restore backup was labelled with the encoding of the backup being
restored — a different file entirely. Recovery still worked; the metadata just lied.
`BackupManager.Write` now derives both from the bytes it is given, so they cannot drift.

Two regression tests pin this, each written to prove it discriminates: both assert the old
buggy value and the correct value differ within the same test, so neither could pass
against the previous code.

### CI

There was none. `.github/workflows/ci.yml` runs on `windows-latest` — mandatory, since the
app is WPF and the installer is WiX. It runs the test suite first (fail fast on logic), then
builds the WPF project, which nothing else in the run would compile: the tests reference
only `HostsManager.Core`, so a broken XAML binding would otherwise sail through green. It
finishes with one x64 installer build to catch broken WiX authoring. Every step was run
locally first — a CI file that has never been executed is a guess, not a safety net.
