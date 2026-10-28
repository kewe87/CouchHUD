<#
Talks to the CouchHUD UI in the Hyper-V test VM over the Chrome DevTools Protocol.

Remote debugging is an administrator switch since 0.4.2: HKLM\SOFTWARE\CouchHud DeveloperMode=1 and RemoteDebuggingPort=<port>
(DWORDs, 64-bit view). CouchHUD ignores WEBVIEW2_* environment variables (it removes them at start), so the old
WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS route no longer works. The port binds to 127.0.0.1 only; the couch browser gets port + 1.
The VM credential must be an administrator of the VM for -Enable/-Disable. Restart CouchHUD after switching.

    scripts\vm-cdp.ps1 -Enable             # set the HKLM values in the VM (then restart CouchHUD, e.g. deploy-vm.ps1 -NoBuild)
    scripts\vm-cdp.ps1 -Disable            # remove them again after the tests (and the old WEBVIEW2_* user variable, if any)
    scripts\vm-cdp.ps1 -Eval 'document.title'
    scripts\vm-cdp.ps1 -Screenshot shot.png
    scripts\vm-cdp.ps1 -Keys ArrowRight,ArrowRight,Enter
    scripts\vm-cdp.ps1 -Console            # errors and warnings since the last call
    scripts\vm-cdp.ps1 -Reload -Console    # reload the page and capture errors from the very start
#>
param(
    [string]$VMName = 'Win11-Test',
    [string]$CredentialPath = "$env:USERPROFILE\couchhud-vm.cred",
    [string]$Eval,
    [string]$Screenshot,
    [string[]]$Keys,
    [int]$KeyDelayMs = 250,
    [switch]$Console,
    [switch]$Reload,
    # The couch browser takes $Port + 1, so 65534 at most.
    [ValidateRange(1024, 65534)]
    [int]$Port = 9222,
    [switch]$Enable,
    [switch]$Disable,
    # Accounts in the VM whose user environment -Disable cleans of WEBVIEW2_* (the couch test accounts).
    [string[]]$TestAccounts = @('couchtest', 'couchkid')
)
$ErrorActionPreference = 'Stop'

if ($Enable -or $Disable) {
    $cred = Import-Clixml $CredentialPath
    Invoke-Command -VMName $VMName -Credential $cred -ArgumentList @([bool]$Enable, $Port, $TestAccounts) -ScriptBlock {
        param($On, $Port, $TestAccounts)
        $key = 'HKLM:\SOFTWARE\CouchHud'
        if ($On) {
            if (-not (Test-Path $key)) { New-Item -Path $key -Force | Out-Null }
            New-ItemProperty -Path $key -Name DeveloperMode -PropertyType DWord -Value 1 -Force | Out-Null
            New-ItemProperty -Path $key -Name RemoteDebuggingPort -PropertyType DWord -Value $Port -Force | Out-Null
            "remote debugging on: 127.0.0.1:$Port (HUD), $($Port + 1) (couch browser); restart CouchHUD"
        }
        else {
            if (Test-Path $key) {
                Remove-ItemProperty -Path $key -Name DeveloperMode, RemoteDebuggingPort -ErrorAction SilentlyContinue
                # -Enable creates the key if it is missing: an empty one (no values, no subkeys) goes again. Anything else in it
                # (the auto sign-in marker of the privileged helper) keeps it.
                $item = Get-Item $key
                if ($item.ValueCount -eq 0 -and $item.SubKeyCount -eq 0) { Remove-Item -Path $key }
                $item.Close()
            }
            # The route before 0.4.2; CouchHUD ignores it now, but it should not linger in the test accounts. Each account's own
            # HKCU\Environment: the loaded hive of a signed-in account, otherwise its NTUSER.DAT loaded for a moment. Names only, no values.
            $profiles = Get-CimInstance Win32_UserProfile | Where-Object { -not $_.Special }
            foreach ($account in $TestAccounts) {
                $sid = try { ([Security.Principal.NTAccount]$account).Translate([Security.Principal.SecurityIdentifier]).Value } catch { $null }
                $prof = if ($sid) { $profiles | Where-Object SID -eq $sid } else { $null }
                if (-not $prof) { "${account}: no such account or profile in the VM, nothing to clean"; continue }
                $mounted = $null
                $root = "Registry::HKEY_USERS\$sid"
                if (-not (Test-Path $root)) {
                    $hive = Join-Path $prof.LocalPath 'NTUSER.DAT'
                    & reg.exe load 'HKU\CouchHudCdpCleanup' $hive 2>$null | Out-Null
                    if ($LASTEXITCODE -ne 0) { "${account}: hive could not be loaded, WEBVIEW2_* not checked (sign the account in and run -Disable again)"; continue }
                    $mounted = 'HKU\CouchHudCdpCleanup'
                    $root = 'Registry::HKEY_USERS\CouchHudCdpCleanup'
                }
                try {
                    $envKey = "$root\Environment"
                    $names = if (Test-Path $envKey) { (Get-Item $envKey).GetValueNames() | Where-Object { $_ -like 'WEBVIEW2_*' } } else { @() }
                    foreach ($name in $names) { Remove-ItemProperty -Path $envKey -Name $name }
                    if ($names) { "${account}: removed $($names -join ', ')" } else { "${account}: no WEBVIEW2_* variables" }
                }
                finally {
                    if ($mounted) {
                        [GC]::Collect(); [GC]::WaitForPendingFinalizers()
                        & reg.exe unload $mounted 2>$null | Out-Null
                        if ($LASTEXITCODE -ne 0) { "${account}: hive could not be unloaded again, restart the VM before signing the account in" }
                    }
                }
            }
            'remote debugging off; restart CouchHUD (a signed-in account sees changed variables only after signing in again)'
        }
    }
    return
}

$remote = {
    param($Eval, $WantShot, $Keys, $KeyDelayMs, $Console, $Reload, $Port)
    # Right after a start the debug port needs a few seconds to come up.
    for ($try = 0; ; $try++) {
        try { $page = (Invoke-RestMethod "http://127.0.0.1:$Port/json" -TimeoutSec 5) | Where-Object type -eq 'page' | Select-Object -First 1; if ($page) { break } }
        catch { if ($try -ge 15) { throw } }
        Start-Sleep -Seconds 2
    }
    if (-not $page) { throw "No CouchHUD page on port $Port. Is CouchHUD running, and remote debugging on (scripts\vm-cdp.ps1 -Enable)?" }
    $ws = [System.Net.WebSockets.ClientWebSocket]::new()
    $ws.Options.KeepAliveInterval = [TimeSpan]::Zero
    $ws.ConnectAsync([Uri]$page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).Wait()
    $script:id = 0
    function Send-Cdp($method, $params = @{}) {
        $script:id++
        $msg = @{ id = $script:id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
        $bytes = [Text.Encoding]::UTF8.GetBytes($msg)
        $ws.SendAsync([ArraySegment[byte]]$bytes, 'Text', $true, [Threading.CancellationToken]::None).Wait()
        $buffer = [byte[]]::new(1MB)
        while ($true) {
            $sb = [Text.StringBuilder]::new()
            do {
                $r = $ws.ReceiveAsync([ArraySegment[byte]]$buffer, [Threading.CancellationToken]::None).Result
                [void]$sb.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
            } until ($r.EndOfMessage)
            $reply = $sb.ToString() | ConvertFrom-Json
            if ($reply.id -eq $script:id) {
                if ($reply.error) { throw "$method failed: $($reply.error.message)" }
                return $reply.result
            }
        }
    }
    function Invoke-Js($expr) {
        $r = Send-Cdp 'Runtime.evaluate' @{ expression = $expr; returnByValue = $true; awaitPromise = $true }
        if ($r.exceptionDetails) { throw "JS: $($r.exceptionDetails.exception.description) $($r.exceptionDetails.text)" }
        $r.result.value
    }
    try {
        $out = [ordered]@{}
        # Runs in Windows PowerShell 5.1 inside the VM. Console capture lives in the page, so it survives between calls until the page reloads.
        $capture = @'
if (!window.__cdpLog) {
  window.__cdpLog = [];
  for (const level of ['error', 'warn']) {
    const orig = console[level];
    console[level] = (...a) => { window.__cdpLog.push(level + ': ' + a.map(String).join(' ')); orig.apply(console, a); };
  }
  addEventListener('error', e => window.__cdpLog.push('uncaught: ' + e.message + ' @ ' + e.filename + ':' + e.lineno));
  addEventListener('unhandledrejection', e => window.__cdpLog.push('rejection: ' + (e.reason && e.reason.stack || e.reason)));
}
'@
        if ($Reload) {
            # Installed before any page script runs, so errors during start-up are caught too.
            [void](Send-Cdp 'Page.addScriptToEvaluateOnNewDocument' @{ source = $capture })
            [void](Send-Cdp 'Page.reload' @{ ignoreCache = $true })
            Start-Sleep -Seconds 8
        }
        [void](Invoke-Js $capture)
        foreach ($key in $Keys) {
            $codes = @{ ArrowUp = 38; ArrowDown = 40; ArrowLeft = 37; ArrowRight = 39; Enter = 13; Escape = 27; Backspace = 8; Tab = 9; Home = 36; PageUp = 33; PageDown = 34; ' ' = 32 }
            $vk = if ($codes.ContainsKey($key)) { $codes[$key] } else { [int][char]$key.ToUpper()[0] }
            $code = if ($key.Length -eq 1) { 'Key' + $key.ToUpper() } else { $key }
            $text = if ($key.Length -eq 1) { $key } else { $null }
            $down = @{ type = 'rawKeyDown'; key = $key; code = $code; windowsVirtualKeyCode = $vk }
            [void](Send-Cdp 'Input.dispatchKeyEvent' $down)
            if ($text) { [void](Send-Cdp 'Input.dispatchKeyEvent' @{ type = 'char'; text = $text; key = $key }) }
            [void](Send-Cdp 'Input.dispatchKeyEvent' @{ type = 'keyUp'; key = $key; code = $code; windowsVirtualKeyCode = $vk })
            Start-Sleep -Milliseconds $KeyDelayMs
        }
        if ($Eval) { $out.eval = Invoke-Js $Eval }
        if ($WantShot) { $out.png = (Send-Cdp 'Page.captureScreenshot' @{ format = 'png' }).data }
        if ($Console) { $out.console = Invoke-Js 'JSON.stringify(window.__cdpLog.splice(0))' | ConvertFrom-Json }
        [pscustomobject]$out
    }
    finally { $ws.Dispose() }
}

$cred = Import-Clixml $CredentialPath
$result = Invoke-Command -VMName $VMName -Credential $cred -ScriptBlock $remote -ArgumentList @($Eval, ([bool]$Screenshot), $Keys, $KeyDelayMs, ([bool]$Console), ([bool]$Reload), $Port)
if ($Screenshot) {
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Screenshot), [Convert]::FromBase64String($result.png))
    "screenshot: $Screenshot"
}
if ($Eval) { $result.eval }
if ($Console) { if ($result.console) { $result.console } else { 'console: clean' } }
