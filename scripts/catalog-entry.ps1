# Packs a plugin or skin folder into <Name>-<Version>.zip (the folder name is the top folder of the archive)
# and prints the SHA-256 for the catalog entry in index.json.
#
#   .\scripts\catalog-entry.ps1 -Folder .\publish\CouchHud.Plugin.Wand -Version 1.0.0
param(
    [Parameter(Mandatory)] [string] $Folder,
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutDir = "."
)

$ErrorActionPreference = 'Stop'
$Folder = (Resolve-Path $Folder).Path.TrimEnd('\')
$name = Split-Path $Folder -Leaf
$zip = Join-Path (Resolve-Path $OutDir) "$name-$Version.zip"
if (Test-Path $zip) { Remove-Item $zip }

# A skin folder: the lint for the catalog review (a skin must not hide tabs) runs before anything is packed.
if (Test-Path (Join-Path $Folder 'skin.css')) {
    node (Join-Path $PSScriptRoot 'skin-check.mjs') $Folder
    if ($LASTEXITCODE -ne 0) { throw "skin-check.mjs: the skin hides tabs, not packed." }
}

# Zip the folder itself, so the archive has one top folder named like the plugin.
Compress-Archive -Path $Folder -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()

Write-Host "Archiv: $zip"
Write-Host "sha256: $hash"
Write-Host ""
Write-Host "In index.json eintragen:"
Write-Host "  `"version`": `"$Version`","
Write-Host "  `"sha256`": `"$hash`""
