<#
.SYNOPSIS
    Cuts a release: bumps the version, builds every installer, tags, and publishes a
    GitHub release with all six artifacts attached.

.DESCRIPTION
    The steps are ordered so that a failure leaves nothing half-published. Everything
    that can fail locally — the preflight checks, the tests, the three installer builds —
    runs before the first irreversible action. If any of it fails, the version bump is
    rolled back and the working tree is exactly where it started.

    Past the push, failures are reported rather than undone: a tag that has reached the
    remote may already have been fetched by someone, and deleting it would be worse than
    leaving it.

    Requires the WiX 5 CLI (see installer/build.ps1) and an authenticated `gh`.

.EXAMPLE
    ./installer/release.ps1 -Version 1.0.5

.EXAMPLE
    ./installer/release.ps1 -Version 1.1.0 -NotesFile notes.md -Draft
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,

    # Hand-written release notes. Without one the notes are generated from the commit
    # log, which is a weaker changelog than the ones this project has shipped so far.
    [string]$NotesFile,

    # Publishes as a draft so the notes and assets can be checked before anyone sees it.
    [switch]$Draft,

    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$distRoot = Join-Path $repoRoot 'dist'
$tag = "v$Version"

function Step($text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Fail($text) { throw $text }

<#
    Windows PowerShell wraps a native command's stderr in an ErrorRecord, which under
    ErrorActionPreference=Stop aborts the script even when the command succeeded or when
    its exit code is the thing being tested — `git push` and `gh release view` both write
    to stderr routinely. These two helpers drop to Continue for the duration of the call
    and judge the command by its exit code, which is the only signal that means anything
    here.
#>
function Test-Native {
    param([string]$Exe, [string[]]$Arguments)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Exe @Arguments *> $null } finally { $ErrorActionPreference = $previous }
    return $LASTEXITCODE -eq 0
}

function Invoke-Native {
    param([string]$Exe, [string[]]$Arguments, [string]$FailMessage)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Exe @Arguments } finally { $ErrorActionPreference = $previous }

    if ($LASTEXITCODE -ne 0) {
        if ($FailMessage) { Fail $FailMessage }
        Fail "$Exe $($Arguments -join ' ') exited with $LASTEXITCODE."
    }
}

# Same three-part rule build.ps1 enforces: Windows Installer ignores a fourth field when
# deciding whether a build is an upgrade, so a release with one wouldn't upgrade cleanly.
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Fail "Version '$Version' must be three numeric parts (e.g. 1.0.5)."
}

if ($NotesFile) {
    if (-not (Test-Path $NotesFile)) { Fail "Notes file not found: $NotesFile" }
    $NotesFile = (Resolve-Path $NotesFile).Path
}

Push-Location $repoRoot
try {
    Step 'Preflight'

    if (-not (Test-Native 'gh' @('auth', 'status'))) { Fail 'gh is not authenticated. Run: gh auth login' }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'master') { Fail "On branch '$branch'. Releases are cut from master." }

    # Uncommitted work would be built into the artifacts but absent from the tagged
    # commit, so the release wouldn't be reproducible from its own tag.
    if (git status --porcelain) {
        git status --short
        Fail 'Working tree is dirty. Commit or stash first.'
    }

    Invoke-Native 'git' @('fetch', 'origin', '--tags', '--quiet') 'git fetch failed.'

    # Being ahead of origin is the normal case — those are the commits being released.
    # Being behind or diverged is not: the tag would name a commit that drops work.
    if (-not (Test-Native 'git' @('merge-base', '--is-ancestor', 'origin/master', 'HEAD'))) {
        Fail 'Local master is behind or has diverged from origin/master. Pull first.'
    }

    if (git tag --list $tag) { Fail "Tag $tag already exists locally." }
    if (git ls-remote --tags origin $tag) { Fail "Tag $tag already exists on origin." }
    if (Test-Native 'gh' @('release', 'view', $tag)) { Fail "Release $tag already exists on GitHub." }

    $current = (Select-String -Path $propsPath -Pattern '<Version>([^<]+)</Version>' |
        Select-Object -First 1).Matches[0].Groups[1].Value
    Write-Host "  $current -> $Version"
    Write-Host "  notes: $(if ($NotesFile) { $NotesFile } else { 'generated from commits' })"

    # ---- Everything below is still reversible until the push. ----

    Step 'Bumping the version'
    (Get-Content $propsPath -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" |
        Set-Content $propsPath -Encoding utf8 -NoNewline

    try {
        if (-not $SkipTests) {
            Step 'Tests'
            Invoke-Native 'dotnet' @(
                'test', (Join-Path $repoRoot 'tests\HostsManager.Tests'),
                '--configuration', 'Release', '--nologo', '--verbosity', 'quiet'
            ) 'Tests failed. Nothing was released.'
        }

        Step 'Installers'
        & (Join-Path $PSScriptRoot 'build.ps1') -Version $Version

        $assets = @('x64', 'x86', 'arm64') |
            ForEach-Object { "HostsManager-$_.msi", "HostsManager-$_.exe" } |
            ForEach-Object {
                $path = Join-Path $distRoot $_
                if (-not (Test-Path $path)) { Fail "build.ps1 did not produce $_" }
                $path
            }
    }
    catch {
        Write-Host "`nRolling back the version bump." -ForegroundColor Yellow
        git checkout -- $propsPath
        throw
    }

    # ---- Irreversible from here. ----

    Step "Tagging and pushing $tag"
    Invoke-Native 'git' @('commit', '--quiet', '-am', "Bump to $tag") 'git commit failed.'
    Invoke-Native 'git' @('tag', $tag) 'git tag failed.'
    Invoke-Native 'git' @('push', '--quiet', 'origin', 'master') 'git push failed. The commit and tag are local only.'
    Invoke-Native 'git' @('push', '--quiet', 'origin', $tag) "Pushing $tag failed. master is pushed; the tag is local only."

    Step 'Publishing the release'
    $ghArgs = @('release', 'create', $tag, '--title', $tag) + $assets
    if ($NotesFile) { $ghArgs += @('--notes-file', $NotesFile) } else { $ghArgs += '--generate-notes' }
    if ($Draft) { $ghArgs += '--draft' }

    Invoke-Native 'gh' $ghArgs ("gh release create failed, but $tag is already pushed. Re-run just the upload:`n" +
        "  gh release create $tag dist/* --title $tag")

    Step 'Done'
    if ($Draft) {
        Write-Host 'Published as a DRAFT — nobody sees it until you publish it in the GitHub UI.' -ForegroundColor Yellow
    }
}
finally {
    Pop-Location
}
