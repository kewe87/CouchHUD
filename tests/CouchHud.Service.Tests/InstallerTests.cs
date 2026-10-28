using System.Text.RegularExpressions;
using CouchHud.Privileged;
using CouchHud.Service.Contracts;
using CouchHud.Service.Logging;

namespace CouchHud.Service.Tests;

/// <summary>
/// The setup (installer/CouchHud.iss) applies the service's hardening with sc.exe; the values there are literals. These tests pin
/// them to <see cref="ServiceHardening"/> and the shared names to <see cref="ServiceInstall"/>, so neither side can drift, and check
/// the parts of the update/uninstall wiring that other packages rely on (parameter names, script parameters, CI publish).
/// </summary>
public class InstallerTests
{
    private static readonly string Root = FindRoot();
    private static readonly string Iss = File.ReadAllText(Path.Combine(Root, "installer", "CouchHud.iss"));
    private static readonly Dictionary<string, string> Defines = ReadDefines(Iss);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CouchHud.slnx"))) return dir.FullName;
        }
        throw new InvalidOperationException("repository root (CouchHud.slnx) not found");
    }

    private static Dictionary<string, string> ReadDefines(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(text, @"^#define\s+(\w+)\s+(?:""([^""]*)""|(\d+))\s*$", RegexOptions.Multiline))
        {
            result[m.Groups[1].Value] = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }
        return result;
    }

    private static string Code => Iss[Iss.IndexOf("[Code]", StringComparison.Ordinal)..];

    /// <summary>The entries of [InstallDelete] (none when the section is missing), comments left out.</summary>
    private static List<string> InstallDeleteEntries()
    {
        var start = Iss.IndexOf("\n[InstallDelete]", StringComparison.Ordinal);
        if (start < 0) return [];
        var body = Iss[(start + "\n[InstallDelete]".Length)..];
        var end = Regex.Match(body, @"^\[\w+\]", RegexOptions.Multiline);
        if (end.Success) body = body[..end.Index];
        return body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(';')).ToList();
    }

    /// <summary>
    /// What an update may delete before copying: an exact allowlist of single program files that older versions installed. No
    /// folders, no wildcards, nothing of the service or of user data. A new entry is added here on purpose, never by pattern.
    /// </summary>
    private static readonly string[] AllowedInstallDeletes =
    [
        // 0.5 renamed the built-in skins.
        @"Type: files; Name: ""{app}\ui\skins\ps4.css""",
        @"Type: files; Name: ""{app}\ui\skins\xbox.css""",
        @"Type: files; Name: ""{app}\ui\skins\cyberpunk.css""",
    ];

    [Fact]
    public void An_update_deletes_only_the_allowed_old_program_files()
    {
        Assert.Single(Regex.Matches(Iss, @"^\[InstallDelete\]", RegexOptions.Multiline));
        var entries = InstallDeleteEntries();
        Assert.NotEmpty(entries);
        foreach (var entry in entries)
        {
            Assert.Contains(entry, AllowedInstallDeletes);
            Assert.StartsWith(@"Type: files; Name: ""{app}\", entry);
            Assert.DoesNotContain("*", entry);
            Assert.DoesNotContain("..", entry);
        }
        // The renamed skins are really gone from the program, so nothing the setup installs is deleted again.
        foreach (var old in new[] { "ps4.css", "xbox.css", "cyberpunk.css" }) Assert.False(File.Exists(Path.Combine(Root, "ui", "skins", old)));
    }

    [Fact]
    public void The_sc_values_of_the_setup_are_the_services_hardening()
    {
        Assert.Equal(ServiceHardening.ServiceName, Defines["ServiceName"]);
        Assert.Equal(ServiceHardening.ServiceSddl, Defines["ServiceSddl"]);
        Assert.Equal(ServiceHardening.SidType, Defines["ServiceSidType"]);
        Assert.Equal(string.Join('/', ServiceHardening.RequiredPrivileges), Defines["ServicePrivileges"]);
        Assert.Equal(ServiceHardening.FailureActions, Defines["ServiceFailureActions"]);
        Assert.Equal(ServiceHardening.FailureResetSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture), Defines["ServiceFailureReset"]);
    }

    [Fact]
    public void The_names_are_the_same_everywhere()
    {
        Assert.Equal(ServiceHardening.ServiceName, ServiceInstall.ServiceName);
        Assert.Equal(EventLogSink.Source, ServiceInstall.EventSource);
        Assert.Equal(ServiceInstall.EventSource, Defines["EventSource"]);
        Assert.Equal(ServiceInstall.ServiceFolder, Defines["ServiceFolder"]);
        Assert.Equal(ServiceInstall.ServiceExe, Defines["ServiceExe"]);
        Assert.Equal(ServiceInstall.ServiceExe, typeof(ServiceHardening).Assembly.GetName().Name + ".exe");
        Assert.Equal(ServiceInstall.RepairScript, Defines["RepairScript"]);
        Assert.Equal(ServiceInstall.EventSourceKey, @"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + Defines["EventSource"]);
        Assert.Contains(@"Subkey: ""SYSTEM\CurrentControlSet\Services\EventLog\Application\{#EventSource}""", Iss);
    }

    [Fact]
    public void Every_sc_command_takes_its_values_from_the_defines()
    {
        // Each value appears once, in its #define: the commands use {#…}, never a second copy that could go stale.
        foreach (var key in new[] { "ServiceSddl", "ServicePrivileges", "ServiceFailureActions", "ServiceFolderGrants", "EventMessageFile" })
        {
            Assert.Single(Regex.Matches(Iss, Regex.Escape(Defines[key])));
        }
        var code = Code;
        Assert.Contains("'sdset {#ServiceName} {#ServiceSddl}'", code);
        Assert.Contains("'privs {#ServiceName} {#ServicePrivileges}'", code);
        Assert.Contains("'sidtype {#ServiceName} {#ServiceSidType}'", code);
        Assert.Contains("'failure {#ServiceName} reset= {#ServiceFailureReset} actions= {#ServiceFailureActions}'", code);
        Assert.Contains("'failureflag {#ServiceName} 1'", code);
        Assert.Contains("'description {#ServiceName} \"{#ServiceDescription}\"'", code);
        // Automatic, not delayed (the shell needs it at sign-in), LocalSystem, the quoted path in the protected folder.
        Assert.Contains("start= auto", code);
        Assert.DoesNotContain("delayed-auto", Iss);
        Assert.Contains("obj= LocalSystem", code);
        Assert.Contains("binPath= \"\\\"' + ServiceFolderPath + '\\{#ServiceExe}\\\"\"", code);
        Assert.DoesNotContain("SeDebugPrivilege", Iss);
        Assert.DoesNotContain("SeTcbPrivilege", Iss);
    }

    [Fact]
    public void A_squatted_pipe_is_named_to_the_administrator_and_stays_fail_closed()
    {
        Assert.Equal(ServiceHardening.ExitPipeSquatted.ToString(System.Globalization.CultureInfo.InvariantCulture), Defines["ServiceExitPipeSquatted"]);
        Assert.Equal("1066", ServiceHardening.ServiceSpecificError.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("ERROR_SERVICE_SPECIFIC_ERROR = 1066;", Code);
        Assert.Contains("de.ServicePipeSquatted=", Iss);
        Assert.Contains("en.ServicePipeSquatted=", Iss);
        // Only the message differs: the exit code stays ExitServiceFailed whenever the service does not run.
        Assert.Contains("if ServiceFailed then Result := {#ExitServiceFailed};", Code);
        Assert.Contains("if ServicePipeSquatted then MsgBox(CustomMessage('ServicePipeSquatted')", Code);
    }

    [Fact]
    public void Only_programs_of_the_system_folder_are_run_by_absolute_path()
    {
        var code = Code;
        Assert.Contains("ExpandConstant('{sys}\\') + Program_", code);
        Assert.Contains("RunSystemProgram('sc.exe'", code);
        Assert.Contains("RunSystemProgram('icacls.exe'", code);
        Assert.Contains("RunSystemProgram('taskkill.exe'", code);
        // The repair script: cmd without AutoRun, every program with its full path, no %VAR% expansion but its own RC.
        foreach (var program in new[] { "sc.exe", "icacls.exe", "taskkill.exe", "ping.exe", "reg.exe" })
        {
            Assert.Contains($"ExpandConstant('{{sys}}\\{program}')", code);
        }
        Assert.Contains("\\cmd.exe /d /c \"\"<app folder>\\repair-service.cmd\"\"", Iss);
        Assert.Contains("'exit /b %RC%'", code);
        Assert.Contains("StringChangeEx(MessageFile, '%', '%%', True)", code);
        Assert.DoesNotMatch(new Regex(@"AddLine\([^\n]*%ERRORLEVEL%", RegexOptions.IgnoreCase), code);
    }

    /// <summary>
    /// ISCC does not run on a developer's PC, so a cheap guard for the Pascal pitfall: a brace comment ends at the first closing
    /// brace, so "{ … {app} … }" leaves the rest of the comment as code. ISPP's {#…} is expanded before Pascal sees it.
    /// </summary>
    [Fact]
    public void No_brace_comment_in_the_code_contains_another_brace()
    {
        var code = Code;
        int? opened = null;
        var inString = false;
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            if (inString)
            {
                if (c == '\'') inString = false;
            }
            else if (opened is not null)
            {
                Assert.False(c == '{', $"brace inside the comment that starts at offset {opened}: {code.Substring(opened.Value, Math.Min(120, code.Length - opened.Value))}");
                if (c == '}') opened = null;
            }
            else if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                i = code.IndexOf('\n', i);
                if (i < 0) break;
            }
            else if (c == '\'') inString = true;
            else if (c == '{' && (i + 1 >= code.Length || code[i + 1] != '#')) opened = i;
        }
        Assert.Null(opened);
    }

    [Fact]
    public void The_install_location_is_always_program_files()
    {
        Assert.Contains("DisableDirPage=yes", Iss);
        Assert.Contains(@"DefaultDirName={commonpf}\{#AppDir}", Iss);
        Assert.Contains("if not InProgramFiles then", Code);
        // The service folder gets its own access list: no inheritance; SYSTEM and Administrators full, Users read only.
        Assert.Equal("*S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX", Defines["ServiceFolderGrants"]);
        Assert.Contains("/inheritance:r /grant:r {#ServiceFolderGrants}", Code);
        Assert.Contains("/setowner *S-1-5-32-544 /T /C /Q", Code);
    }

    [Fact]
    public void An_update_run_by_the_service_writes_no_per_user_entry_and_spares_the_users_session()
    {
        // P6 starts the setup with /SERVICEUPDATE=1 /KEEPSESSION=<id> (plus /MERGETASKS=!autostart).
        Assert.Contains("{param:SERVICEUPDATE|0}", Code);
        Assert.Contains("{param:KEEPSESSION|}", Code);
        var run = Iss.Split('\n').Single(l => l.StartsWith("Root: HKCU;", StringComparison.Ordinal) && l.Contains(@"CurrentVersion\Run", StringComparison.Ordinal));
        Assert.Contains("Check: not IsServiceUpdate", run);
        Assert.DoesNotContain(Iss.Split('\n'), l => l.StartsWith("Root: HKCU;", StringComparison.Ordinal) && !l.Contains("Check: not IsServiceUpdate", StringComparison.Ordinal));
        Assert.Contains("' -KeepSession ' + Value", Code);
        Assert.Contains("RunScript('stop-couchhud.ps1', ' -MaxSeconds 15' + KeepSessionArgument)", Code);

        var stop = File.ReadAllText(Path.Combine(Root, "installer", "stop-couchhud.ps1"));
        Assert.Contains("[int]$KeepSession = -1", stop);
        Assert.Contains("$_.SessionId -ne $own -and $_.SessionId -ne $KeepSession", stop);
        // The service is stopped through the SCM, never killed by the script (the SCM would restart it).
        Assert.Contains("$servicePrefix = $prefix + 'service\\'", stop);
    }

    [Fact]
    public void The_service_is_stopped_after_the_shells_and_before_the_files()
    {
        var code = Code;
        var prepare = code[code.IndexOf("function PrepareToInstall", StringComparison.Ordinal)..];
        var stopShells = prepare.IndexOf("RunScript('stop-couchhud.ps1', ' -MaxSeconds 15'", StringComparison.Ordinal);
        var stopService = prepare.IndexOf("StopServiceForUpdate", StringComparison.Ordinal);
        var leftovers = prepare.IndexOf("RunScript('stop-couchhud.ps1', ' -ServiceLeftovers", StringComparison.Ordinal);
        Assert.InRange(stopShells, 0, stopService);
        Assert.True(leftovers > stopService);
        // Before the stop: users may not start it, the SCM does not restart it. Start type stays automatic (an update cut off
        // still leaves a service that starts with Windows).
        var body = code[code.IndexOf("function StopServiceForUpdate", StringComparison.Ordinal)..code.IndexOf("procedure FailClosed", StringComparison.Ordinal)];
        var sdset = body.IndexOf("RunSc('sdset {#ServiceName} {#ServiceSddlUpdating}')", StringComparison.Ordinal);
        var noFailure = body.IndexOf("RunSc('failure {#ServiceName} reset= 0 actions= \"\"')", StringComparison.Ordinal);
        var stop = body.IndexOf("RunSc('stop", StringComparison.Ordinal);
        Assert.InRange(sdset, 0, stop);
        Assert.InRange(noFailure, 0, stop);
        Assert.DoesNotContain("start= disabled", body);
        Assert.Equal(ServiceHardening.ServiceSddl.Replace("(A;;CCLCRPRC;;;IU)", "(A;;CCLCRC;;;IU)", StringComparison.Ordinal), Defines["ServiceSddlUpdating"]);
        Assert.Single(Regex.Matches(Iss, Regex.Escape(Defines["ServiceSddlUpdating"])));
        // Abort or rollback: both come back.
        var restore = code[code.IndexOf("procedure RestoreService", StringComparison.Ordinal)..];
        restore = restore[..restore.IndexOf("end;", StringComparison.Ordinal)];
        Assert.Contains("'failure {#ServiceName} reset= {#ServiceFailureReset} actions= {#ServiceFailureActions}'", restore);
        Assert.Contains("'sdset {#ServiceName} {#ServiceSddl}'", restore);
        Assert.Contains("if ServiceTouched and not ServiceAttempted then RestoreService;", code);
    }

    [Fact]
    public void Leftovers_of_the_service_are_ended_only_when_they_are_still_the_listed_process()
    {
        var stop = File.ReadAllText(Path.Combine(Root, "installer", "stop-couchhud.ps1"));
        Assert.Contains("[switch]$ServiceLeftovers", stop);
        Assert.Contains("$_.ProcessId -notin $scm", stop);
        Assert.Contains("GetProcessTimes(h, out c, out e, out k, out u) || System.Math.Abs(c - created) > 10000", stop);
        Assert.Contains("QueryFullProcessImageNameW(h, 0, name, ref size)", stop);
        // The leftovers mode ends before the state file of the shells is written.
        Assert.True(stop.IndexOf("if ($ServiceLeftovers)", StringComparison.Ordinal) < stop.IndexOf("Set-Content -LiteralPath $StateFile", StringComparison.Ordinal));
    }

    [Fact]
    public void A_service_that_is_not_hardened_completely_does_not_stay_registered()
    {
        var code = Code;
        var configure = code[code.IndexOf("function ConfigureService", StringComparison.Ordinal)..];
        configure = configure[..configure.IndexOf("procedure RestoreService", StringComparison.Ordinal)];
        // Every early exit before the start is preceded by FailClosed.
        var beforeStart = configure[..configure.IndexOf("RunSc('start", StringComparison.Ordinal)];
        Assert.Equal(Regex.Matches(beforeStart, @"\bExit;").Count, Regex.Matches(beforeStart, @"FailClosed\((Created|False)\);\s*Exit;").Count);
        var failClosed = code[code.IndexOf("procedure FailClosed", StringComparison.Ordinal)..code.IndexOf("function ConfigureService", StringComparison.Ordinal)];
        Assert.Contains("RunSc('delete {#ServiceName}')", failClosed);
        Assert.Contains("RunSc('config {#ServiceName} start= disabled')", failClosed);
    }

    [Fact]
    public void The_setup_clears_profiler_and_startup_hook_variables_before_it_starts_anything()
    {
        var code = Code;
        foreach (var name in new[] { "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER_PATH", "DOTNET_STARTUP_HOOKS", "COMPlus_InstallRoot", "PSModulePath" })
        {
            Assert.Contains(name + ",", code);
        }
        var setup = code[code.IndexOf("function InitializeSetup", StringComparison.Ordinal)..];
        Assert.StartsWith("function InitializeSetup: Boolean;\nbegin\n  CleanEnvironment;", setup.Replace("\r\n", "\n"));
        var uninstall = code[code.IndexOf("function InitializeUninstall", StringComparison.Ordinal)..].Replace("\r\n", "\n");
        Assert.Contains("begin\n  CleanEnvironment;", uninstall[..200]);
        Assert.Contains("'-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"'", code);
        Assert.Single(Regex.Matches(code, "powershell.exe"));
        // The app path must not carry anything cmd.exe would interpret.
        Assert.Contains("'&|^%!<>\"'", code);
    }

    [Fact]
    public void Every_folder_of_the_ui_goes_with_the_uninstall_when_empty_deepest_first()
    {
        // Finding B3 of the 0.4.1 acceptance test: ui\i18n\de and en stayed behind.
        var entries = Regex.Matches(Iss, @"^Type: dirifempty; Name: ""\{app\}\\?([^""]*)""", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        var ui = Path.Combine(Root, "ui");
        var folders = Directory.GetDirectories(ui, "*", SearchOption.AllDirectories)
            .Select(d => "ui\\" + Path.GetRelativePath(ui, d).Replace('/', '\\'))
            .Append("ui")
            .ToList();
        foreach (var folder in folders)
        {
            var at = entries.IndexOf(folder);
            Assert.True(at >= 0, $"no dirifempty entry for {folder}");
            // A child before its parent: dirifempty only removes empty folders.
            foreach (var child in folders.Where(f => f.StartsWith(folder + "\\", StringComparison.Ordinal))) Assert.True(entries.IndexOf(child) < at, $"{child} after {folder}");
        }
        Assert.Equal("", entries[^1]);
        // Only the exact old program files an update deletes (allowlist, checked above) may name files under ui.
        var rest = string.Join('\n', Iss.Split('\n').Where(l => !AllowedInstallDeletes.Contains(l.Trim())));
        Assert.DoesNotMatch(new Regex(@"^Type: (filesandordirs|files); Name: ""\{app\}\\ui", RegexOptions.Multiline), rest);
    }

    [Fact]
    public void The_setup_restricts_the_old_tasks_before_the_new_service_is_registered()
    {
        // Security review M-1: no moment after the update in which a user may still start an old task, also with a squatted pipe name.
        var configure = Code[Code.IndexOf("function ConfigureService", StringComparison.Ordinal)..];
        configure = configure[..configure.IndexOf("\nend;", StringComparison.Ordinal)];
        var harden = configure.IndexOf("HardenServiceFolder", StringComparison.Ordinal);
        var restrict = configure.IndexOf("RestrictLegacyTasks;", StringComparison.Ordinal);
        var create = configure.IndexOf("RunSc('create", StringComparison.Ordinal);
        Assert.True(harden >= 0 && harden < restrict && restrict < create);
        var procedure = Code[Code.IndexOf("procedure RestrictLegacyTasks;", StringComparison.Ordinal)..];
        procedure = procedure[..procedure.IndexOf("\nend;", StringComparison.Ordinal)];
        Assert.Contains("ServiceFolderPath + '\\{#ServiceExe}'", procedure);
        Assert.Contains("'--restrict-legacy-tasks'", procedure);
        Assert.Equal("--restrict-legacy-tasks", typeof(CouchHud.Service.ServiceRuntime).Assembly.GetType("CouchHud.Service.Program")!
            .GetField("RestrictLegacyTasksSwitch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetRawConstantValue());
    }

    [Fact]
    public void The_data_folder_is_inside_the_service_folder_and_kept_by_updates()
    {
        Assert.Equal(ServiceInstall.DataFolderName, Defines["ServiceDataFolder"]);
        Assert.Contains(@"Type: filesandordirs; Name: ""{app}\{#ServiceFolder}\{#ServiceDataFolder}""", Iss);
        Assert.DoesNotContain(InstallDeleteEntries(), e => e.Contains(@"{#ServiceFolder}", StringComparison.Ordinal));
        var harden = Code[Code.IndexOf("function HardenServiceFolder", StringComparison.Ordinal)..];
        harden = harden[..harden.IndexOf("end;", StringComparison.Ordinal)];
        var acl = harden.IndexOf("FolderAclParams", StringComparison.Ordinal);
        var create = harden.IndexOf("ForceDirectories(ServiceDataPath)", StringComparison.Ordinal);
        var dataAcl = harden.IndexOf("DataAclParams", StringComparison.Ordinal);
        var owner = harden.IndexOf("FolderOwnerParams", StringComparison.Ordinal);
        Assert.True(acl >= 0 && acl < create && create < dataAcl && dataAcl < owner);
        Assert.Contains(@"/LOG=""<app folder>\service\data\logs\setup-<version>.log""", Iss);
    }

    [Fact]
    public void The_data_folder_is_system_and_administrators_only()
    {
        // Inheritance broken at data: the Users read of the service folder does not reach the state, the code hashes or a staged setup.
        Assert.Equal("*S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F", Defines["ServiceDataGrants"]);
        Assert.Contains("ServiceDataPath + '\" /inheritance:r /grant:r {#ServiceDataGrants}'", Code);
        Assert.DoesNotContain("S-1-5-32-545", Defines["ServiceDataGrants"]);
        // The repair script sets the same list (after making the folder, before the owner).
        var repair = Code[Code.IndexOf("procedure WriteRepairScript", StringComparison.Ordinal)..];
        var mkdir = repair.IndexOf("mkdir", StringComparison.Ordinal);
        var dataAcl = repair.IndexOf("Icacls + ' ' + DataAclParams", StringComparison.Ordinal);
        var owner = repair.IndexOf("Icacls + ' ' + FolderOwnerParams", StringComparison.Ordinal);
        Assert.True(mkdir >= 0 && mkdir < dataAcl && dataAcl < owner);
        // The service creates it with the same list when the setup has not (ProtectedFolderRules.DataFolderSddl).
        Assert.Equal("O:SYG:SYD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)", ProtectedFolderRules.DataFolderSddl);
        // The CI smoke test checks it on a real install.
        var smoke = File.ReadAllText(Path.Combine(Root, "scripts", "ci-service-smoke.ps1"));
        Assert.Contains("data folder does not inherit", smoke);
        Assert.Contains("data folder is SYSTEM and Administrators only", smoke);
    }

    [Fact]
    public void An_administrators_approval_goes_through_uac_and_sc_control_only()
    {
        // Review M-c: no credential prompt of CouchHUD's own; the shell starts sc.exe control elevated, Windows' consent.exe asks.
        var admin = File.ReadAllText(Path.Combine(Root, "src", "CouchHud.Shell", "Services", "AdminService.cs"));
        Assert.Contains("EmergencyRepair.AdminApproval(Environment.SystemDirectory", admin);
        Assert.False(File.Exists(Path.Combine(Root, "src", "CouchHud.Shell", "Services", "CredentialPrompt.cs")));
        Assert.DoesNotContain("CredUIPromptForWindowsCredentials", string.Concat(Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")).Select(File.ReadAllText)));
        // Only administrators (and SYSTEM) may send a user-defined control (CR); interactive users may not, also not while an update runs.
        var dacl = ServiceHardening.ServiceSddl;
        Assert.Contains("CR", Rights(dacl, "BA"));
        Assert.DoesNotContain("CR", Rights(dacl, "IU"));
        Assert.DoesNotContain("CR", Rights(Defines["ServiceSddlUpdating"], "IU"));
        foreach (var everyone in new[] { "WD", "AU", "BU" }) Assert.Empty(Rights(dacl, everyone));
    }

    /// <summary>The two-letter rights an allow entry of the SDDL gives <paramref name="trustee"/> (e.g. CC, LC, RP, CR).</summary>
    private static List<string> Rights(string sddl, string trustee)
    {
        var match = Regex.Match(sddl, @"\(A;;([A-Z]*);;;" + trustee + @"\)");
        if (!match.Success) return [];
        var letters = match.Groups[1].Value;
        return [.. Enumerable.Range(0, letters.Length / 2).Select(i => letters.Substring(i * 2, 2))];
    }

    [Fact]
    public void The_uninstall_removes_the_service()
    {
        Assert.Contains("RemoveService;", Code);
        Assert.Contains(@"Type: files; Name: ""{app}\{#RepairScript}""", Iss);
        Assert.Contains("Flags: uninsdeletekey", Iss);
        var cleanup = File.ReadAllText(Path.Combine(Root, "src", "CouchHud.Shell", "Services", "UninstallCleanup.cs"));
        Assert.True(cleanup.IndexOf("Step(\"service\"", StringComparison.Ordinal) < cleanup.IndexOf("Step(\"tasks\"", StringComparison.Ordinal));
        // ReleaseAllAccounts through the pipe first, while the service still runs.
        Assert.True(cleanup.IndexOf("Step(\"service release\"", StringComparison.Ordinal) is > 0 and var release
            && release < cleanup.IndexOf("Step(\"service\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Ci_publishes_the_service_into_the_setup_and_smoke_tests_it()
    {
        var build = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "build.yml"));
        Assert.Contains("dotnet publish src/CouchHud.Service/CouchHud.Service.csproj -c Release -r win-x64 --self-contained true", build);
        Assert.Contains("-o publish/service", build);
        var project = File.ReadAllText(Path.Combine(Root, "src", "CouchHud.Service", "CouchHud.Service.csproj"));
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", project);
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>false</IncludeNativeLibrariesForSelfExtract>", project);
        Assert.Contains("<IncludeAllContentForSelfExtract>false</IncludeAllContentForSelfExtract>", project);
        Assert.Contains(@"Source: ""{#PublishDir}\{#ServiceFolder}\*""; DestDir: ""{app}\{#ServiceFolder}""", Iss);
        Assert.Contains("scripts/ci-service-smoke.ps1", build);
        var smoke = File.ReadAllText(Path.Combine(Root, "scripts", "ci-service-smoke.ps1"));
        Assert.Contains(Protocol.PipeName, smoke);
    }

    [Fact]
    public void Every_action_is_pinned_to_a_commit()
    {
        var build = File.ReadAllText(Path.Combine(Root, ".github", "workflows", "build.yml"));
        var uses = Regex.Matches(build, @"uses:\s*(\S+)").Select(m => m.Groups[1].Value).ToList();
        Assert.NotEmpty(uses);
        Assert.All(uses, u => Assert.Matches(@"^[\w.-]+/[\w.-]+@[0-9a-f]{40}$", u));
    }
}
