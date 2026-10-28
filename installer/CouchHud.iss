; CouchHUD installer (Inno Setup 6)
; Build: iscc /DAppVersion=0.5.0 /DPublishDir=..\publish /DWebView2Setup=MicrosoftEdgeWebview2Setup.exe CouchHud.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
; A test or development build carries a suffix (0.5.0-test, 0.5.0-dev+abc): AppVersion and the file name keep it, the file's
; version resource (VersionInfoVersion) takes only numbers, so it gets the part before the first "-" or "+".
#define VersionCut Pos("+", AppVersion)
#if VersionCut > 0
  #define VersionCore Copy(AppVersion, 1, VersionCut - 1)
#else
  #define VersionCore AppVersion
#endif
#define VersionCut Pos("-", VersionCore)
#if VersionCut > 0
  #define NumericVersion Copy(VersionCore, 1, VersionCut - 1)
#else
  #define NumericVersion VersionCore
#endif
#ifndef PublishDir
  #define PublishDir "..\publish"
#endif

#define AppName "CouchHUD"
; Folder and file names stay "CouchHud": settings and the shell entry of existing installs depend on them.
#define AppDir "CouchHud"
#define AppExe "CouchHud.exe"

; CouchHud.Service (LocalSystem): published self-contained into {#PublishDir}\service, installed into {app}\service.
; The values below are the service's hardening (src/CouchHud.Service/ServiceHardening.cs, verified in the P0 spike) and its
; names (src/CouchHud.Privileged/ServiceRemoval.cs, ServiceInstall). InstallerTests compares them with the C# constants: change
; them there and here together.
#define ServiceName "CouchHud"
#define ServiceDisplayName "CouchHUD Service"
#define ServiceDescription "Carries out the administrator tasks of CouchHUD (lock policies, updates, automatic sign-in) for signed-in users. Without it these functions are unavailable."
#define ServiceFolder "service"
#define ServiceExe "CouchHud.Service.exe"
; {app}\service\data: the service's state, staging, jobs and logs (ServiceInstall.DataFolder). Created by the setup after the folder's
; access list is set, kept by updates (never in [InstallDelete]), removed by the uninstall.
#define ServiceDataFolder "data"
#define ServiceSidType "unrestricted"
#define ServicePrivileges "SeChangeNotifyPrivilege/SeImpersonatePrivilege/SeBackupPrivilege/SeRestorePrivilege/SeShutdownPrivilege"
#define ServiceSddl "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRPRC;;;IU)"
; During an update: the same, but interactive users may not start it (no RP). The start type stays automatic, so an update that
; is cut off (power loss, killed setup) still leaves a service that starts with Windows; the next setup or the repair restores all.
#define ServiceSddlUpdating "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCRC;;;IU)"
#define ServiceFailureReset "86400"
#define ServiceFailureActions "restart/5000/restart/30000/restart/60000"
; The service folder: no inheritance from {app}, SYSTEM and Administrators full, Users read and execute, owner Administrators.
#define ServiceFolderGrants "*S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX"
; The data folder below it (state, codes, staged setups, logs): inheritance broken there, SYSTEM and Administrators only. Users may not
; even read it: no user can open (and hold) a staged setup, read the service's state or copy the code hashes to guess them offline.
#define ServiceDataGrants "*S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F"
#define EventSource "CouchHud.Service"
; The message file System.Diagnostics.EventLog itself registers (EventLog.CreateEventSource): part of every Windows 10/11.
#define EventMessageFile "%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll"
#define RepairScript "repair-service.cmd"
; Exit code of the setup when the files are installed but the service could not be set up or does not run (GetCustomSetupExitCode).
#define ExitServiceFailed 20
; The service's own exit code when its pipe name was taken before it started (ServiceHardening.ExitPipeSquatted, 0xC0D1): the setup
; names that case to the administrator. Exit code, installed files and the restricted tasks stay as for every other failure (fail-closed).
#define ServiceExitPipeSquatted 49361

[Setup]
; Never change the AppId: updates and uninstall find the installation through it.
AppId={{6E3C2B8A-4F1D-4C7E-9A52-1B7D0C9E3F21}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=kewe87
AppPublisherURL=https://github.com/kewe87/CouchHUD
AppSupportURL=https://github.com/kewe87/CouchHUD/issues
AppUpdatesURL=https://github.com/kewe87/CouchHUD/releases
AppCopyright=Copyright © 2026 kewe87
VersionInfoVersion={#NumericVersion}
; Program Files, always: the service runs as SYSTEM from {app}\service, so {app} must be a folder only administrators can
; change. No folder page; /DIR= or an earlier install elsewhere is refused in PrepareToInstall (InProgramFiles).
DefaultDirName={commonpf}\{#AppDir}
DisableDirPage=yes
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE
OutputDir=output
OutputBaseFilename=CouchHud-Setup-{#AppVersion}
SetupIconFile=..\src\CouchHud.Shell\couchhud.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
WizardImageFile=wizard-1.bmp,wizard-2.bmp
WizardSmallImageFile=wizard-small-1.bmp,wizard-small-2.bmp
Compression=lzma2/ultra64
SolidCompression=yes
; A running CouchHUD (also as the shell) is closed for an update. No AppMutex on purpose: a silent setup
; (the self-update: /VERYSILENT /FORCECLOSEAPPLICATIONS) would stop at it instead of closing CouchHUD.
; The self-update starts a helper that restarts CouchHUD after the setup (SelfUpdateManager.StartRestarter).
CloseApplications=yes
RestartApplications=no
; The setup language follows the Windows display language: German for German, English for anything else
; (the first language below is the fallback). No language dialog, no "same language as last time".
ShowLanguageDialog=no
UsePreviousLanguage=no
LanguageDetectionMethod=uilanguage

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "info-en.txt"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"; InfoBeforeFile: "info-de.txt"

[CustomMessages]
de.DesktopIcon=Desktop-Symbol anlegen
en.DesktopIcon=Create a desktop icon
de.Autostart=CouchHUD bei der Windows-Anmeldung starten (App-Modus, ändert nicht die Shell)
en.Autostart=Start CouchHUD when signing in to Windows (app mode, doesn't replace the shell)
de.WebView2=Microsoft WebView2 Runtime wird installiert …
en.WebView2=Installing the Microsoft WebView2 Runtime …
de.Launch=CouchHUD jetzt starten
en.Launch=Start CouchHUD now
de.Cleanup=Windows-Einstellungen von CouchHUD werden zurückgesetzt …
en.Cleanup=Restoring Windows settings changed by CouchHUD …
de.DeleteData=Sollen auch deine CouchHUD-Einstellungen, Spielzeiten, Lesezeichen und Caches gelöscht werden?%n%nNein behält sie für eine spätere Neuinstallation.
en.DeleteData=Also delete your CouchHUD settings, playtime, bookmarks and caches?%n%nNo keeps them for a later reinstall.
de.StillRunning=CouchHUD läuft noch. Bitte erst beenden (Menü Beenden oder PC-Modus), dann erneut starten.
en.StillRunning=CouchHUD is still running. Please quit it first (Quit or PC mode), then run this again.
de.AutoLogonNote=Hinweis: Falls du die automatische Windows-Anmeldung selbst (z.B. mit netplwiz) eingeschaltet hast, bleibt sie an. Von CouchHUD eingerichtete wurde entfernt. Ausschalten: Windows-Taste + R, „netplwiz“, Haken bei „Benutzer müssen Benutzernamen und Kennwort eingeben“ setzen.
en.AutoLogonNote=Note: automatic Windows sign-in that you turned on yourself (e.g. with netplwiz) stays on; the one CouchHUD set up was removed. To turn it off: Windows key + R, "netplwiz", tick "Users must enter a user name and password".
de.SignOutNote=CouchHUD war deine Windows-Oberfläche. Bitte abmelden und neu anmelden, dann startet wieder der normale Windows-Desktop. Fehlt die Taskleiste: Strg+Alt+Entf, dann „Abmelden“.
en.SignOutNote=CouchHUD was your Windows shell. Please sign out and sign in again to get the normal Windows desktop back. If the taskbar is missing: Ctrl+Alt+Del, then "Sign out".
de.NotProgramFiles=CouchHUD muss unter „%1“ installiert sein: Der CouchHUD-Dienst läuft mit Systemrechten und darf nur in einem Ordner liegen, den allein Administratoren ändern können. Diese Installation liegt in „%2“. Bitte CouchHUD deinstallieren und neu installieren.
en.NotProgramFiles=CouchHUD must be installed under "%1": the CouchHUD service runs with system rights and may only live in a folder that only administrators can change. This installation is in "%2". Please uninstall CouchHUD and install it again.
de.ServiceStuck=Der CouchHUD-Dienst lässt sich nicht beenden. Bitte den PC neu starten und das Setup erneut ausführen.
en.ServiceStuck=The CouchHUD service cannot be stopped. Please restart the PC and run the setup again.
de.ServiceFailed=Der CouchHUD-Dienst konnte nicht eingerichtet oder gestartet werden. Bis er läuft, gehen Sperre, Updates und automatische Anmeldung nicht. Bitte das Setup erneut ausführen oder in CouchHUD „Dienst reparieren“ wählen.
en.ServiceFailed=The CouchHUD service could not be set up or started. Until it runs, the lock, updates and automatic sign-in do not work. Please run the setup again or choose "Repair service" in CouchHUD.
de.ServicePipeSquatted=Der CouchHUD-Dienst wurde nicht gestartet: Ein anderes Programm belegt bereits seinen Anschluss (die Named Pipe des Dienstes). Aus Sicherheit läuft der Dienst dann nicht; bis er läuft, gehen Sperre, Updates und automatische Anmeldung nicht. Bitte den PC neu starten und das Setup danach erneut ausführen. Tritt es wieder auf, das Programm suchen, das sich als CouchHUD-Dienst ausgibt (Ereignisanzeige, Anwendung, Quelle CouchHud.Service).
en.ServicePipeSquatted=The CouchHUD service was not started: another program already holds its connection (the service's named pipe). For safety the service does not run then; until it runs, the lock, updates and automatic sign-in do not work. Please restart the PC and then run the setup again. If it happens again, look for the program that poses as the CouchHUD service (Event Viewer, Application, source CouchHud.Service).

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked
Name: "autostart"; Description: "{cm:Autostart}"; Flags: unchecked

[InstallDelete]
; Only single program files an older version installed and this one no longer has (an update copies over the folder, it does not
; clean it). Never folders, never wildcards, never anything with user data: InstallerTests keeps this an exact allowlist.
; 0.5 renamed the built-in skins (deepsea.css, greenlight.css, neoncity.css).
Type: files; Name: "{app}\ui\skins\ps4.css"
Type: files; Name: "{app}\ui\skins\xbox.css"
Type: files; Name: "{app}\ui\skins\cyberpunk.css"

[Files]
; Helpers that stop and restart CouchHUD (extracted to {tmp} on demand, never installed).
Source: "stop-couchhud.ps1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "restart-couchhud.ps1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "\{#ServiceFolder},\{#ServiceFolder}\*"; Flags: ignoreversion recursesubdirs createallsubdirs
; The service on its own line: its folder gets its own access list (HardenServiceFolder) before the service is registered.
Source: "{#PublishDir}\{#ServiceFolder}\*"; DestDir: "{app}\{#ServiceFolder}"; Flags: ignoreversion recursesubdirs createallsubdirs
#ifdef WebView2Setup
Source: "{#WebView2Setup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Per user, like any autostart program; removed with the uninstall. Never in an update the service runs (/SERVICEUPDATE=1): that
; setup runs as SYSTEM, its HKCU is HKU\S-1-5-18, and the remembered task selection would put the entry there (P0 spike).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "CouchHud"; ValueData: """{app}\{#AppExe}"""; Tasks: autostart; Check: not IsServiceUpdate; Flags: uninsdeletevalue
; The event source of the service (it never registers one itself). Gone with the uninstall.
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Services\EventLog\Application\{#EventSource}"; ValueType: expandsz; ValueName: "EventMessageFile"; ValueData: "{#EventMessageFile}"; Flags: uninsdeletekey

[Run]
#ifdef WebView2Setup
Filename: "{tmp}\{#WebView2Setup}"; Parameters: "/silent /install"; StatusMsg: "{cm:WebView2}"; Check: NeedsWebView2; Flags: waituntilterminated
#endif
; runasoriginaluser: CouchHUD itself never runs elevated.
Filename: "{app}\{#AppExe}"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallDelete]
Type: files; Name: "{app}\updating"
Type: filesandordirs; Name: "{app}\policy"
; Written by the setup (WriteRepairScript), so not in the uninstall log.
Type: files; Name: "{app}\{#RepairScript}"
; The service's data (written by the service, not by the setup). Inside the admin-only service folder: no user can put a link there.
Type: filesandordirs; Name: "{app}\{#ServiceFolder}\{#ServiceDataFolder}"
; Folders of the program that an update over an older version did not note as its own (the 0.4.1 acceptance test left ui\i18n\de and
; en behind): removed when empty, deepest first (processed in this order after the files). Nothing that is not empty goes.
Type: dirifempty; Name: "{app}\ui\i18n\de"
Type: dirifempty; Name: "{app}\ui\i18n\en"
Type: dirifempty; Name: "{app}\ui\i18n"
Type: dirifempty; Name: "{app}\ui\assets"
Type: dirifempty; Name: "{app}\ui\components"
Type: dirifempty; Name: "{app}\ui\core"
Type: dirifempty; Name: "{app}\ui\remote"
Type: dirifempty; Name: "{app}\ui\skins"
Type: dirifempty; Name: "{app}\ui\views"
Type: dirifempty; Name: "{app}\ui"
Type: dirifempty; Name: "{app}\plugins\CouchHud.Plugin.Wand"
Type: dirifempty; Name: "{app}\plugins"
Type: dirifempty; Name: "{app}\runtimes\win-x64\native"
Type: dirifempty; Name: "{app}\runtimes\win-x64"
Type: dirifempty; Name: "{app}\runtimes"
Type: dirifempty; Name: "{app}\cs"
Type: dirifempty; Name: "{app}\de"
Type: dirifempty; Name: "{app}\es"
Type: dirifempty; Name: "{app}\fr"
Type: dirifempty; Name: "{app}\it"
Type: dirifempty; Name: "{app}\ja"
Type: dirifempty; Name: "{app}\ko"
Type: dirifempty; Name: "{app}\pl"
Type: dirifempty; Name: "{app}\pt-BR"
Type: dirifempty; Name: "{app}\ru"
Type: dirifempty; Name: "{app}\tr"
Type: dirifempty; Name: "{app}\zh-Hans"
Type: dirifempty; Name: "{app}\zh-Hant"
Type: dirifempty; Name: "{app}\{#ServiceFolder}"
Type: dirifempty; Name: "{app}"

[UninstallRun]
; Before the files go: shell entry, Task Manager policy, the service (stopped, deleted, its ProgramData state), scheduled tasks,
; firewall rule, Game Bar. If the service is still there afterwards, CurUninstallStepChanged removes it natively.
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-cleanup"; StatusMsg: "{cm:Cleanup}"; Flags: waituntilterminated runhidden; RunOnceId: "CouchHudCleanup"

[Code]
const
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

function WebView2Version(Root: Integer; Key: String): String;
begin
  if not RegQueryStringValue(Root, Key, 'pv', Result) then Result := '';
end;

{ Only a machine-wide runtime counts (32- or 64-bit view of HKLM): a per-user runtime (HKCU, files in %LOCALAPPDATA%) can be changed
  by every program of the user, and CouchHUD passes the machine-wide folder to WebView2 explicitly. So the machine-wide runtime is
  installed even when the user already has one of their own. }
function NeedsWebView2: Boolean;
var
  Version: String;
begin
  Version := WebView2Version(HKLM32, WebView2Key);
  if ((Version = '') or (Version = '0.0.0.0')) and IsWin64 then Version := WebView2Version(HKLM64, WebView2Key);
  Result := (Version = '') or (Version = '0.0.0.0');
end;

type
  TNowFileTime = record
    Lo: Cardinal;
    Hi: Cardinal;
  end;

procedure GetSystemTimeAsFileTime(var T: TNowFileTime); external 'GetSystemTimeAsFileTime@kernel32.dll stdcall';
function GetCurrentProcessId: Cardinal; external 'GetCurrentProcessId@kernel32.dll stdcall';

{ The marker "unixMilliseconds setupPid" in the program folder (UpdateMarker in CouchHud.Core): the guard of
  CouchHUD respects it while it is young and this process lives, and neither locks Windows nor restarts the old
  version. It lives in the program folder, not in %APPDATA%\CouchHud: only admins can write there, and %APPDATA% of an elevated
  or remote setup is another user's than the one running CouchHUD. }
procedure WriteUpdateMarker;
var
  T: TNowFileTime;
  Ms: Int64;
begin
  GetSystemTimeAsFileTime(T);
  Ms := (Int64(T.Hi) * 4294967296 + Int64(T.Lo)) div 10000 - 11644473600000;
  SaveStringToFile(ExpandConstant('{app}\updating'), IntToStr(Ms) + ' ' + IntToStr(GetCurrentProcessId), False);
end;

function StateFile: String;
begin
  Result := ExpandConstant('{tmp}\couchhud-sessions.txt');
end;

function RunScript(Script, Extra: String): Integer;
var
  ResultCode: Integer;
begin
  Result := -1;
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\') + Script +
    '" -AppDir "' + ExpandConstant('{app}') + '" -StateFile "' + StateFile + '"' + Extra, '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
  then Result := ResultCode;
end;

{ ---- Command line of an update that CouchHud.Service runs (P6) ---------------------------------------------------------------
  /SERVICEUPDATE=1   the setup runs as SYSTEM: no per-user entries (HKCU is HKU\S-1-5-18 then). Pass /MERGETASKS=!autostart too.
  /KEEPSESSION=<id>  the session of the user who asked for the update: stop-couchhud.ps1 does not disconnect it. Digits only.
  /LOG="<app folder>\service\data\logs\setup-<version>.log"   the setup log, next to the service's own data (admin-only). }

function IsServiceUpdate: Boolean;
begin
  Result := ExpandConstant('{param:SERVICEUPDATE|0}') = '1';
end;

function KeepSessionArgument: String;
var
  Value: String;
  I: Integer;
begin
  Result := '';
  Value := ExpandConstant('{param:KEEPSESSION|}');
  if Value = '' then Exit;
  if Length(Value) > 5 then
  begin
    Log('KEEPSESSION ignored: too long');
    Exit;
  end;
  for I := 1 to Length(Value) do
    if (Value[I] < '0') or (Value[I] > '9') then
    begin
      Log('KEEPSESSION ignored: not a session number');
      Exit;
    end;
  Result := ' -KeepSession ' + Value;
end;

{ ---- CouchHud.Service ---------------------------------------------------------------------------------------------------------
  Only native programs by absolute path (sc.exe, icacls.exe, taskkill.exe from the system folder), never an elevated .NET program
  (M15). No braces inside these comments: a brace comment ends at the first closing brace.
  Every call is logged with its exit code (/LOG). }

type
  TServiceStatus = record
    ServiceType: Cardinal;
    CurrentState: Cardinal;
    ControlsAccepted: Cardinal;
    Win32ExitCode: Cardinal;
    ServiceSpecificExitCode: Cardinal;
    CheckPoint: Cardinal;
    WaitHint: Cardinal;
  end;

function ScOpenManager(MachineName, DatabaseName: String; DesiredAccess: Cardinal): THandle;
  external 'OpenSCManagerW@advapi32.dll stdcall';
function ScOpenService(Manager: THandle; ServiceName: String; DesiredAccess: Cardinal): THandle;
  external 'OpenServiceW@advapi32.dll stdcall';
function ScQueryStatus(Service: THandle; var Status: TServiceStatus): Boolean;
  external 'QueryServiceStatus@advapi32.dll stdcall';
function ScCloseHandle(Handle: THandle): Boolean;
  external 'CloseServiceHandle@advapi32.dll stdcall';

const
  SC_MANAGER_CONNECT = $0001;
  SERVICE_QUERY_STATUS = $0004;
  SERVICE_STOPPED = 1;
  SERVICE_RUNNING = 4;
  ERROR_SERVICE_ALREADY_RUNNING = 1056;
  ERROR_SERVICE_SPECIFIC_ERROR = 1066;

var
  ServiceTouched: Boolean;  { PrepareToInstall disabled and stopped an installed service }
  ServiceReady: Boolean;    { ssPostInstall set the service up and it runs }
  ServiceAttempted: Boolean; { ssPostInstall reached: from here on ConfigureService decides, no RestoreService }
  ServiceFailed: Boolean;   { ssPostInstall could not set it up: exit code ExitServiceFailed }
  WatchServiceExit: Boolean; { after the start in ssPostInstall: ServiceState notes how the new service stopped }
  ServicePipeSquatted: Boolean; { the new service stopped with ServiceExitPipeSquatted: another program holds its pipe name }

{ 0 when the service is not installed (or the SCM cannot be asked), else SERVICE_STOPPED .. SERVICE_PAUSED. Locale-independent,
  unlike the text sc.exe prints. }
function ServiceState: Cardinal;
var
  Manager, Service: THandle;
  Status: TServiceStatus;
begin
  Result := 0;
  Manager := ScOpenManager('', 'ServicesActive', SC_MANAGER_CONNECT);
  if Manager = 0 then Exit;
  try
    Service := ScOpenService(Manager, '{#ServiceName}', SERVICE_QUERY_STATUS);
    if Service <> 0 then
    try
      if ScQueryStatus(Service, Status) then
      begin
        Result := Status.CurrentState;
        { Only for the message to the administrator: the outcome (exit code, fail-closed) is the same for every failure. }
        if WatchServiceExit and (Status.Win32ExitCode = ERROR_SERVICE_SPECIFIC_ERROR) and (Status.ServiceSpecificExitCode = {#ServiceExitPipeSquatted}) then
          ServicePipeSquatted := True;
      end;
    finally
      ScCloseHandle(Service);
    end;
  finally
    ScCloseHandle(Manager);
  end;
end;

function WaitForServiceState(Wanted: Cardinal; Milliseconds: Integer): Boolean;
var
  Waited: Integer;
begin
  Waited := 0;
  Result := ServiceState = Wanted;
  while not Result and (Waited < Milliseconds) do
  begin
    Sleep(250);
    Waited := Waited + 250;
    Result := ServiceState = Wanted;
  end;
end;

function RunSystemProgram(Program_, Params: String): Integer;
var
  ResultCode: Integer;
begin
  Result := -1;
  if Exec(ExpandConstant('{sys}\') + Program_, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then Result := ResultCode;
  Log(Program_ + ' ' + Params + ' -> ' + IntToStr(Result));
end;

function RunSc(Params: String): Integer;
begin
  Result := RunSystemProgram('sc.exe', Params);
end;

function ServiceFolderPath: String;
begin
  Result := ExpandConstant('{app}\{#ServiceFolder}');
end;

{ The old SYSTEM tasks of 0.4.0 (\CouchHud\<SID>\Policy-*, Couch-*, PC-*) to SYSTEM and administrators only, before the new service runs
  (security review M-1 of finding B1): no moment after the update in which a user may still start one, and also when the service
  cannot start (a squatted pipe name). The new service program does it (Task Scheduler COM), from the folder just hardened; it only
  takes rights away. A failure is logged, the service does it again at its start. }
procedure RestrictLegacyTasks;
var
  ResultCode: Integer;
begin
  if Exec(ServiceFolderPath + '\{#ServiceExe}', '--restrict-legacy-tasks', ExpandConstant('{sys}'), SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Log('service: old tasks restricted -> ' + IntToStr(ResultCode))
  else
    Log('service: old tasks not restricted (not started)');
end;

{ The parameters of "sc create" and "sc config": own process, LocalSystem, automatic (not delayed: the service must answer before
  the users' Run entries and the shell need it). sc reads binPath= as one argument: the quoted path inside quotes. }
function ServiceParams: String;
begin
  Result := 'type= own start= auto error= normal obj= LocalSystem binPath= "\"' + ServiceFolderPath + '\{#ServiceExe}\"" DisplayName= "{#ServiceDisplayName}"';
end;

{ The hardening after create/config (design §1, ServiceHardening): failure actions, sid type, required privileges, service DACL. }
function HardeningCommands: TArrayOfString;
begin
  SetArrayLength(Result, 6);
  Result[0] := 'description {#ServiceName} "{#ServiceDescription}"';
  Result[1] := 'failure {#ServiceName} reset= {#ServiceFailureReset} actions= {#ServiceFailureActions}';
  Result[2] := 'failureflag {#ServiceName} 1';
  Result[3] := 'sidtype {#ServiceName} {#ServiceSidType}';
  Result[4] := 'privs {#ServiceName} {#ServicePrivileges}';
  Result[5] := 'sdset {#ServiceName} {#ServiceSddl}';
end;

{ icacls: the service folder does not inherit from the app folder; owner Administrators for the folder and everything in it. }
function FolderAclParams: String;
begin
  Result := '"' + ServiceFolderPath + '" /inheritance:r /grant:r {#ServiceFolderGrants}';
end;

function FolderOwnerParams: String;
begin
  Result := '"' + ServiceFolderPath + '" /setowner *S-1-5-32-544 /T /C /Q';
end;

function ServiceDataPath: String;
begin
  Result := ServiceFolderPath + '\{#ServiceDataFolder}';
end;

{ icacls: the data folder does not inherit from the service folder (no Users read); SYSTEM and Administrators only. }
function DataAclParams: String;
begin
  Result := '"' + ServiceDataPath + '" /inheritance:r /grant:r {#ServiceDataGrants}';
end;

{ Access list first, then the data folder and its own list (SYSTEM and Administrators only), then the owner of everything. }
function HardenServiceFolder: Boolean;
begin
  Result := RunSystemProgram('icacls.exe', FolderAclParams) = 0;
  if Result and not DirExists(ServiceDataPath) then Result := ForceDirectories(ServiceDataPath);
  if Result then Result := RunSystemProgram('icacls.exe', DataAclParams) = 0;
  if Result then Result := RunSystemProgram('icacls.exe', FolderOwnerParams) = 0;
end;

{ The app folder must lie below Program Files (64-bit), so only administrators can replace what SYSTEM runs. The service checks the same at
  every start (InstallProtection) and refuses to act from an unprotected place. }
function InProgramFiles: Boolean;
var
  App, Root: String;
  I: Integer;
begin
  App := AddBackslash(Lowercase(ExpandConstant('{app}')));
  Root := AddBackslash(Lowercase(ExpandConstant('{commonpf64}')));
  Result := (Pos(Root, App) = 1) and (Length(App) > Length(Root)) and (Pos('..', App) = 0) and (Pos('~', App) = 0);
  { The path ends up in command lines and in the repair batch file: nothing cmd.exe would read as an operator or a variable. }
  for I := 1 to Length(App) do
    if Pos(Copy(App, I, 1), '&|^%!<>"') > 0 then Result := False;
end;

{ Before files are replaced: first nobody but SYSTEM and administrators may start the service and the SCM's failure actions are
  off (so neither a user nor the SCM brings it back in between), then stop; a hanging service is ended. The start type stays
  automatic: an update that is cut off leaves a service that still starts with Windows. ssPostInstall (ConfigureService) and an
  abort or rollback (RestoreService) put the DACL and the failure actions back. False when it still runs. }
function StopServiceForUpdate: Boolean;
begin
  Result := True;
  if ServiceState = 0 then Exit;
  ServiceTouched := True;
  RunSc('sdset {#ServiceName} {#ServiceSddlUpdating}');
  RunSc('failure {#ServiceName} reset= 0 actions= ""');
  if ServiceState <> SERVICE_STOPPED then
  begin
    RunSc('stop {#ServiceName}');
    if not WaitForServiceState(SERVICE_STOPPED, 30000) then
    begin
      RunSystemProgram('taskkill.exe', '/F /FI "SERVICES eq {#ServiceName}"');
      Result := WaitForServiceState(SERVICE_STOPPED, 10000);
    end;
  end;
end;

{ Fail closed when the service could not be set up completely: it must never stay registered without its hardening. One this
  setup created is deleted again; an existing one (an update) is disabled until the setup runs again or an administrator repairs it. }
procedure FailClosed(Created: Boolean);
begin
  if ServiceState = 0 then Exit;
  if Created then
  begin
    Log('service: hardening incomplete, deleting the service this setup created');
    RunSc('delete {#ServiceName}');
  end
  else
  begin
    Log('service: hardening incomplete, disabling the service until it is repaired');
    RunSc('config {#ServiceName} start= disabled');
    RunSc('stop {#ServiceName}');
  end;
end;

{ Create or reconfigure, harden, start, and check that it still runs a moment later (a service that ends at once, e.g. because its
  pipe name is taken, would otherwise count as started). A failed step of the setup itself ends in FailClosed; a service that is
  set up completely but does not keep running stays (hardened) and the setup reports it. }
function ConfigureService: Boolean;
var
  Commands: TArrayOfString;
  I, Code: Integer;
  Created: Boolean;
begin
  Result := False;
  Created := ServiceState = 0;
  if not FileExists(ServiceFolderPath + '\{#ServiceExe}') then
  begin
    Log('service: ' + ServiceFolderPath + '\{#ServiceExe} missing');
    FailClosed(False);
    Exit;
  end;
  if not HardenServiceFolder then
  begin
    FailClosed(False);
    Exit;
  end;
  RestrictLegacyTasks;
  if Created then Code := RunSc('create {#ServiceName} ' + ServiceParams)
  else Code := RunSc('config {#ServiceName} ' + ServiceParams);
  if Code <> 0 then
  begin
    FailClosed(Created);
    Exit;
  end;
  Commands := HardeningCommands;
  for I := 0 to GetArrayLength(Commands) - 1 do
    if RunSc(Commands[I]) <> 0 then
    begin
      FailClosed(Created);
      Exit;
    end;
  Code := RunSc('start {#ServiceName}');
  WatchServiceExit := True;
  try
    if (Code <> 0) and (Code <> ERROR_SERVICE_ALREADY_RUNNING) then
    begin
      Code := ServiceState;  { notes a squatted pipe, see ServiceState }
      Exit;
    end;
    if WaitForServiceState(SERVICE_RUNNING, 30000) then
    begin
      Sleep(2000);
      Result := ServiceState = SERVICE_RUNNING;
    end;
    if not Result then Log('service: stopped again right after its start (see the Application event log, source {#EventSource})');
    if ServicePipeSquatted then Log('service: pipe squatting suspected, exit code {#ServiceExitPipeSquatted} of the service');
  finally
    WatchServiceExit := False;
  end;
end;

{ After an abort or a rollback: the service that PrepareToInstall stopped runs again (with the files that are there now). }
procedure RestoreService;
begin
  if ServiceState = 0 then Exit;
  RunSc('failure {#ServiceName} reset= {#ServiceFailureReset} actions= {#ServiceFailureActions}');
  RunSc('sdset {#ServiceName} {#ServiceSddl}');
  RunSc('start {#ServiceName}');
end;

{ Uninstall: same order as StopServiceForUpdate, then delete. Used when --uninstall-cleanup left the service behind. }
procedure RemoveService;
begin
  if ServiceState = 0 then Exit;
  Log('service: still installed after the cleanup, removing it');
  StopServiceForUpdate;
  RunSc('delete {#ServiceName}');
  DelTree(ServiceFolderPath, True, True, True);
end;

procedure AddLine(var Lines: TArrayOfString; Line: String);
var
  N: Integer;
begin
  N := GetArrayLength(Lines);
  SetArrayLength(Lines, N + 1);
  Lines[N] := Line;
end;

{ The emergency repair (E11) an administrator runs through UAC, e.g. from CouchHUD's "Repair service":
    <system folder>\cmd.exe /d /c ""<app folder>\repair-service.cmd""
  It re-creates the service with exactly the configuration above and nothing else. A batch file, not PowerShell: an elevated
  PowerShell is a .NET program and would load a profiler from the user's environment (M15); cmd.exe, sc.exe, icacls.exe and
  reg.exe do not. Every program by its absolute path, no environment variable is expanded (/d: no AutoRun either), "if errorlevel"
  instead of %ERRORLEVEL% (a variable of that name in the environment would shadow it). Written at install time because only the
  setup knows the system and app folders without reading the environment; it lives in the app folder, which only administrators
  can change. }
procedure WriteRepairScript;
var
  Lines, Commands: TArrayOfString;
  Sc, Icacls, MessageFile: String;
  N: Integer;
begin
  Sc := '"' + ExpandConstant('{sys}\sc.exe') + '"';
  Icacls := '"' + ExpandConstant('{sys}\icacls.exe') + '"';
  AddLine(Lines, '@echo off');
  AddLine(Lines, 'rem CouchHUD: repairs the CouchHUD service. Written by the setup, run elevated. Exit code 0: the service runs.');
  AddLine(Lines, 'setlocal EnableExtensions DisableDelayedExpansion');
  AddLine(Lines, 'set "RC=0"');
  AddLine(Lines, Sc + ' config {#ServiceName} start= disabled >nul 2>&1');
  AddLine(Lines, Sc + ' stop {#ServiceName} >nul 2>&1');
  AddLine(Lines, '"' + ExpandConstant('{sys}\ping.exe') + '" -n 6 127.0.0.1 >nul');
  AddLine(Lines, '"' + ExpandConstant('{sys}\taskkill.exe') + '" /F /FI "SERVICES eq {#ServiceName}" >nul 2>&1');
  AddLine(Lines, '"' + ExpandConstant('{sys}\ping.exe') + '" -n 3 127.0.0.1 >nul');
  AddLine(Lines, Icacls + ' ' + FolderAclParams + ' >nul');
  AddLine(Lines, 'if errorlevel 1 set "RC=1"');
  AddLine(Lines, 'if not exist "' + ServiceDataPath + '\" mkdir "' + ServiceDataPath + '"');
  AddLine(Lines, Icacls + ' ' + DataAclParams + ' >nul');
  AddLine(Lines, 'if errorlevel 1 set "RC=1"');
  AddLine(Lines, Icacls + ' ' + FolderOwnerParams + ' >nul');
  AddLine(Lines, 'if errorlevel 1 set "RC=1"');
  AddLine(Lines, Sc + ' query {#ServiceName} >nul 2>&1');
  AddLine(Lines, 'if errorlevel 1060 if not errorlevel 1061 goto create');
  AddLine(Lines, Sc + ' config {#ServiceName} ' + ServiceParams);
  AddLine(Lines, 'if errorlevel 1 set "RC=1"');
  AddLine(Lines, 'goto harden');
  AddLine(Lines, ':create');
  AddLine(Lines, Sc + ' create {#ServiceName} ' + ServiceParams);
  AddLine(Lines, 'if errorlevel 1 set "RC=1"');
  AddLine(Lines, ':harden');
  Commands := HardeningCommands;
  for N := 0 to GetArrayLength(Commands) - 1 do
  begin
    AddLine(Lines, Sc + ' ' + Commands[N]);
    AddLine(Lines, 'if errorlevel 1 set "RC=1"');
  end;
  { %% is a literal % in a batch file: the value stays REG_EXPAND_SZ with %SystemRoot% in it. }
  MessageFile := '{#EventMessageFile}';
  StringChangeEx(MessageFile, '%', '%%', True);
  AddLine(Lines, '"' + ExpandConstant('{sys}\reg.exe') + '" add "HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Application\{#EventSource}" /v EventMessageFile /t REG_EXPAND_SZ /d "' + MessageFile + '" /f >nul');
  AddLine(Lines, 'if errorlevel 1 set "RC=1"');
  AddLine(Lines, Sc + ' start {#ServiceName}');
  AddLine(Lines, 'if errorlevel 1056 if not errorlevel 1057 goto started');
  AddLine(Lines, 'if errorlevel 1 set "RC=1"');
  AddLine(Lines, ':started');
  AddLine(Lines, 'exit /b %RC%');
  if not SaveStringsToFile(ExpandConstant('{app}\{#RepairScript}'), Lines, False) then Log('service: repair script not written');
end;

{ An update ends CouchHUD itself instead of relying on the Restart Manager, which fails across sessions (setup
  run by remote maintenance, software distribution or a service: "Session Mismatch") and when CouchHUD refuses
  to close:
  1. the marker (above), so the guard doesn't lock Windows or restart the old version in the moment between,
  2. stop-couchhud.ps1: ends every program from the program folder in all sessions (CouchHUD and its windowless
     guard), notes the sessions and users, waits up to 15 s until the files are free,
  3. then the service, through the SCM (after the shells, so no shell sees its service vanish while it still runs).
  After the setup (DeinitializeSetup, also after an abort and rollback) restart-couchhud.ps1 starts CouchHUD
  again in each of these sessions as that user, not elevated, through a one-shot scheduled task (Interactive
  logon type, no password). Needed above all when CouchHUD is the shell: Winlogon starts the shell only at
  sign-in, the session would stay black. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if not InProgramFiles then
  begin
    Result := FmtMessage(CustomMessage('NotProgramFiles'), [ExpandConstant('{commonpf64}'), ExpandConstant('{app}')]);
    Exit;
  end;
  if FileExists(ExpandConstant('{app}\{#AppExe}')) or (ServiceState <> 0) then
  begin
    ExtractTemporaryFile('stop-couchhud.ps1');
    ExtractTemporaryFile('restart-couchhud.ps1');
  end;
  if FileExists(ExpandConstant('{app}\{#AppExe}')) then
  begin
    WriteUpdateMarker;
    DeleteFile(StateFile);
    RunScript('stop-couchhud.ps1', ' -MaxSeconds 15' + KeepSessionArgument);
  end;
  if not StopServiceForUpdate then
  begin
    Result := CustomMessage('ServiceStuck');
    Exit;
  end;
  { Whatever still runs from the service folder now (a child of the service): ended by image path and start time, never the SCM. }
  if ServiceTouched then RunScript('stop-couchhud.ps1', ' -ServiceLeftovers -MaxSeconds 10');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    WriteRepairScript;
    ServiceAttempted := True;
    ServiceReady := ConfigureService;
    if not ServiceReady then
    begin
      ServiceFailed := True;
      Log('service: setup failed, exit code {#ExitServiceFailed}');
      if not WizardSilent then
      begin
        if ServicePipeSquatted then MsgBox(CustomMessage('ServicePipeSquatted'), mbError, MB_OK)
        else MsgBox(CustomMessage('ServiceFailed'), mbError, MB_OK);
      end;
    end;
    DeleteFile(ExpandConstant('{app}\updating'));
  end;
end;

function GetCustomSetupExitCode: Integer;
begin
  Result := 0;
  if ServiceFailed then Result := {#ExitServiceFailed};
end;

procedure DeinitializeSetup;
begin
  { Aborted or rolled back before the new files were set up: the old service as it was (DACL, failure actions, running). }
  if ServiceTouched and not ServiceAttempted then RestoreService;
  if FileExists(StateFile) then
  begin
    DeleteFile(ExpandConstant('{app}\updating'));
    RunScript('restart-couchhud.ps1', '');
    DeleteFile(StateFile);
  end;
end;

const
  CouchMutex = 'Local\CouchHud.Instance';

function SetEnvVar(Name, Value: String): Boolean; external 'SetEnvironmentVariableW@kernel32.dll stdcall';
function RemoveEnvVar(Name: String; NoValue: THandle): Boolean; external 'SetEnvironmentVariableW@kernel32.dll stdcall';

{ The setup runs elevated with the environment of the user who started it, and every program it starts inherits that environment
  (PowerShell 5.1 is .NET Framework, the uninstall cleanup is .NET): a profiler or startup hook set in HKCU\Environment would load
  into an elevated process (M15). These variables go before anything is started; PowerShell gets the system module path only.
  Residual: other COMPlus_/DOTNET_ settings cannot be listed from here and are not removed (none of them is known to load code), and
  Windows PowerShell still adds the personal module folder of the elevated account (only that account can write there). }
function UnsafeVariables: String;
begin
  Result := 'COR_ENABLE_PROFILING,COR_PROFILER,COR_PROFILER_PATH,COR_PROFILER_PATH_32,COR_PROFILER_PATH_64,' +
    'CORECLR_ENABLE_PROFILING,CORECLR_PROFILER,CORECLR_PROFILER_PATH,CORECLR_PROFILER_PATH_32,CORECLR_PROFILER_PATH_64,' +
    'COMPlus_EnableProfiling,DOTNET_EnableProfiling,COMPlus_InstallRoot,COMPlus_Version,COMPlus_DefaultVersion,' +
    'DOTNET_STARTUP_HOOKS,DOTNET_ADDITIONAL_DEPS,DOTNET_SHARED_STORE,DOTNET_ROOT,DOTNET_ROOT(x86),DOTNET_ROOT_X64,' +
    'PSModulePath,PSExecutionPolicyPreference,';
end;

procedure CleanEnvironment;
var
  Rest: String;
  Comma: Integer;
begin
  Rest := UnsafeVariables;
  Comma := Pos(',', Rest);
  while Comma > 0 do
  begin
    RemoveEnvVar(Copy(Rest, 1, Comma - 1), 0);
    Rest := Copy(Rest, Comma + 1, Length(Rest));
    Comma := Pos(',', Rest);
  end;
  SetEnvVar('PSModulePath', ExpandConstant('{sys}\WindowsPowerShell\v1.0\Modules'));
end;

{ What AppMutex did, without stopping a silent update: an interactive setup doesn't run over a running CouchHUD.
  A silent one (the self-update) goes on, the Restart Manager closes CouchHUD (CloseApplications). }
function InitializeSetup: Boolean;
begin
  CleanEnvironment;
  Result := True;
  if not WizardSilent and CheckForMutexes(CouchMutex) then
  begin
    MsgBox(CustomMessage('StillRunning'), mbError, MB_OK);
    Result := False;
  end;
end;

{ Not while CouchHUD is running. Silent (winget, scripts): end CouchHUD and its guard, which is the same program.
  The marker keeps the guard from locking Windows and restarting CouchHUD in the moment between. }
{ Read before --uninstall-cleanup resets it: was CouchHUD this person's Windows shell? Then the session has no shell after the
  uninstall. The uninstaller runs as admin and Inno offers no unelevated start at uninstall time (ExecAsOriginalUser is setup
  only), and an explorer started from here would run elevated (the whole desktop and everything opened from it as admin).
  So it does not start one; it asks the person to sign out and in again, and Winlogon starts explorer normally. }
var
  WasShell: Boolean;

function CouchHudWasShell: Boolean;
var
  Shell: String;
begin
  Result := RegQueryStringValue(HKCU, 'Software\Microsoft\Windows NT\CurrentVersion\Winlogon', 'Shell', Shell)
    and (Pos(Lowercase('{#AppExe}'), Lowercase(Shell)) > 0);
end;

function InitializeUninstall: Boolean;
var
  ResultCode: Integer;
begin
  CleanEnvironment;
  WasShell := CouchHudWasShell;
  Result := True;
  if CheckForMutexes(CouchMutex) then
  begin
    if UninstallSilent then
    begin
      WriteUpdateMarker;
      Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /T /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      Sleep(1500);
    end
    else
    begin
      MsgBox(CustomMessage('StillRunning'), mbError, MB_OK);
      Result := False;
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { --uninstall-cleanup removes the service; if it could not (or did not run), natively here. }
    RemoveService;
    if not UninstallSilent then
    begin
      if MsgBox(CustomMessage('DeleteData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      begin
        DelTree(ExpandConstant('{userappdata}\CouchHud'), True, True, True);
        DelTree(ExpandConstant('{localappdata}\CouchHud'), True, True, True);
      end;
      MsgBox(CustomMessage('AutoLogonNote'), mbInformation, MB_OK);
      if WasShell then MsgBox(CustomMessage('SignOutNote'), mbInformation, MB_OK);
    end
    else if WasShell then Log('CouchHUD was the Windows shell: the session needs a sign-out and sign-in for the Windows desktop.');
  end;
end;
