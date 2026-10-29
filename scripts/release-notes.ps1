#requires -Version 7
# Writes the release page text for a tag: the section "## <version> (...)" of CHANGELOG.md without its heading.
# GitHub caps a release text at 125000 characters and appends its own "Full Changelog" link, so a longer section
# keeps its German part and links to the English part of CHANGELOG.md at the tag. Fails when the section is missing.
#
#   .\scripts\release-notes.ps1 -Tag v0.6.3 -Out release-notes.md
param(
    [Parameter(Mandatory)] [string] $Tag,
    [Parameter(Mandatory)] [string] $Out,
    [string] $Changelog = (Join-Path $PSScriptRoot '..' 'CHANGELOG.md'),
    [string] $Repo = 'kewe87/CouchHUD',
    [int] $MaxChars = 120000
)
$ErrorActionPreference = 'Stop'

if ($Tag -notmatch '^v(\d+\.\d+\.\d+)$') { throw "Tag '$Tag' is not vX.Y.Z" }
$version = $Matches[1]

$lines = Get-Content -LiteralPath $Changelog -Encoding utf8
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match "^## $([regex]::Escape($version)) \(") { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no section for $version" }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## ') { $end = $i; break }
}
$section = $lines[($start + 1)..($end - 1)]
$body = ($section -join "`n").Trim()

if ($body.Length -gt $MaxChars) {
    $english = [array]::IndexOf($section, '### English')
    if ($english -lt 0) { throw "Section $version is too long and has no '### English' part" }
    $german = ($section[0..($english - 1)] -join "`n").Trim()
    if ($german.Length -gt $MaxChars) { throw "Even the German part of $version is too long for a release page" }
    $body = "$german`n`n### English`n`nDie englischen Notizen sind für diese Seite zu lang. / The English notes for $version are too long for this page; they are in [CHANGELOG.md](https://github.com/$Repo/blob/$Tag/CHANGELOG.md#english)."
}

[IO.File]::WriteAllText($Out, "$body`n", [Text.UTF8Encoding]::new($false))
Write-Host "Release notes for $Tag`: $($body.Length) characters -> $Out"
