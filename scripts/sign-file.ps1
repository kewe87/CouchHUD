#requires -Version 7
# Signs any file with ECDSA P-256 (SHA-256) and writes <file>.sig (Base64 over a context line plus the exact file bytes).
# Used for the catalog index (catalog-sign.ps1) and the checksum file of a release (sign-release.ps1).
#
# The private key stays OUTSIDE the repository: %USERPROFILE%\.couchhud\catalog-signing.key, protected with
# DPAPI (only this Windows user on this PC can read it). Never commit it, never copy it into the repo.
# The public key is compiled into CouchHUD (CatalogSignature.PublicKeyPem).
#
#   .\scripts\sign-file.ps1 -GenerateKey          # creates the key pair if missing (only here, never while signing), prints the public key
#   .\scripts\sign-file.ps1 -File .\catalog\index.json
#
# Signing never creates a key: with -File and no key file it stops. It also refuses a key whose public half is not the one
# compiled into CouchHUD (src/CouchHud.Core/Plugins/Catalog.cs), because every client would reject such a signature.
#
# -BumpSerial (catalog index only, used by catalog-sign.ps1): after the key checks and right before signing, "serial" in the
# index is raised by 1 (inserted as 1 when missing; only the root property counts, a nested "serial" is left alone). The bumped bytes
# are built in memory and signed first; the index and the .sig are replaced only afterwards (temp file + move), so a failure keeps the old pair. Clients refuse an index with a lower serial than the one they saved
# (replay protection, see SECURITY.md), so every changed index has to carry a higher one.
#
# Sign the file exactly as it will be published (no later line-ending or whitespace changes).
#
# Domain separation: the signed bytes are a context line followed by the file, so a signature made for one purpose
# is worthless for the other (CatalogSignature.CatalogContext / ReleaseContext):
#   -Context catalog   "couchhud-catalog-v1`n" + file   (default; catalog index.json)
#   -Context release   "couchhud-release-v1`n" + file   (CouchHud-Setup-<v>.exe.sha256)
param(
    [string] $File,
    [ValidateSet('catalog', 'release')] [string] $Context = 'catalog',
    [switch] $GenerateKey,
    [switch] $BumpSerial,
    [string] $KeyFile = (Join-Path $env:USERPROFILE '.couchhud\catalog-signing.key')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security.Cryptography.ProtectedData -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.Security -ErrorAction SilentlyContinue

function Get-Fingerprint([System.Security.Cryptography.ECDsa] $key) {
    [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($key.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
}

function Write-PublicKey([System.Security.Cryptography.ECDsa] $key) {
    Write-Host ''
    Write-Host 'Public key (CatalogSignature.PublicKeyPem):'
    Write-Host $key.ExportSubjectPublicKeyInfoPem()
    Write-Host "Fingerprint (SHA-256 of the key): $(Get-Fingerprint $key)"
}

# Compared as text (full paths, no existence needed), so a key path in a folder that does not exist yet is judged too.
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
function Test-InsideRepo([string] $path) {
    $full = [IO.Path]::GetFullPath($path)
    $full.Equals($repoRoot, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
}
if (Test-InsideRepo $KeyFile) { throw 'The private key must not live inside the repository.' }

if (-not (Test-Path $KeyFile)) {
    # A key is created only on request. A fresh key would silently sign something every installed CouchHUD rejects.
    if (-not $GenerateKey) { throw "No key at $KeyFile. Signing never creates a key (only -GenerateKey does, and that key is not the one compiled into CouchHUD); use the release owner's key." }
    New-Item -ItemType Directory -Force (Split-Path $KeyFile -Parent) | Out-Null
    $new = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
    $plain = $new.ExportPkcs8PrivateKey()
    $protected = [System.Security.Cryptography.ProtectedData]::Protect($plain, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllText($KeyFile, [Convert]::ToBase64String($protected))
    [Array]::Clear($plain, 0, $plain.Length)
    Write-Host "Created key pair. Private key (DPAPI, this user only): $KeyFile"
    Write-PublicKey $new
    $new.Dispose()
}

$protectedBytes = [Convert]::FromBase64String([IO.File]::ReadAllText($KeyFile).Trim())
$plainBytes = [System.Security.Cryptography.ProtectedData]::Unprotect($protectedBytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$read = 0
$ecdsa.ImportPkcs8PrivateKey($plainBytes, [ref]$read)
[Array]::Clear($plainBytes, 0, $plainBytes.Length)

if ($GenerateKey -and -not $File) { Write-PublicKey $ecdsa; $ecdsa.Dispose(); return }
if (-not $File) { $ecdsa.Dispose(); throw 'Pass -File <path of the file to sign>.' }

# The key must be the one compiled into CouchHUD, exactly as sign-plugin-statement.ps1 checks it.
$catalogCs = [IO.File]::ReadAllText((Join-Path $repoRoot 'src\CouchHud.Core\Plugins\Catalog.cs'))
$pem = [regex]::Match($catalogCs, '-----BEGIN PUBLIC KEY-----[\s\S]+?-----END PUBLIC KEY-----').Value
if (-not $pem) { $ecdsa.Dispose(); throw 'CouchHUD''s public key not found in Catalog.cs.' }
$builtIn = [System.Security.Cryptography.ECDsa]::Create()
$builtIn.ImportFromPem($pem)
$builtInFingerprint = Get-Fingerprint $builtIn
$builtIn.Dispose()
if ((Get-Fingerprint $ecdsa) -ne $builtInFingerprint) { $ecdsa.Dispose(); throw "The key at $KeyFile is not the one compiled into CouchHUD ($builtInFingerprint): nothing signed." }

$path = (Resolve-Path $File).Path
$bytes = [IO.File]::ReadAllBytes($path)
$newBytes = $null

if ($BumpSerial) {
    if ($Context -ne 'catalog') { $ecdsa.Dispose(); throw '-BumpSerial is for the catalog index only.' }
    # Only the ROOT property "serial" counts (a "serial" nested inside an entry is data of that entry). It is found with a JSON reader, then
    # edited in the bytes, so the rest of the file stays byte for byte as it was. Everything happens in memory: the file is replaced only
    # after the signature over the new bytes exists.
    if (-not ('CouchHudIndexSerial' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Text.Json;
public static class CouchHudIndexSerial
{
    // { found (0/1), offset of the number, length of the number, value, offset of the root "{" }
    public static long[] Find(byte[] b)
    {
        int bom = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
        var r = new Utf8JsonReader(new ReadOnlySpan<byte>(b, bom, b.Length - bom), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 16 });
        long brace = -1, at = 0, len = 0, value = 0;
        bool found = false, first = true;
        while (r.Read())
        {
            if (first)
            {
                if (r.TokenType != JsonTokenType.StartObject) throw new InvalidOperationException("The index is not a JSON object.");
                brace = bom + r.TokenStartIndex;
                first = false;
            }
            else if (r.TokenType == JsonTokenType.PropertyName && r.CurrentDepth == 1 && r.ValueTextEquals("serial"))
            {
                if (found) throw new InvalidOperationException("The index has two root \"serial\" properties.");
                r.Read();
                long v;
                if (r.TokenType != JsonTokenType.Number || !r.TryGetInt64(out v) || v < 0 || v == long.MaxValue) throw new InvalidOperationException("The root \"serial\" is not a whole number of 0 or more.");
                found = true;
                at = bom + r.TokenStartIndex;
                len = r.HasValueSequence ? r.ValueSequence.Length : r.ValueSpan.Length;
                value = v;
            }
        }
        if (brace < 0) throw new InvalidOperationException("The index is empty.");
        return new long[] { found ? 1 : 0, at, len, value, brace };
    }
}
'@
    }
    try { $f = [CouchHudIndexSerial]::Find($bytes) } catch { $ecdsa.Dispose(); throw }
    if ($f[0] -eq 1) {
        $next = $f[3] + 1
        $insert = [Text.Encoding]::ASCII.GetBytes([string]$next)
        $newBytes = [byte[]]($bytes[0..($f[1] - 1)] + $insert + $bytes[($f[1] + $f[2])..($bytes.Length - 1)])
    }
    else {
        # No root serial: insert it as the first property, after the opening brace and the whitespace that follows it (same whitespace after the comma).
        $next = 1
        $after = [int]$f[4] + 1
        $end = $after
        while ($end -lt $bytes.Length -and $bytes[$end] -in 0x20, 0x09, 0x0A, 0x0D) { $end++ }
        $white = if ($end -gt $after) { $bytes[$after..($end - 1)] } else { [byte[]]@() }
        $insert = $white + [Text.Encoding]::ASCII.GetBytes("`"serial`": $next$(if ($end -lt $bytes.Length -and $bytes[$end] -eq 0x7D) { '' } else { ',' })") + $white
        $newBytes = [byte[]]($bytes[0..($f[4])] + $insert + $bytes[$end..($bytes.Length - 1)])
    }
    Write-Host "Serial of the index will be raised to $next"
}

$signedBytes = if ($newBytes) { $newBytes } else { $bytes }
$prefix = [Text.Encoding]::ASCII.GetBytes("couchhud-$Context-v1`n")
try {
    $signature = $ecdsa.SignData([byte[]]($prefix + $signedBytes), [System.Security.Cryptography.HashAlgorithmName]::SHA256)

    # Write both new files next to the old ones first (a failure here leaves the old pair as it was), then move them into place.
    $tmpFile = "$path.new"
    $tmpSig = "$path.sig.new"
    [IO.File]::WriteAllBytes($tmpFile, $signedBytes)
    [IO.File]::WriteAllText($tmpSig, [Convert]::ToBase64String($signature))
    $oldSig = if (Test-Path "$path.sig") { [IO.File]::ReadAllBytes("$path.sig") } else { $null }
    try {
        Move-Item -Force $tmpFile $path
        Move-Item -Force $tmpSig "$path.sig"
    }
    catch {
        # Put the old pair back as well as possible.
        [IO.File]::WriteAllBytes($path, $bytes)
        if ($null -ne $oldSig) { [IO.File]::WriteAllBytes("$path.sig", $oldSig) }
        throw
    }
}
finally {
    Remove-Item -Force -ErrorAction SilentlyContinue "$path.new", "$path.sig.new"
}
Write-Host "Signed ($Context) $path -> $path.sig ($($signedBytes.Length) bytes)"
Write-Host "Key fingerprint: $(Get-Fingerprint $ecdsa)"
$ecdsa.Dispose()
