using System.Security.AccessControl;
using System.Security.Principal;
using CouchHud.Core.Security;
using Microsoft.Win32;

namespace CouchHud.Shell.Services;

/// <summary>
/// The developer switches of this start (--dev, remote debugging), decided once in App.OnStartup. Release builds only honour them
/// when an administrator set HKLM\SOFTWARE\CouchHud DeveloperMode=1 (DWORD) and the key is administrator-only.
/// </summary>
public static class DeveloperMode
{
#if DEBUG
    public const bool DebugBuild = true;
#else
    public const bool DebugBuild = false;
#endif

    /// <summary>The decision of this process; everything off until App.OnStartup has decided.</summary>
    public static DevDecision Current { get; private set; } = new(false, null, "not decided");

    public static DevDecision Decide(IEnumerable<string> args)
    {
        var machine = ReadMachineSwitch();
        // One line per start when the switch is ignored for its access list (with or without --dev), names only, never values.
        if (DevMode.UnsafeSwitchWarning(machine) is { } warning) Console.Error.WriteLine(warning);
        Current = DevMode.Decide(args.Contains(DevMode.DevSwitch), DebugBuild, machine);
        return Current;
    }

    /// <summary>
    /// Extra WebView2 browser arguments for developers (remote debugging on loopback). The HUD (MainWindow) and the couch browser
    /// (BrowserPane) both take theirs from here; a new WebView2 environment must do the same, never read them from elsewhere.
    /// </summary>
    public static string BrowserArgumentsFor(BrowserSurface surface, string? baseArguments = null) =>
        BrowserArguments.Build(baseArguments, Current.RemoteDebuggingPort, surface);

    /// <summary>HKLM (64-bit view) only; missing, unreadable or writable by others means off.</summary>
    private static MachineDevSwitch ReadMachineSwitch()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(DevMode.MachineKey);
            if (key is null) return MachineDevSwitch.None;
            // The raw descriptor straight from the key (RegGetKeySecurity), not the managed rule list: that one drops entries it cannot
            // express (callback, object, custom), and an entry the check does not understand has to count as granting (fail closed).
            var raw = new RawSecurityDescriptor(KeySecurity(key), 0);
            var adminOnly = DevMode.IsAdminOnly(raw.Owner?.Value, Rules(raw.DiscretionaryAcl));
            return new MachineDevSwitch(adminOnly, Dword(key, DevMode.DeveloperModeValue), Dword(key, DevMode.RemoteDebuggingPortValue));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException
                                       or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"[dev] machine switch unreadable, off: {ex.Message}");
            return MachineDevSwitch.None;
        }
    }

    private const uint OwnerSecurityInformation = 0x1;
    private const uint DaclSecurityInformation = 0x4;
    private const int ErrorInsufficientBuffer = 122;

    [System.Runtime.InteropServices.DllImport("advapi32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int RegGetKeySecurity(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, uint securityInformation, byte[]? descriptor, ref uint size);

    /// <summary>
    /// Owner and DACL of the open key as a self-relative security descriptor: size first, then the bytes (once more if the descriptor
    /// grew in between). Any other error throws (the caller treats the switch as off).
    /// </summary>
    private static byte[] KeySecurity(RegistryKey key)
    {
        const uint info = OwnerSecurityInformation | DaclSecurityInformation;
        uint size = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = size == 0 ? null : new byte[size];
            var error = RegGetKeySecurity(key.Handle, info, buffer, ref size);
            if (error == 0 && buffer is not null) return buffer.Length == size ? buffer : buffer[..(int)size];
            if (error is not (0 or ErrorInsufficientBuffer) || size == 0 || size > 64 * 1024)
                throw new System.ComponentModel.Win32Exception(error == 0 ? ErrorInsufficientBuffer : error, "RegGetKeySecurity failed");
        }
        throw new InvalidOperationException("RegGetKeySecurity: security descriptor keeps changing size");
    }

    /// <summary>The DACL's entries for <see cref="DevMode.IsAdminOnly"/>; null for a null DACL (everyone has full access).</summary>
    private static List<KeyAccessRule>? Rules(RawAcl? dacl)
    {
        if (dacl is null) return null;
        var rules = new List<KeyAccessRule>();
        foreach (GenericAce ace in dacl)
        {
            var inheritOnly = ace.AceFlags.HasFlag(AceFlags.InheritOnly);
            rules.Add(ace switch
            {
                CommonAce { IsCallback: false, AceQualifier: AceQualifier.AccessAllowed } c => new KeyAccessRule(c.SecurityIdentifier.Value, AceKind.Allow, c.AccessMask, inheritOnly),
                CommonAce { IsCallback: false, AceQualifier: AceQualifier.AccessDenied } c => new KeyAccessRule(c.SecurityIdentifier.Value, AceKind.Deny, c.AccessMask, inheritOnly),
                CommonAce c => new KeyAccessRule(c.SecurityIdentifier.Value, AceKind.Unknown, c.AccessMask, inheritOnly),
                KnownAce k => new KeyAccessRule(k.SecurityIdentifier.Value, AceKind.Unknown, k.AccessMask, inheritOnly),
                _ => new KeyAccessRule("", AceKind.Unknown, -1, inheritOnly)
            });
        }
        return rules;
    }

    private static int? Dword(RegistryKey key, string name) =>
        key.GetValue(name) is { } value ? DevMode.Dword(value, key.GetValueKind(name)) : null;
}
