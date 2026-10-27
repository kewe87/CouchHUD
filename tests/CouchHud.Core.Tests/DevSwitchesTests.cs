using CouchHud.Core.Security;

namespace CouchHud.Core.Tests;

public class DevSwitchesTests
{
    // --- WEBVIEW2_* scrubbing ------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_webview2_variable_goes_whatever_its_case_and_the_rest_stays()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = "--remote-debugging-port=9222",
            ["webview2_browser_executable_folder"] = @"C:\Users\kid\fake",
            ["WebView2_User_Data_Folder"] = @"C:\Users\kid\data",
            ["WEBVIEW2_RELEASE_CHANNEL_PREFERENCE"] = "1",
            ["WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER"] = "x",
            ["WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER"] = "1",
            ["WEBVIEW2_SOME_FUTURE_OVERRIDE"] = "y",
            ["PATH"] = @"C:\Windows",
            ["MY_WEBVIEW2_NOTE"] = "stays",
            ["COREWEBVIEW2_MAX_INSTANCES"] = "5"
        };

        var removed = WebViewEnvironment.Scrub(env);

        Assert.Equal(7, removed.Count);
        Assert.DoesNotContain(env.Keys, k => k.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["COREWEBVIEW2_MAX_INSTANCES", "MY_WEBVIEW2_NOTE", "PATH"], env.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Removed_names_are_reported_without_values()
    {
        var env = new Dictionary<string, string?> { ["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = "--remote-debugging-port=9222" };
        var removed = WebViewEnvironment.Scrub(env);
        Assert.Equal(["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"], removed);
        Assert.DoesNotContain(removed, n => n.Contains("9222"));
    }

    [Fact]
    public void Nothing_to_remove_leaves_the_environment_alone()
    {
        var env = new Dictionary<string, string?> { ["PATH"] = "x", ["TEMP"] = "y" };
        Assert.Empty(WebViewEnvironment.Scrub(env));
        Assert.Equal(2, env.Count);
    }

    [Fact]
    public void The_process_environment_is_cleaned()
    {
        var name = "WEBVIEW2_COUCHHUD_TEST_" + Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable(name, "--remote-debugging-port=9222");
        try
        {
            var removed = WebViewEnvironment.ScrubProcess();
            Assert.Contains(name, removed, StringComparer.OrdinalIgnoreCase);
            Assert.Null(Environment.GetEnvironmentVariable(name));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    // --- dev mode decision ---------------------------------------------------------------------------------------------------

    private static readonly MachineDevSwitch AdminOn = new(AdminOnly: true, DeveloperMode: 1, RemoteDebuggingPort: null);

    [Fact]
    public void Release_ignores_dev_without_the_machine_switch()
    {
        var d = DevMode.Decide(devArgument: true, debugBuild: false, MachineDevSwitch.None);
        Assert.False(d.Dev);
        Assert.Null(d.RemoteDebuggingPort);
        Assert.Contains("ignored", d.Reason);
    }

    [Fact]
    public void Debug_build_honours_dev()
    {
        Assert.True(DevMode.Decide(true, debugBuild: true, MachineDevSwitch.None).Dev);
    }

    [Fact]
    public void Release_honours_dev_with_the_admin_only_machine_switch()
    {
        Assert.True(DevMode.Decide(true, debugBuild: false, AdminOn).Dev);
    }

    [Fact]
    public void A_machine_switch_others_can_write_counts_as_off()
    {
        var d = DevMode.Decide(true, false, AdminOn with { AdminOnly = false, RemoteDebuggingPort = 9222 });
        Assert.False(d.Dev);
        Assert.Null(d.RemoteDebuggingPort);
        Assert.Contains("writable", d.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    public void Only_developer_mode_one_switches_on(int value)
    {
        Assert.False(DevMode.Decide(true, false, AdminOn with { DeveloperMode = value }).Dev);
    }

    [Fact]
    public void No_dev_argument_means_no_dev_even_with_the_switch()
    {
        Assert.False(DevMode.Decide(false, true, AdminOn).Dev);
        Assert.False(DevMode.Decide(false, false, AdminOn).Dev);
    }

    [Theory]
    [InlineData(9222, 9222)]
    [InlineData(1024, 1024)]
    [InlineData(65535, 65535)]
    [InlineData(1023, null)]
    [InlineData(65536, null)]
    [InlineData(0, null)]
    public void Remote_debugging_port_needs_the_switch_and_a_sane_port(int value, int? expected)
    {
        var d = DevMode.Decide(false, false, AdminOn with { RemoteDebuggingPort = value });
        Assert.Equal(expected, d.RemoteDebuggingPort);
    }

    [Fact]
    public void Remote_debugging_port_without_developer_mode_is_ignored_even_in_debug()
    {
        Assert.Null(DevMode.Decide(true, true, AdminOn with { DeveloperMode = null, RemoteDebuggingPort = 9222 }).RemoteDebuggingPort);
    }

    // --- ACL of HKLM\SOFTWARE\CouchHud ---------------------------------------------------------------------------------------

    private const string Users = "S-1-5-32-545";
    private const string SomeUser = "S-1-5-21-1-2-3-1001";
    private const string CreatorOwner = "S-1-3-0";
    private const int ReadKey = 0x20019;
    private const int FullControl = 0xF003F;
    private const int SetValue = 0x2;

    private static readonly KeyAccessRule[] DefaultSoftwareAcl =
    [
        new(DevMode.SystemSid, true, FullControl, false),
        new(DevMode.AdministratorsSid, true, FullControl, false),
        new(Users, true, ReadKey, false),
        new(CreatorOwner, true, FullControl, inheritOnly: true)
    ];

    [Fact]
    public void Default_hklm_software_acl_is_admin_only()
    {
        Assert.True(DevMode.IsAdminOnly(DevMode.AdministratorsSid, DefaultSoftwareAcl));
        Assert.True(DevMode.IsAdminOnly(DevMode.SystemSid, DefaultSoftwareAcl));
    }

    [Fact]
    public void A_user_owner_is_not_admin_only()
    {
        Assert.False(DevMode.IsAdminOnly(SomeUser, DefaultSoftwareAcl));
        Assert.False(DevMode.IsAdminOnly(null, DefaultSoftwareAcl));
    }

    [Theory]
    [InlineData(SetValue)]
    [InlineData(0x4)]          // create subkey
    [InlineData(0x40000)]      // write DAC
    [InlineData(0x80000)]      // write owner
    [InlineData(0x10000000)]   // generic all
    [InlineData(0x40000000)]   // generic write
    public void Write_rights_for_users_are_not_admin_only(int rights)
    {
        Assert.False(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new(Users, true, rights, false)]));
        Assert.False(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new(SomeUser, true, rights, false)]));
    }

    [Fact]
    public void Deny_and_inherit_only_rules_do_not_open_the_key()
    {
        Assert.True(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new(Users, false, FullControl, false), new(Users, true, FullControl, true)]));
    }

    // --- browser arguments ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Without_port_only_the_base_arguments()
    {
        Assert.Equal(BrowserArguments.HudBase, BrowserArguments.Build(BrowserArguments.HudBase, null, BrowserSurface.Hud));
        Assert.Equal("", BrowserArguments.Build(null, null, BrowserSurface.BrowserPane));
    }

    [Fact]
    public void Hud_gets_the_port_the_pane_the_next_one()
    {
        Assert.Equal($"{BrowserArguments.HudBase} --remote-debugging-port=9222", BrowserArguments.Build(BrowserArguments.HudBase, 9222, BrowserSurface.Hud));
        Assert.Equal("--remote-debugging-port=9223", BrowserArguments.Build(null, 9222, BrowserSurface.BrowserPane));
        Assert.Empty(BrowserArguments.Extra(65535, BrowserSurface.BrowserPane));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(70000)]
    public void Odd_ports_add_nothing(int port)
    {
        Assert.Empty(BrowserArguments.Extra(port, BrowserSurface.Hud));
    }

    [Fact]
    public void Remote_debugging_never_leaves_loopback()
    {
        foreach (var surface in new[] { BrowserSurface.Hud, BrowserSurface.BrowserPane })
            Assert.DoesNotContain("--remote-debugging-address", BrowserArguments.Build(BrowserArguments.HudBase, 9222, surface));
        Assert.Throws<ArgumentException>(() => BrowserArguments.Build("--remote-debugging-address=0.0.0.0", 9222, BrowserSurface.Hud));
    }

    // --- runtime location ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Runtime_under_a_protected_root_counts_others_do_not()
    {
        var programs = Path.Combine(Path.GetTempPath(), "pf-" + Guid.NewGuid().ToString("N"));
        var exe = Path.Combine(programs, "Microsoft", "EdgeWebView", "Application", "130.0", "msedgewebview2.exe");
        Assert.True(BrowserArguments.InProtectedFolder(exe, [null, programs]));
        Assert.False(BrowserArguments.InProtectedFolder(Path.Combine(Path.GetTempPath(), "user", "msedgewebview2.exe"), [programs]));
        // A sibling with the same prefix is not inside.
        Assert.False(BrowserArguments.InProtectedFolder(programs + "-evil" + Path.DirectorySeparatorChar + "msedgewebview2.exe", [programs]));
        // Climbing out with .. does not count.
        Assert.False(BrowserArguments.InProtectedFolder(Path.Combine(programs, "..", "x", "msedgewebview2.exe"), [programs]));
        Assert.False(BrowserArguments.InProtectedFolder(null, [programs]));
        // Any other program below the root is not the runtime.
        Assert.False(BrowserArguments.InProtectedFolder(Path.Combine(programs, "Tools", "msedgewebview2.exe"), [programs]));
        Assert.False(BrowserArguments.InProtectedFolder(Path.Combine(programs, "Microsoft", "EdgeWebView", "Application", "130.0", "evil.exe"), [programs]));
    }

    // --- N1: scrub rounds and known names ------------------------------------------------------------------------------------

    [Fact]
    public void Scrub_removes_known_names_and_repeats_until_nothing_is_left()
    {
        var env = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WEBVIEW2_USER_DATA_FOLDER", "Path" };
        var hidden = "WEBVIEW2_RELEASE_CHANNELS"; // not enumerated at first, but known by name
        env.Add(hidden);
        env.Add("webview2_odd_one");
        // Removing the odd one makes another one appear (as if something put one back meanwhile): a second round is needed.
        void Remove(string name)
        {
            env.Remove(name);
            if (name.Equals("webview2_odd_one", StringComparison.OrdinalIgnoreCase)) env.Add("WEBVIEW2_LATE_ONE");
        }

        var removed = WebViewEnvironment.ScrubProcess(() => env.Where(n => n != hidden).ToList(), Remove, env.Contains);

        Assert.Contains("WEBVIEW2_RELEASE_CHANNELS", removed);
        Assert.Contains("WEBVIEW2_USER_DATA_FOLDER", removed);
        Assert.Contains("webview2_odd_one", removed, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("WEBVIEW2_LATE_ONE", removed);
        Assert.Equal(["Path"], env);
    }

    [Fact]
    public void Scrub_gives_up_after_eight_rounds()
    {
        var calls = 0;
        var removed = WebViewEnvironment.ScrubProcess(() => { calls++; return ["WEBVIEW2_STUCK"]; }, _ => { }, _ => false);
        Assert.Equal(8, calls);
        Assert.Equal(["WEBVIEW2_STUCK"], removed);
    }

    [Fact]
    public void Known_names_cover_the_documented_variables()
    {
        foreach (var name in new[] { "ADDITIONAL_BROWSER_ARGUMENTS", "BROWSER_EXECUTABLE_FOLDER", "USER_DATA_FOLDER", "RELEASE_CHANNEL_PREFERENCE",
                     "RELEASE_CHANNELS", "CHANNEL_SEARCH_KIND", "PIPE_FOR_SCRIPT_DEBUGGER", "WAIT_FOR_SCRIPT_DEBUGGER" })
            Assert.Contains("WEBVIEW2_" + name, WebViewEnvironment.Known);
    }

    // --- N2/N5: more ACL cases -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("S-1-1-0")]   // Everyone
    [InlineData("S-1-5-11")]  // Authenticated Users
    [InlineData("S-1-5-4")]   // INTERACTIVE
    public void Broad_groups_with_write_rights_are_not_admin_only(string sid)
    {
        Assert.False(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new(sid, true, SetValue, false)]));
        Assert.True(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new(sid, true, ReadKey, false)]));
    }

    [Theory]
    [InlineData(0x20)]        // KEY_CREATE_LINK
    [InlineData(0x10000)]     // DELETE
    [InlineData(0x02000000)]  // MAXIMUM_ALLOWED
    public void Link_delete_and_maximum_allowed_count_as_writing(int rights)
    {
        Assert.False(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new(Users, true, rights, false)]));
    }

    [Fact]
    public void A_null_dacl_is_never_admin_only()
    {
        Assert.False(DevMode.IsAdminOnly(DevMode.AdministratorsSid, null));
    }

    [Fact]
    public void An_entry_the_check_does_not_understand_counts_as_granting()
    {
        // Even read-only rights: a callback or object entry may grant under conditions the check cannot see.
        Assert.False(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new KeyAccessRule(Users, AceKind.Unknown, ReadKey, false)]));
        Assert.False(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new KeyAccessRule("", AceKind.Unknown, -1, false)]));
        // For an administrator, or only for subkeys, it changes nothing.
        Assert.True(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new KeyAccessRule(DevMode.SystemSid, AceKind.Unknown, FullControl, false)]));
        Assert.True(DevMode.IsAdminOnly(DevMode.AdministratorsSid, [.. DefaultSoftwareAcl, new KeyAccessRule(Users, AceKind.Unknown, FullControl, true)]));
    }

    [Fact]
    public void Only_a_dword_counts()
    {
        Assert.Equal(1, DevMode.Dword(1, Microsoft.Win32.RegistryValueKind.DWord));
        Assert.Null(DevMode.Dword("1", Microsoft.Win32.RegistryValueKind.String));
        Assert.Null(DevMode.Dword("1", Microsoft.Win32.RegistryValueKind.ExpandString));
        Assert.Null(DevMode.Dword(1L, Microsoft.Win32.RegistryValueKind.QWord));
        Assert.Null(DevMode.Dword(new byte[] { 1, 0, 0, 0 }, Microsoft.Win32.RegistryValueKind.Binary));
        Assert.Null(DevMode.Dword(null, Microsoft.Win32.RegistryValueKind.DWord));
    }

    // --- M1: machine-wide runtime folder -------------------------------------------------------------------------------------

    private const string Pf86 = @"C:\Program Files (x86)";
    private const string Location = @"C:\Program Files (x86)\Microsoft\EdgeWebView\Application";

    private static RuntimeFolderChoice Choose(IEnumerable<RuntimeRegistration> registrations, Func<string, bool>? exists = null,
        Func<string, string>? canonical = null, Func<string, bool>? reparse = null) =>
        WebViewRuntime.Choose(registrations, [@"C:\Program Files", Pf86], exists ?? (_ => true), canonical ?? (p => p), reparse ?? (_ => false));

    [Fact]
    public void A_valid_machine_runtime_is_chosen()
    {
        var choice = Choose([new(Location, "130.0.2849.80")]);
        Assert.Equal(Location + @"\130.0.2849.80", choice.Folder);
    }

    [Fact]
    public void The_highest_valid_registration_wins()
    {
        var choice = Choose([new(Location, "129.0.1.2"), new(Location, "130.0.2849.80"), new(@"C:\Users\kid\AppData\Local\Microsoft\EdgeWebView\Application", "999.0.0.0")]);
        Assert.Equal(Location + @"\130.0.2849.80", choice.Folder);
    }

    [Theory]
    [InlineData(@"C:\Users\kid\AppData\Local\Microsoft\EdgeWebView\Application")]
    [InlineData(@"C:\Windows\Temp\Microsoft\EdgeWebView\Application")]
    [InlineData(@"\\server\share\Microsoft\EdgeWebView\Application")]
    [InlineData(@"C:\Program Files (x86)\Evil\Application")]
    [InlineData(@"C:\Program Files (x86)\..\Users\kid\Microsoft\EdgeWebView\Application")]
    public void Outside_program_files_is_not_chosen(string location)
    {
        Assert.Null(Choose([new(location, "130.0.2849.80")]).Folder);
    }

    [Fact]
    public void A_link_that_resolves_outside_program_files_is_not_chosen()
    {
        var choice = Choose([new(Location, "130.0.2849.80")], canonical: _ => @"C:\Users\kid\fake\Microsoft\EdgeWebView\Application\130.0.2849.80\msedgewebview2.exe");
        Assert.Null(choice.Folder);
        Assert.Contains("Program Files", choice.Reason);
    }

    [Fact]
    public void A_reparse_point_on_the_way_is_not_chosen()
    {
        var choice = Choose([new(Location, "130.0.2849.80")], reparse: _ => true);
        Assert.Null(choice.Folder);
        Assert.Contains("reparse", choice.Reason);
    }

    [Fact]
    public void A_missing_or_unregistered_runtime_leaves_it_to_the_loader()
    {
        Assert.Null(Choose([]).Folder);
        Assert.Null(Choose([new(null, null)]).Folder);
        var missing = Choose([new(Location, "130.0.2849.80")], exists: _ => false);
        Assert.Null(missing.Folder);
        Assert.Contains("missing", missing.Reason);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("130.0")]
    [InlineData(@"..\..\x")]
    [InlineData("130.0.1.2\\..")]
    [InlineData("abc")]
    public void Odd_versions_are_not_chosen(string pv)
    {
        Assert.Null(Choose([new(Location, pv)]).Folder);
    }

    [Fact]
    public void An_unreadable_path_is_not_chosen()
    {
        Assert.Null(Choose([new(Location, "130.0.2849.80")], canonical: _ => throw new UnauthorizedAccessException()).Folder);
    }
}
