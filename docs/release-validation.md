# Build and release validation

Use Windows and the exact .NET SDK specified by `global.json` (currently 8.0.424).
Install Git and authenticate GitHub CLI when publishing. PowerShell runs the scripts.

From a clean checkout:

```powershell
dotnet restore --locked-mode
dotnet test --configuration Release --no-restore
dotnet build src/HostsManager/HostsManager.csproj --configuration Release --no-restore
./installer/build.ps1
./installer/Verify-Checksums.ps1
```

The build restores repository-local WiX 5.0.2 and matching UI/Util extensions. It creates
six versioned files in `dist`: a self-contained portable EXE and MSI for x64, x86 and
ARM64. Normal builds and each publish architecture have separate NuGet lock files.
CI tests, builds the desktop app, packages each architecture and retains checked artifacts.
This pins build inputs; it does not promise byte-identical MSI files across builds.

Run `build.ps1 -UpdateDependencies` only when deliberately refreshing dependencies;
review and commit changed lock files, including those for every architecture. Dependabot
checks NuGet and GitHub Actions weekly. Review the SDK, bundled runtime and WiX versions
manually each month and for security advisories. Update `global.json`, tool manifest and
extension versions together as applicable, regenerate locks and repeat the matrix below.
Self-contained apps require a new release to pick up runtime servicing. A11 tracks .NET 10.

## Publishing

On clean master, use `installer/release.ps1 -Version <next-version> -NotesFile <notes>`.
It verifies tests and packages before pushing, creates a draft with six artifacts plus
`SHA256SUMS.txt`, and checks GitHub's recorded sizes and SHA-256 digests before publishing.
An upload or verification failure leaves the release draft for inspection. Do not blindly
rerun the full script after a tag has been pushed. Use `-Draft` to retain a reviewed draft.

To verify downloads, place the six artifacts and `SHA256SUMS.txt` together and run
`installer/Verify-Checksums.ps1 -Directory <download-folder>` from the corresponding tag.
The verifier requires every file listed in the manifest. It rejects malformed records,
duplicate names, missing files and mismatches. A checksum verifies consistency with the
manifest; it is not a publisher signature. Code signing is a separate decision.

## Manual release matrix

Record each result separately for native x64, x86 and ARM64 Windows. Emulation must be
identified explicitly. Compilation and packaging do not count as installation evidence.

| Scenario | Expected result |
| --- | --- |
| Portable startup with a disposable hosts file | Window opens; correct version; no system hosts changes |
| Fresh MSI install | Correct path, shortcut, version and single Programs entry |
| Upgrade from previous version | One Programs entry; settings and backups retained |
| Uninstall | Installed app removed; retention behavior recorded; hosts content unchanged |
| Same-account / alternate-account elevation | Save and backup ownership correct; cancellation truthful |
| Artifact verification | All six sizes and hashes match the published draft |

See [1.0.15 validation](a15-a18-implementation.md) for executed checks and remaining scenarios.
