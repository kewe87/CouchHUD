#requires -Version 7
# Signs a catalog index.json (writes index.json.sig). Thin wrapper around sign-file.ps1, which holds the
# key handling (private key outside the repository, DPAPI-protected) and documents it. Signing never creates a key
# and refuses a key that is not the one compiled into CouchHUD. Each run raises "serial" in the index by 1 before signing
# (replay protection, see SECURITY.md); signing the same file twice raises it twice, which does no harm.
#
#   .\scripts\catalog-sign.ps1 -GenerateKey          # creates the key pair if missing, prints the public key
#   .\scripts\catalog-sign.ps1 -Index .\catalog\index.json
param(
    [string] $Index,
    [switch] $GenerateKey,
    [string] $KeyFile = (Join-Path $env:USERPROFILE '.couchhud\catalog-signing.key')
)

$ErrorActionPreference = 'Stop'
$arguments = @{ KeyFile = $KeyFile }
if ($Index) { $arguments.File = $Index; $arguments.BumpSerial = $true }
if ($GenerateKey) { $arguments.GenerateKey = $true }
& (Join-Path $PSScriptRoot 'sign-file.ps1') @arguments
