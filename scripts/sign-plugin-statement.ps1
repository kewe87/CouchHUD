#requires -Version 7
# Packs one of CouchHUD's own plugins, signs its package statement with CouchHUD's key (first-party) and writes the statement, its
# signature, the package hash and the version into the plugin's entry in catalog/index.json.
#
# Run by the release owner only, on the PC that holds the key. It never creates a key: without the key it stops.
# The private key stays OUTSIDE the repository (%USERPROFILE%\.couchhud\catalog-signing.key, DPAPI, see sign-file.ps1).
#
#   dotnet publish plugins/CouchHud.Plugin.Wand/CouchHud.Plugin.Wand.csproj -c Release -o publish-plugins/CouchHud.Plugin.Wand
#   .\scripts\sign-plugin-statement.ps1 -Statement .\catalog\statements\wand.json -Folder .\publish-plugins\CouchHud.Plugin.Wand -OutDir .\publish-plugins
#
# Afterwards: upload the printed .zip to the release named in the entry's download URL, then sign the index again
# (.\scripts\catalog-sign.ps1 -Index .\catalog\index.json), because the index changed.
#
# Domain separation: the statement is signed as "couchhud-plugin-v1`n" + statement bytes (CatalogSignature.PluginContext), so neither
# a catalog nor a release signature can pass for it, nor the other way round.
param(
    [Parameter(Mandatory)] [string] $Statement,
    [Parameter(Mandatory)] [string] $Folder,
    [string] $Index = (Join-Path $PSScriptRoot '..\catalog\index.json'),
    [string] $OutDir = '.',
    [string] $KeyFile = (Join-Path $env:USERPROFILE '.couchhud\catalog-signing.key')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue

if (-not (Test-Path $KeyFile)) { throw "No key at $KeyFile. This script never creates one." }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Folder = (Resolve-Path $Folder).Path.TrimEnd('\')
$statementPath = (Resolve-Path $Statement).Path
$indexPath = (Resolve-Path $Index).Path
$template = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($statementPath))
$id = [string]$template['id']
$version = [string]$template['version']
$folderName = [string]$template['folder']
if ((Split-Path $Folder -Leaf) -ne $folderName) { throw "The folder must be named $folderName (the top folder of the package)." }
$files = Get-ChildItem -Recurse -File $Folder
$refused = $files | Where-Object { $_.Extension -in '.exe', '.bat', '.cmd', '.ps1', '.vbs', '.js', '.msi', '.sys', '.lnk', '.scr', '.com' }
if ($refused) { throw "Programs or scripts in the package: $($refused.Name -join ', ')" }
# The SDK and CouchHUD's own assemblies come from CouchHUD; a copy in the package is refused when it loads.
$hostCopies = $files | Where-Object { $_.Name -match '^CouchHud\.(Sdk|Core|Shell|Service|Service\.Contracts|ServiceClient|Privileged)\.dll$' }
if ($hostCopies) { throw "CouchHUD's own assemblies in the package: $($hostCopies.Name -join ', ')" }

# 1. The package, exactly as it will be uploaded.
New-Item -ItemType Directory -Force $OutDir | Out-Null
$zip = Join-Path (Resolve-Path $OutDir) "$folderName-$version.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $Folder -DestinationPath $zip
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$size = (Get-Item $zip).Length

# 2. The key, and a check that it is the one compiled into CouchHUD (else the statement would not count as first-party).
$protectedBytes = [Convert]::FromBase64String([IO.File]::ReadAllText($KeyFile).Trim())
$plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect($protectedBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
try {
    $read = 0
    $ecdsa.ImportPkcs8PrivateKey($plainBytes, [ref]$read)
    [Array]::Clear($plainBytes, 0, $plainBytes.Length)
    $fingerprint = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($ecdsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()

    $catalogCs = [IO.File]::ReadAllText((Join-Path $repoRoot 'src\CouchHud.Core\Plugins\Catalog.cs'))
    $pem = [regex]::Match($catalogCs, '-----BEGIN PUBLIC KEY-----[\s\S]+?-----END PUBLIC KEY-----').Value
    if (-not $pem) { throw 'CouchHUD''s public key not found in Catalog.cs.' }
    $builtIn = [System.Security.Cryptography.ECDsa]::Create()
    $builtIn.ImportFromPem($pem)
    $builtInFingerprint = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($builtIn.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
    $builtIn.Dispose()
    if ($fingerprint -ne $builtInFingerprint) { throw "The key at $KeyFile is not the one compiled into CouchHUD ($builtInFingerprint)." }

    # 3. The statement: the template with the package's hash, size and the key's fingerprint, as compact UTF-8 JSON.
    $template['sha256'] = $sha
    $template['publisherKey'] = $fingerprint
    $template['size'] = $size
    $options = [System.Text.Json.JsonSerializerOptions]::new()
    $options.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
    $bytes = [Text.Encoding]::UTF8.GetBytes($template.ToJsonString($options))
    if ($bytes.Length -gt 16384) { throw 'The statement is too large.' }
    $prefix = [Text.Encoding]::ASCII.GetBytes("couchhud-plugin-v1`n")
    $signature = $ecdsa.SignData([byte[]]($prefix + $bytes), [System.Security.Cryptography.HashAlgorithmName]::SHA256)
}
finally {
    $ecdsa.Dispose()
}

# 4. Into the index entry (the version, hash and download name follow the package; the index must be signed again afterwards).
# ($Index is the [string] parameter: PowerShell variables ignore case, so the parsed index needs its own name.)
$indexNode = [System.Text.Json.Nodes.JsonNode]::Parse([IO.File]::ReadAllText($indexPath))
$entry = $indexNode['entries'].AsArray() | Where-Object { [string]$_['id'] -eq $id } | Select-Object -First 1
if (-not $entry) { throw "No entry '$id' in $indexPath." }
$entry['version'] = $version
$entry['sha256'] = $sha
$entry['download'] = "https://github.com/kewe87/CouchHUD-Catalog/releases/download/$id-$version/$folderName-$version.zip"
$entry['license'] = [string]$template['license']
$caps = [System.Text.Json.Nodes.JsonArray]::new()
foreach ($c in $template['capabilities'].AsArray()) { $caps.Add([string]$c) }
$entry['capabilities'] = $caps
$entry['statement'] = [Convert]::ToBase64String($bytes)
$entry['signature'] = [Convert]::ToBase64String($signature)
$written = [System.Text.Json.JsonSerializerOptions]::new()
$written.WriteIndented = $true
$written.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
[IO.File]::WriteAllText($indexPath, $indexNode.ToJsonString($written) + "`n", [Text.UTF8Encoding]::new($false))

Write-Host "Package:   $zip"
Write-Host "sha256:    $sha ($size bytes)"
Write-Host "Statement: signed first-party with $fingerprint, written into '$id' in $indexPath"
Write-Host 'Next: upload the package to the release, then .\scripts\catalog-sign.ps1 -Index .\catalog\index.json'
