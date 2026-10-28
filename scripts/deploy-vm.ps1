<#
Publishes CouchHUD locally and copies it into the Hyper-V test VM over PowerShell Direct.
Run on the host as a member of "Hyper-V-Administratoren". The VM credential is stored once with:
    Get-Credential | Export-Clixml "$env:USERPROFILE\couchhud-vm.cred"

    scripts\deploy-vm.ps1                 # publish, copy, start in the signed-in desktop session
    scripts\deploy-vm.ps1 -NoBuild        # copy the last publish again
    scripts\deploy-vm.ps1 -Arguments --dev
    scripts\deploy-vm.ps1 -Service        # also update CouchHud.Service (stop, replace <InstallDir>\service\CouchHud.Service.exe, start)

-Service needs the service installed by the setup (it does not register one) and a VM credential that is an administrator of the VM.
#>
param(
    [string]$VMName = 'Win11-Test',
    [string]$CredentialPath = "$env:USERPROFILE\couchhud-vm.cred",
    [string]$InstallDir = 'C:\Program Files\CouchHud',
    [string[]]$Arguments = @(),
    [switch]$NoBuild,
    [switch]$NoStart,
    # Also replace the installed CouchHud.Service (published like the release build into publish\service).
    [switch]$Service
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'publish'
# The setup's layout (installer\CouchHud.iss): the service as one self-contained exe in {app}\service, its data below it.
$servicePublish = Join-Path $publish 'service'
$serviceName = 'CouchHud'
$serviceExe = 'CouchHud.Service.exe'

if (-not $NoBuild) {
    Write-Host '› dotnet publish'
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
    dotnet publish "$root\src\CouchHud.Shell\CouchHud.Shell.csproj" -c Release -r win-x64 --self-contained true -o $publish --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if ($Service) {
        Write-Host '› dotnet publish (service)'
        # As in .github/workflows/build.yml: single file, symbols embedded, the folder holds exactly the one exe.
        dotnet publish "$root\src\CouchHud.Service\CouchHud.Service.csproj" -c Release -r win-x64 --self-contained true -p:DebugType=embedded -o $servicePublish --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
if ($Service -and -not (Test-Path (Join-Path $servicePublish $serviceExe))) {
    throw "No $serviceExe in $servicePublish. Run without -NoBuild once."
}

$session = New-PSSession -VMName $VMName -Credential (Import-Clixml $CredentialPath)
try {
    Write-Host "› stop CouchHUD in $VMName"
    # As the shell, explorer.exe is not there to take over; the copy only replaces files, it never touches the shell entry.
    Invoke-Command -Session $session -ScriptBlock {
        Get-Process CouchHud -ErrorAction SilentlyContinue | Stop-Process -Force
        Start-Sleep -Milliseconds 500
    }

    Write-Host "› copy to $InstallDir"
    Invoke-Command -Session $session -ScriptBlock { New-Item -ItemType Directory -Force $using:InstallDir | Out-Null }
    # The service folder only with -Service and only after the service has stopped (its exe is in use while it runs).
    Get-ChildItem $publish | Where-Object Name -ne 'service' |
        Copy-Item -ToSession $session -Destination $InstallDir -Recurse -Force

    if ($Service) {
        $serviceDir = Join-Path $InstallDir 'service'
        Write-Host "› stop $serviceName in $VMName"
        Invoke-Command -Session $session -ScriptBlock {
            $svc = Get-Service -Name $using:serviceName -ErrorAction SilentlyContinue
            if (-not $svc) { throw "The service $($using:serviceName) is not installed in the VM; install CouchHUD with the setup first." }
            if (-not (Test-Path (Join-Path $using:serviceDir $using:serviceExe))) { throw "$($using:serviceExe) not found in $($using:serviceDir); the service is installed elsewhere." }
            if ($svc.Status -ne 'Stopped') { Stop-Service -Name $using:serviceName -Force }
            try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
            catch {
                # Did not stop in time: leave the old service running rather than stopped.
                Start-Service -Name $using:serviceName -ErrorAction SilentlyContinue
                throw "The service $($using:serviceName) did not stop within 30 s; nothing was replaced."
            }
            # The exe stays locked until the process is really gone.
            $proc = [IO.Path]::GetFileNameWithoutExtension($using:serviceExe)
            for ($i = 0; $i -lt 20 -and (Get-Process $proc -ErrorAction SilentlyContinue); $i++) { Start-Sleep -Milliseconds 500 }
        }

        try {
            Write-Host "› copy the service to $serviceDir"
            # Only the exe: the data folder (state, codes, logs) and the folder's access list stay as the setup left them.
            Copy-Item -ToSession $session -Path (Join-Path $servicePublish $serviceExe) -Destination $serviceDir -Force
        }
        finally {
            # Started again also when the copy failed (then the old exe runs on).
            Write-Host "› start $serviceName"
            Invoke-Command -Session $session -ScriptBlock {
                # Owner Administrators, as the setup sets it for everything in the service folder (not the account that copied).
                & "$env:SystemRoot\System32\icacls.exe" (Join-Path $using:serviceDir $using:serviceExe) /setowner '*S-1-5-32-544' /C /Q | Out-Null
                if ($LASTEXITCODE -ne 0) { Write-Warning "icacls /setowner failed ($LASTEXITCODE)" }
                Start-Service -Name $using:serviceName
                $svc = Get-Service -Name $using:serviceName
                $svc.WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
                $svc | Select-Object Name, Status
            }
        }
    }

    if (-not $NoStart) {
        Write-Host '› start in the desktop session'
        # PowerShell Direct is a non-interactive logon; a one-shot task with an interactive principal
        # puts the window on the signed-in user's desktop.
        Invoke-Command -Session $session -ScriptBlock {
            $user = (Get-CimInstance Win32_ComputerSystem).UserName
            if (-not $user) { throw 'Nobody is signed in to the VM desktop.' }
            $exe = Join-Path $using:InstallDir 'CouchHud.exe'
            $action = if ($using:Arguments) {
                New-ScheduledTaskAction -Execute $exe -Argument ($using:Arguments -join ' ') -WorkingDirectory $using:InstallDir
            } else {
                New-ScheduledTaskAction -Execute $exe -WorkingDirectory $using:InstallDir
            }
            $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
            Register-ScheduledTask -TaskName 'CouchHudDeploy' -Action $action -Principal $principal -Settings $settings -Force | Out-Null
            Start-ScheduledTask -TaskName 'CouchHudDeploy'
            Start-Sleep -Seconds 2
            Unregister-ScheduledTask -TaskName 'CouchHudDeploy' -Confirm:$false
            Get-Process CouchHud -ErrorAction SilentlyContinue | Select-Object Id, SessionId, StartTime
        }
    }
}
finally {
    Remove-PSSession $session
}
Write-Host '✓ deployed'
