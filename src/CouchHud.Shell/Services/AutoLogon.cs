using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using CouchHud.Core.Guard;
using CouchHud.Core.Security;
using CouchHud.Core.Localization;
using CouchHud.Service.Contracts;
using CouchHud.ServiceClient;

namespace CouchHud.Shell.Services;

/// <summary>
/// Windows automatic sign-in, set up like Sysinternals Autologon: the password is typed once
/// in CouchHud, checked with LogonUser and then stored by Windows as the LSA secret
/// "DefaultPassword" (encrypted, readable only by the system), by the CouchHUD service. CouchHud itself never keeps
/// the password: not in config.json, not in the log, not on a command line. It goes in one request over the service's pipe,
/// and only after the client checked that the pipe's server is the service's own process (a squatter gets nothing).
/// While it is set up the password does live briefly in memory as a .NET string (UI message,
/// LogonUser); the request bytes are wiped after sending.
/// </summary>
public static class AutoLogon
{
    public static (bool Enabled, string User) Status()
    {
        var (enabled, user, _) = Read();
        return (enabled, user);
    }

    private static (bool Enabled, string User, string Domain) Read() => AutoLogonSetup.Read();

    /// <summary>
    /// Machine-wide note that CouchHud set the automatic sign-in up (the config flag lives in
    /// one user's profile; the uninstaller may run as someone else).
    /// </summary>
    public static bool SetUpByCouchHud() => AutoLogonSetup.SetUpByCouchHud();

    public static LogonAccount CurrentAccount()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return AutoLogonAccount.Parse(identity.Name, Environment.MachineName);
    }

    /// <summary>Whether and for whom automatic sign-in is on, and whether it is the signed-in user.</summary>
    public static AutoLogonStatus Evaluate(bool configFlag)
    {
        var (enabled, user, domain) = Read();
        return AutoLogonAccount.Evaluate(enabled, user, domain, configFlag || SetUpByCouchHud(), CurrentAccount(), CurrentUpn(), Environment.MachineName);
    }

    /// <summary>
    /// "Only allow Windows Hello sign-in for Microsoft accounts" makes Windows ignore a stored
    /// password for automatic sign-in.
    /// </summary>
    public static bool PasswordlessOnly() => AutoLogonSetup.PasswordlessOnly();

    /// <summary>
    /// The recovery's way to switch the automatic sign-in off without a UAC prompt: the service first (it switches off only an
    /// automatic sign-in of this account), then the reserve task of earlier versions (owner decision E5: kept for the recovery, it
    /// only works in the safe direction). Waits until Windows shows it as off; false when neither did it in time.
    /// </summary>
    public static bool TurnOffWithoutPrompt()
    {
        try
        {
            var result = AdminService.Ops.AutoLogonOffAsync().GetAwaiter().GetResult();
            if (result.Ok && WaitOff()) return true;
            Console.Error.WriteLine($"[autologon] service did not switch it off: {result}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[autologon] service: {ex.GetType().Name}");
        }
        if (!PolicyHelper.AutoLogonOffInstalled()) return false;
        return PrivilegedServices.Scheduler.Run(PolicyHelper.AutoLogonOffTask()) == 0 && WaitOff();
    }

    private static bool WaitOff()
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < until)
        {
            if (!Read().Enabled) return true;
            Thread.Sleep(100);
        }
        return !Read().Enabled;
    }

    /// <summary>Expert fallback: Windows' own dialog; untick "Users must enter a user name and password".</summary>
    public static void OpenDialog()
    {
        // netplwiz asks for administrator rights itself.
        Process.Start(new ProcessStartInfo("netplwiz.exe") { UseShellExecute = true })?.Dispose();
    }

    // ---- check the password (not elevated) ------------------------------------------------

    /// <summary>
    /// Checks the password with LogonUser. Returns the account spelling Windows accepted
    /// (it is stored for the sign-in), or a German message for the user.
    /// </summary>
    public static (LogonAccount? Account, string Error) Verify(string password)
    {
        if (!AutoLogonAccount.IsUsablePassword(password)) return (null, Strings.Get("autologon.needPassword"));
        var candidates = AutoLogonAccount.Candidates(CurrentAccount(), CurrentUpn());
        if (candidates.Count == 0) return (null, Strings.Get("autologon.noAccount"));
        // Wrong passwords count towards the account lockout: pause after a few.
        var wait = Attempts.Wait;
        if (wait > TimeSpan.Zero) return (null, Strings.Get("autologon.wait", Math.Ceiling(wait.TotalSeconds)));
        var code = 0;
        // The UPN is only a second candidate (Microsoft/work accounts have one, local accounts do not).
        foreach (var candidate in candidates)
        {
            code = PrivilegedServices.Logon.Verify(candidate.User, candidate.Domain.Length == 0 ? null : candidate.Domain, password).Error;
            if (code == 0)
            {
                Attempts.Succeeded();
                // Winlogon gets the form Windows reports for the signed-in user (local alias and PC name,
                // like Sysinternals Autologon), even if the e-mail address was what verified.
                return (CurrentAccount(), "");
            }
            // Another spelling only helps when Windows did not know the name/password pair.
            if (code != LogonErrors.LogonFailure) break;
        }
        if (code == LogonErrors.LogonFailure) Attempts.Failed();
        return (null, ExplainLogonError(code));
    }

    private static readonly PasswordAttemptLimiter Attempts = new(() => DateTimeOffset.UtcNow);

    private static string ExplainLogonError(int code) => code switch
    {
        LogonErrors.LogonFailure => Strings.Get("autologon.wrongPassword"),
        LogonErrors.LogonTypeNotGranted => Strings.Get("autologon.notGranted"),
        1327 => Strings.Get("autologon.blankRestricted"),
        1328 => Strings.Get("autologon.timeRestricted"),
        1330 or 1907 => Strings.Get("autologon.expired"),
        1331 => Strings.Get("autologon.disabled"),
        1909 => Strings.Get("autologon.locked"),
        _ => Strings.Get("autologon.failed", code)
    };

    private static string? CurrentUpn()
    {
        var size = 256u;
        var text = new StringBuilder((int)size);
        return NativeLogon.GetUserNameEx(NativeLogon.NameUserPrincipal, text, ref size) ? text.ToString() : null;
    }

    // ---- through the service ----------------------------------------------------------------

    /// <summary>
    /// Switches the automatic sign-in on for the signed-in account: the service checks the password itself (the logon's account must be
    /// the caller's), stores it as the LSA secret and sets Winlogon. With "Windows Hello only" on, the service also allows the password
    /// sign-in again (Windows would ignore the stored password otherwise). Throws with the reason when it did not work.
    /// </summary>
    public static async Task EnableAsync(string password)
    {
        // A machine-wide setting: the service wants a parent's approval (the parents' code, entered just before) or an administrator's;
        // without one Windows' own administrator prompt asks for it, and the request goes once more.
        var allow = PasswordlessOnly();
        var result = await AdminService.WithGrantAsync(Operation.SetAutoLogon, () => AdminService.Ops.SetAutoLogonAsync(password, allowPasswordSignIn: allow));
        if (result.Failure is FailureKind.Refused or FailureKind.NeedGrant) throw new InvalidOperationException(Strings.Get("autologon.serviceDenied"));
        AdminService.ThrowIfFailed(result);
    }

    /// <summary>Ends the automatic sign-in of this account through the service.</summary>
    public static async Task TurnOffAsync(bool configFlag)
    {
        var before = Evaluate(configFlag).State;
        var result = await AdminService.Ops.AutoLogonOffAsync();
        // Off already with nothing of CouchHUD's stored, and the service has nothing of this account to switch off: done, not refused
        // (Windows is asked again after the refusal).
        if (result.Failure == FailureKind.Refused && AutoLogonAccount.AlreadyOff(before, refusedByService: true, enabledAfter: Read().Enabled)) return;
        AdminService.ThrowIfFailed(result);
        if (!await Task.Run(WaitOff)) throw new InvalidOperationException(Strings.Get("autologon.windowsSlow"));
    }

    /// <summary>Uninstall: ends the automatic sign-in only if CouchHud set it up. Runs elevated already.</summary>
    public static void RemoveIfSetUpByCouchHud(bool configFlag)
    {
        if (!configFlag && !SetUpByCouchHud()) return;
        if (PrivilegedServices.AutoLogon.TurnOff() != 0) throw new InvalidOperationException(Strings.Get("autologon.removeFailed"));
    }

    // ---- Windows APIs ---------------------------------------------------------------------

    private static class NativeLogon
    {
        public const int NameUserPrincipal = 8;

        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetUserNameEx(int format, StringBuilder name, ref uint size);
    }
}
