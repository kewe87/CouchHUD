namespace CouchHud.Core.Security;

/// <summary>A Windows account as the sign-in APIs want it: user name and domain (the PC name for local accounts).</summary>
public readonly record struct LogonAccount(string User, string Domain);

public enum AutoLogonState
{
    /// <summary>Windows asks for the password at startup.</summary>
    Off,
    /// <summary>On, and set up by CouchHud.</summary>
    CouchHud,
    /// <summary>On, set up some other way (netplwiz, Sysinternals Autologon).</summary>
    External,
    /// <summary>CouchHud set it up but Windows switched it off again (password changed or wrong).</summary>
    Lost
}

public sealed record AutoLogonStatus(AutoLogonState State, string User, bool OtherUser);

/// <summary>Pure logic around the account for automatic sign-in: name handling and status.</summary>
public static class AutoLogonAccount
{
    /// <summary>Splits "DOMAIN\user", "user@example.com" or "user" into an account; without a domain the PC name is used.</summary>
    public static LogonAccount Parse(string? name, string machine)
    {
        name = (name ?? "").Trim();
        var slash = name.IndexOf('\\');
        if (slash > 0 && slash < name.Length - 1) return new LogonAccount(name[(slash + 1)..], name[..slash]);
        // A UPN carries its domain itself: LogonUser wants no separate domain then.
        if (name.Contains('@')) return new LogonAccount(name, "");
        return new LogonAccount(name, machine);
    }

    /// <summary>
    /// The accounts to try with LogonUser, in order: the name Windows reports, then the UPN
    /// (Microsoft accounts are known by their e-mail address as well).
    /// </summary>
    public static IReadOnlyList<LogonAccount> Candidates(LogonAccount primary, string? upn)
    {
        var list = new List<LogonAccount>();
        if (primary.User.Length > 0) list.Add(primary);
        var fromUpn = Parse(upn, primary.Domain);
        if (fromUpn.User.Contains('@') && !list.Contains(fromUpn)) list.Add(fromUpn);
        return list;
    }

    /// <summary>Does the account stored for the automatic sign-in belong to the signed-in user?</summary>
    public static bool IsSameAccount(string storedUser, string storedDomain, LogonAccount current, string? currentUpn, string machine)
    {
        if (string.IsNullOrWhiteSpace(storedUser)) return false;
        var stored = Parse(storedUser, string.IsNullOrWhiteSpace(storedDomain) ? machine : storedDomain.Trim());
        if (currentUpn is { Length: > 0 } && string.Equals(stored.User, currentUpn, StringComparison.OrdinalIgnoreCase)) return true;
        if (!string.Equals(stored.User, current.User, StringComparison.OrdinalIgnoreCase)) return false;
        // "." and an empty domain both mean this PC.
        string Norm(string d) => d is "" or "." ? machine : d;
        return string.Equals(Norm(stored.Domain), Norm(current.Domain), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Switching off what is off already: before the call nothing of CouchHUD's was there (state Off: Windows has it off, no marker, no
    /// config flag), the CouchHUD service refused because nothing of it is the caller's to switch off (it forgets the owner when it switches
    /// off), and Windows still shows it off after the refusal. The wish is met, so no "the service refused" (VM test 0.5). Only the refusal
    /// counts: a service that is not there or failed is still an error, and so is a refusal in the Lost state (Windows switched it off, but
    /// CouchHUD's marker and possibly the stored password are still there: "Delete saved password" must not report success then) or while
    /// it is on.
    /// </summary>
    public static bool AlreadyOff(AutoLogonState before, bool refusedByService, bool enabledAfter) =>
        before == AutoLogonState.Off && refusedByService && !enabledAfter;

    public static AutoLogonStatus Evaluate(bool enabled, string storedUser, string storedDomain, bool setUpByCouchHud, LogonAccount current, string? currentUpn, string machine)
    {
        if (!enabled) return new AutoLogonStatus(setUpByCouchHud ? AutoLogonState.Lost : AutoLogonState.Off, "", false);
        var other = !IsSameAccount(storedUser, storedDomain, current, currentUpn, machine);
        var plain = string.IsNullOrWhiteSpace(storedDomain) || storedDomain == "." || storedDomain.Equals(machine, StringComparison.OrdinalIgnoreCase)
                    || storedUser.Contains('\\') || storedUser.Contains('@');
        return new AutoLogonStatus(setUpByCouchHud ? AutoLogonState.CouchHud : AutoLogonState.External, plain ? storedUser : $"{storedDomain}\\{storedUser}", other);
    }

    /// <summary>A password for automatic sign-in must exist; Windows does not sign in accounts without one.</summary>
    public static bool IsUsablePassword(string? password) => !string.IsNullOrEmpty(password) && password.Length <= 256;
}

/// <summary>
/// Every wrong Windows password counts towards the account lockout. After a few in a row
/// the setup makes the user wait, so a mistyped password cannot lock the account.
/// </summary>
public sealed class PasswordAttemptLimiter(Func<DateTimeOffset> now, int freeAttempts = 3, int pauseSeconds = 30)
{
    private int _failures;
    private DateTimeOffset _blockedUntil;

    /// <summary>Time to wait before the next try, zero when allowed.</summary>
    public TimeSpan Wait => _blockedUntil > now() ? _blockedUntil - now() : TimeSpan.Zero;

    public void Failed()
    {
        if (++_failures < freeAttempts) return;
        _failures = 0;
        _blockedUntil = now().AddSeconds(pauseSeconds);
    }

    public void Succeeded() => _failures = 0;
}

public enum WinlogonRestoreKind
{
    /// <summary>Not ours to touch (nothing was saved, or someone else changed it since).</summary>
    Leave,
    Write,
    Delete
}

public readonly record struct WinlogonRestore(WinlogonRestoreKind Kind, string? Value = null);

/// <summary>
/// Turning the automatic sign-in off gives back the Winlogon user and domain name that were there before CouchHud
/// wrote its own account (they are saved at the first turn-on). Only while the value is still the one CouchHud wrote:
/// a name that someone else set in the meantime stays.
/// </summary>
public static class WinlogonNames
{
    public static WinlogonRestore Restore(string? current, string weWrote, string? previous, bool previousSaved)
    {
        if (!previousSaved) return new WinlogonRestore(WinlogonRestoreKind.Leave);
        if (current is null || !string.Equals(current, weWrote, StringComparison.OrdinalIgnoreCase)) return new WinlogonRestore(WinlogonRestoreKind.Leave);
        return previous is null
            ? new WinlogonRestore(WinlogonRestoreKind.Delete)
            : new WinlogonRestore(WinlogonRestoreKind.Write, previous);
    }
}

/// <summary>What the recovery does about the automatic sign-in.</summary>
public static class AutoLogonRecovery
{
    /// <summary>
    /// Only a sign-in that CouchHud set up and that is on: without the couch shell it would land on the normal desktop with no
    /// password. One set up by someone else (netplwiz, Autologon) is not ours to touch; one that is off needs nothing.
    /// </summary>
    public static bool ShouldSwitchOff(AutoLogonState state) => state == AutoLogonState.CouchHud;
}
