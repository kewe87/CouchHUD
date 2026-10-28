#requires -Version 7
# Signs the checksum file of a CouchHUD release, the last step of a release. CI cannot do it: the private key
# only exists on the maintainer's PC (see sign-file.ps1). Without CouchHud-Setup-<v>.exe.sha256.sig an installed
# CouchHUD refuses to install the update. The signature is what makes every installed CouchHUD trust that file,
# so this script signs nothing it has not checked:
#
#   1. The tag points to a commit you recognise (you type its first 8 characters), and that commit's
#      Directory.Build.props carries the tag's version.
#   2. The setup is downloaded and hashed here. The hash must equal the digest GitHub computed for the upload
#      (mandatory) and the one line of the .sha256 file (<64 hex>, two spaces, setup name; nothing else).
#   3. The setup has a build provenance attestation from this repository's workflow (gh attestation verify).
#      A gh without that command stops the release; only -SkipAttestation (explicit, loud) goes on without the check.
#
#   Push the tag v0.4.0: CI builds the setup and attaches the setup and its .sha256 to the release.
#   .\scripts\sign-release.ps1 -Tag v0.4.0   # checks, signs the .sha256 (context "release") and uploads the .sha256.sig
#
# Needs the GitHub CLI (gh), signed in with write access to the repository.
param(
    [Parameter(Mandatory)] [string] $Tag,
    [string] $Repo = 'kewe87/CouchHUD',
    # Default: a release without a verified build attestation is not signed. Only for a gh that cannot check one (and a setup you built yourself).
    [switch] $SkipAttestation,
    [string] $KeyFile = (Join-Path $env:USERPROFILE '.couchhud\catalog-signing.key')
)

$ErrorActionPreference = 'Stop'
if ($Tag -notmatch '^v\d+\.\d+\.\d+([.-][0-9A-Za-z.-]+)?$') { throw "Tag '$Tag' does not look like v1.2.3" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) not found.' }

# First step: a release never ships with a catalog index whose signature is still to be renewed (catalog/index.json.unsigned).
$repoRoot = Split-Path -Parent $PSScriptRoot
if (Test-Path (Join-Path $repoRoot 'catalog\index.json.unsigned')) { throw 'catalog/index.json.unsigned exists: sign catalog/index.json (scripts/catalog-sign.ps1) and delete the marker before a release.' }
& git -C $repoRoot cat-file -e "${Tag}:catalog/index.json.unsigned" 2>$null
if ($LASTEXITCODE -eq 0) { throw "The commit of $Tag still carries catalog/index.json.unsigned: renew the catalog signature, delete the marker and tag again." }

# The signature must match the index (checked with the public key pinned in the code): a stale one never ships.
$env:RELEASE = '1'
& dotnet test (Join-Path $repoRoot 'tests\CouchHud.Core.Tests') -c Release --nologo -v quiet --filter 'FullyQualifiedName~The_example_index_in_the_repository_parses'
$catalogCheck = $LASTEXITCODE
Remove-Item Env:\RELEASE
if ($catalogCheck -ne 0) { throw 'catalog/index.json.sig does not match catalog/index.json: sign it again (scripts/catalog-sign.ps1).' }

function Invoke-Gh {
    $output = & gh @args
    if ($LASTEXITCODE -ne 0) { throw "gh $($args -join ' ') failed." }
    $output
}

$version = $Tag.TrimStart('v')
$setupName = "CouchHud-Setup-$version.exe"
$sumName = "$setupName.sha256"

# 1. Tag -> commit
$ref = (Invoke-Gh api "repos/$Repo/git/ref/tags/$Tag" --jq '.object.type + " " + .object.sha') -split ' '
$commit = $ref[1]
if ($ref[0] -eq 'tag') { $commit = Invoke-Gh api "repos/$Repo/git/tags/$commit" --jq '.object.sha' }
$info = Invoke-Gh api "repos/$Repo/commits/$commit" --jq '.sha + "  " + .commit.author.date + "  " + .commit.author.name + " | " + (.commit.message | split("\n")[0])'
Write-Host "Tag $Tag -> commit $info"
$props = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(((Invoke-Gh api "repos/$Repo/contents/Directory.Build.props?ref=$commit" --jq '.content') -join '').Replace("`n", '')))
if ($props -notmatch '<Version>([^<]+)</Version>' -or $Matches[1] -ne $version) { throw "Directory.Build.props at that commit has version '$($Matches[1])', not $version." }
$answer = Read-Host "Type the first 8 characters of the commit to confirm you built and want to release exactly this"
if ($answer.Trim().ToLowerInvariant() -ne $commit.Substring(0, 8)) { throw 'Not confirmed.' }

$work = Join-Path ([IO.Path]::GetTempPath()) "couchhud-sign-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $work | Out-Null
try {
    # 2. Setup and checksum file, three ways to the same hash
    Invoke-Gh release download $Tag --repo $Repo --pattern $setupName --pattern $sumName --dir $work | Out-Null
    $setup = Join-Path $work $setupName
    $file = Join-Path $work $sumName
    if (-not (Test-Path $setup) -or -not (Test-Path $file)) { throw "Release $Tag lacks $setupName or $sumName (is the CI build finished?)." }

    $local = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    $digest = (Invoke-Gh release view $Tag --repo $Repo --json assets --jq ".assets[] | select(.name == `"$setupName`") | .digest") -join ''
    if ($digest -notmatch '^sha256:([0-9a-f]{64})$') { throw "GitHub reports no SHA-256 digest for $setupName ('$digest'): refusing to sign." }
    if ($Matches[1] -ne $local) { throw "The downloaded setup ($local) differs from GitHub's digest ($($Matches[1]))." }

    $lines = @([IO.File]::ReadAllText($file).Split("`n") | ForEach-Object { $_.TrimEnd("`r") } | Where-Object { $_.Length -gt 0 })
    if ($lines.Count -ne 1) { throw "$sumName must have exactly one line, it has $($lines.Count)." }
    if ($lines[0] -cne "$local  $setupName") { throw "$sumName says '$($lines[0])', expected '$local  $setupName'." }
    Write-Host "Setup, GitHub digest and $sumName agree: $local"

    # 3. Provenance
    & gh attestation --help *> $null
    if ($LASTEXITCODE -eq 0) {
        & gh attestation verify $setup --repo $Repo --signer-workflow "$Repo/.github/workflows/build.yml"
        if ($LASTEXITCODE -ne 0) { throw 'The setup has no valid build attestation from this repository workflow: refusing to sign.' }
    }
    elseif ($SkipAttestation) {
        Write-Warning 'This gh has no "attestation" command and -SkipAttestation was given: the build provenance of the setup is NOT checked.'
    }
    else {
        throw 'This gh has no "attestation" command, so the build provenance of the setup cannot be checked: refusing to sign. Update gh (https://cli.github.com), or pass -SkipAttestation to sign without that check.'
    }

    & (Join-Path $PSScriptRoot 'sign-file.ps1') -File $file -Context release -KeyFile $KeyFile
    Invoke-Gh release upload $Tag "$file.sig" --repo $Repo --clobber | Out-Null
    Write-Host "Done: $sumName.sig is attached to $Tag. Installed CouchHUDs offer the update from now on."
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
