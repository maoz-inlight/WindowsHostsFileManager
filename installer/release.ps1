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

# Same three-part rule build.ps1 enforces: Windows Installer ignores a fourth field when
# deciding whether a build is an upgrade, so a release with one wouldn't upgrade cleanly.
if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    Fail "Version '$Version' must be three numeric parts (e.g. 1.0.5)."
}

if ($NotesFile -and -not (Test-Path $NotesFile)) { Fail "Notes file not found: $NotesFile" }

Push-Location $repoRoot
try {
    Step 'Preflight'

    gh auth status 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail 'gh is not authenticated. Run: gh auth login' }

    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    if ($branch -ne 'master') { Fail "On branch '$branch'. Releases are cut from master." }

    # Uncommitted work would be built into the artifacts but absent from the tagged
    # commit, so the release wouldn't be reproducible from its own tag.
    if (git status --porcelain) {
        git status --short
        Fail 'Working tree is dirty. Commit or stash first.'
    }

    git fetch origin --tags --quiet
    if ($LASTEXITCODE -ne 0) { Fail 'git fetch failed.' }

    # Being ahead of origin is the normal case — those are the commits being released.
    # Being behind or diverged is not: the tag would name a commit that drops work.
    git merge-base --is-ancestor origin/master HEAD
    if ($LASTEXITCODE -ne 0) {
        Fail 'Local master is behind or has diverged from origin/master. Pull first.'
    }

    if (git tag --list $tag) { Fail "Tag $tag already exists locally." }
    if (git ls-remote --tags origin $tag) { Fail "Tag $tag already exists on origin." }

    gh release view $tag 2>&1 | Out-Null
    if ($LASTEXITCODE -eq 0) { Fail "Release $tag already exists on GitHub." }

    $current = (Select-String -Path $propsPath -Pattern '<Version>([^<]+)</Version>' |
        Select-Object -First 1).Matches[0].Groups[1].Value
    Write-Host "  $current -> $Version"

    # ---- Everything below is still reversible until the push. ----

    Step 'Bumping the version'
    $bumped = $false
    (Get-Content $propsPath -Raw) -replace '<Version>[^<]+</Version>', "<Version>$Version</Version>" |
        Set-Content $propsPath -Encoding utf8 -NoNewline
    $bumped = $true

    try {
        if (-not $SkipTests) {
            Step 'Tests'
            dotnet test (Join-Path $repoRoot 'tests\HostsManager.Tests') `
                --configuration Release --nologo --verbosity quiet
            if ($LASTEXITCODE -ne 0) { Fail 'Tests failed. Nothing was released.' }
        }

        Step 'Installers'
        & (Join-Path $PSScriptRoot 'build.ps1') -Version $Version

        $expected = @('x64', 'x86', 'arm64') | ForEach-Object { "HostsManager-$_.msi", "HostsManager-$_.exe" }
        $assets = $expected | ForEach-Object {
            $path = Join-Path $distRoot $_
            if (-not (Test-Path $path)) { Fail "build.ps1 did not produce $_" }
            $path
        }
    }
    catch {
        if ($bumped) {
            Write-Host "`nRolling back the version bump." -ForegroundColor Yellow
            git checkout -- $propsPath
        }
        throw
    }

    # ---- Irreversible from here. ----

    Step "Tagging and pushing $tag"
    git commit --quiet -am "Bump to $tag"
    if ($LASTEXITCODE -ne 0) { Fail 'git commit failed.' }

    git tag $tag
    if ($LASTEXITCODE -ne 0) { Fail 'git tag failed.' }

    git push --quiet origin master
    if ($LASTEXITCODE -ne 0) { Fail 'git push failed. The commit and tag are local only.' }

    git push --quiet origin $tag
    if ($LASTEXITCODE -ne 0) { Fail "Pushing $tag failed. master is pushed; the tag is local only." }

    Step 'Publishing the release'
    $args = @('release', 'create', $tag, '--title', $tag) + $assets
    if ($NotesFile) { $args += @('--notes-file', (Resolve-Path $NotesFile).Path) }
    else { $args += '--generate-notes' }
    if ($Draft) { $args += '--draft' }

    gh @args
    if ($LASTEXITCODE -ne 0) {
        Fail "gh release create failed, but $tag is already pushed. Re-run just the upload:`n" +
             "  gh release create $tag dist/* --title $tag"
    }

    Step 'Done'
    if ($Draft) { Write-Host 'Published as a DRAFT — nobody sees it until you publish it in the GitHub UI.' -ForegroundColor Yellow }
    gh release view $tag --web 2>&1 | Out-Null
}
finally {
    Pop-Location
}
