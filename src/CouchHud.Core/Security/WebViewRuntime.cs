using CouchHud.Core.Guard;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Security;

/// <summary>One registration of the Evergreen WebView2 runtime: EdgeUpdate's <c>location</c> and <c>pv</c> (HKLM, 32- or 64-bit view).</summary>
public sealed record RuntimeRegistration(string? Location, string? Version);

/// <summary>The runtime folder CouchHUD passes to WebView2 explicitly, or null (then the loader's own choice, logged), and why.</summary>
public sealed record RuntimeFolderChoice(string? Folder, string Reason);

/// <summary>
/// Which WebView2 runtime the HUD runs on. Left to itself the loader may pick a per-user runtime (registered in HKCU, files in
/// %LOCALAPPDATA%), which every program of the user can change or fake. CouchHUD therefore reads the machine-wide registration (HKLM,
/// written by the elevated runtime installer) and passes that version folder as browserExecutableFolder, but only when it is the real
/// runtime below Program Files: canonical path, no reparse point on the way, <c>msedgewebview2.exe</c> present.
/// </summary>
public static class WebViewRuntime
{
    public const string ClientKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
    public const string Exe = "msedgewebview2.exe";

    /// <param name="registrations">The HKLM registrations (both views); the highest valid version wins.</param>
    /// <param name="trustedRoots">Program Files (64 and 32 bit).</param>
    /// <param name="fileExists">Whether the exe is there.</param>
    /// <param name="canonical">The real path of the exe through an open handle (links and short names resolved).</param>
    /// <param name="hasReparsePoint">Whether any part of the path is a reparse point (junction, symbolic link, mount point).</param>
    public static RuntimeFolderChoice Choose(IEnumerable<RuntimeRegistration> registrations, IEnumerable<string?> trustedRoots,
        Func<string, bool> fileExists, Func<string, string> canonical, Func<string, bool> hasReparsePoint)
    {
        var roots = trustedRoots.ToList();
        (Version Version, string Folder)? best = null;
        var reason = "no machine-wide runtime registered";
        foreach (var registration in registrations)
        {
            if (Check(registration, roots, fileExists, canonical, hasReparsePoint, out var version, out var folder, out var why))
            {
                if (best is null || version > best.Value.Version) best = (version, folder);
            }
            else reason = why;
        }
        return best is { } chosen ? new RuntimeFolderChoice(chosen.Folder, $"machine-wide runtime {chosen.Version}") : new RuntimeFolderChoice(null, reason);
    }

    private static bool Check(RuntimeRegistration registration, List<string?> roots, Func<string, bool> fileExists, Func<string, string> canonical,
        Func<string, bool> hasReparsePoint, out Version version, out string folder, out string why)
    {
        version = new Version();
        folder = "";
        if (string.IsNullOrWhiteSpace(registration.Location) || string.IsNullOrWhiteSpace(registration.Version))
        {
            why = "no machine-wide runtime registered";
            return false;
        }
        var pv = registration.Version.Trim();
        if (!pv.All(c => char.IsAsciiDigit(c) || c == '.') || !Version.TryParse(pv, out var parsed) || parsed.Revision < 0 || parsed == new Version(0, 0, 0, 0))
        {
            why = "machine-wide runtime has no valid version";
            return false;
        }
        var location = registration.Location.Trim().TrimEnd('\\', '/');
        if (!TrustedCli.IsDrivePath(location + '\\'))
        {
            why = "machine-wide runtime location is not a local path";
            return false;
        }
        var exe = $@"{location}\{pv}\{Exe}";
        try
        {
            if (!fileExists(exe))
            {
                why = "machine-wide runtime registered but missing";
                return false;
            }
            var real = canonical(exe);
            if (!TrustedCli.IsTrustedInstallPath(real, Exe, roots) || !LockedForeground.IsWebViewRuntimePath(real))
            {
                why = "machine-wide runtime is not below Program Files";
                return false;
            }
            if (hasReparsePoint(real))
            {
                why = "machine-wide runtime path has a reparse point";
                return false;
            }
            version = parsed;
            folder = real[..real.LastIndexOfAny(['\\', '/'])];
            why = "";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            why = $"machine-wide runtime path rejected ({ex.GetType().Name})";
            return false;
        }
    }
}

/// <summary>What to do next when starting a web view (HUD or couch browser) on a runtime.</summary>
public enum RuntimeStartAction
{
    /// <summary>Start on the machine-wide folder (chosen anew each time: an update may just have replaced the version folder).</summary>
    Machine,
    /// <summary>The profile was last used by a newer runtime: put it aside (renamed, never deleted), then the machine-wide folder again.</summary>
    SetAsideProfile,
    /// <summary>The loader's own choice (may be a per-user runtime; checked and warned about afterwards).</summary>
    Loader,
    /// <summary>A lock or parental controls are on and a protected runtime exists: never fall back to the loader. Desktop fallback (Windows lock).</summary>
    FailClosed,
    /// <summary>Nothing more to try: the error goes up (desktop fallback, as without this policy).</summary>
    Fail
}

/// <summary>Where a web view start stands, for <see cref="RuntimeStartPolicy.Next"/>.</summary>
/// <param name="Strict">A couch lock or parental controls are on.</param>
/// <param name="MachineFolder">The machine-wide folder <see cref="WebViewRuntime.Choose"/> gives right now, or null.</param>
/// <param name="MachineFailures">Starts on a machine-wide folder that failed so far.</param>
/// <param name="ProfileNewer">The profile's "Last Version" is newer than the runtime of <paramref name="MachineFolder"/>.</param>
/// <param name="ProfileSetAside">The profile was put aside already (once per start at most).</param>
/// <param name="LoaderTried">A start with the loader's choice was made already.</param>
public sealed record RuntimeStartState(bool Strict, string? MachineFolder, int MachineFailures, bool ProfileNewer, bool ProfileSetAside, bool LoaderTried);

public static class RuntimeStartPolicy
{
    /// <summary>Starts on a machine-wide folder before giving up on it (the second with a fresh choice), plus one after setting the profile aside.</summary>
    public const int MachineAttempts = 2;

    /// <summary>
    /// The next step. First the machine-wide runtime; a failure is tried once more with a fresh choice (update races), a profile from a
    /// newer runtime is put aside once. Then: with a lock or parental controls on and a protected runtime there, fail closed (the
    /// loader might pick a per-user runtime); otherwise the loader's choice once, with a warning. Without any machine-wide runtime the
    /// loader is all there is (warned about after the start), whatever the lock.
    /// </summary>
    public static RuntimeStartAction Next(RuntimeStartState s)
    {
        if (s.LoaderTried) return RuntimeStartAction.Fail;
        if (s.MachineFailures == 0) return s.MachineFolder is null ? RuntimeStartAction.Loader : RuntimeStartAction.Machine;
        if (s.MachineFolder is not null && s.ProfileNewer && !s.ProfileSetAside) return RuntimeStartAction.SetAsideProfile;
        var budget = MachineAttempts + (s.ProfileSetAside ? 1 : 0);
        if (s.MachineFolder is not null && s.MachineFailures < budget) return RuntimeStartAction.Machine;
        return s.Strict ? RuntimeStartAction.FailClosed : RuntimeStartAction.Loader;
    }

    /// <summary>
    /// Whether the profile was last used by a newer runtime than the one in <paramref name="machineFolder"/> (its last segment is the
    /// version): the content of <c>EBWebView\Last Version</c>. Anything unreadable counts as not newer.
    /// </summary>
    public static bool ProfileNewer(string? lastVersionFile, string? machineFolder)
    {
        if (string.IsNullOrWhiteSpace(lastVersionFile) || string.IsNullOrWhiteSpace(machineFolder)) return false;
        var segment = machineFolder.TrimEnd('\\', '/');
        segment = segment[(segment.LastIndexOfAny(['\\', '/']) + 1)..];
        return Version.TryParse(lastVersionFile.Trim(), out var profile) && Version.TryParse(segment, out var runtime) && profile > runtime;
    }

    /// <summary>The name a profile folder is put aside under (next to it, never deleted).</summary>
    public static string SetAsideName(string profileFolder, DateTime now) =>
        $"{profileFolder.TrimEnd('\\', '/')}.newer-runtime-{now:yyyyMMdd-HHmmss}";

    /// <summary>Profiles put aside that may lie next to the profile at most (the new one included).</summary>
    public const int SetAsideKept = 2;

    /// <summary>The search pattern for the profiles put aside next to <paramref name="profileFolder"/> (folder name only).</summary>
    public static string SetAsidePattern(string profileFolder)
    {
        var trimmed = profileFolder.TrimEnd('\\', '/');
        return $"{trimmed[(trimmed.LastIndexOfAny(['\\', '/']) + 1)..]}.newer-runtime-*";
    }

    /// <summary>
    /// Which of <paramref name="siblings"/> (full paths) to remove before one more profile is put aside, oldest first, so that at most
    /// <see cref="SetAsideKept"/> remain afterwards. Only exact names from <see cref="SetAsideName"/> count (a timestamp in the name,
    /// which sorts by time); anything else in the folder is never touched.
    /// </summary>
    public static IReadOnlyList<string> SetAsideToRemove(string profileFolder, IEnumerable<string> siblings, int kept = SetAsideKept)
    {
        var prefix = $"{profileFolder.TrimEnd('\\', '/')}.newer-runtime-";
        var ours = siblings
            .Select(s => s.TrimEnd('\\', '/'))
            .Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && IsStamp(s[prefix.Length..]))
            .OrderBy(s => s[prefix.Length..], StringComparer.Ordinal)
            .ToList();
        var excess = ours.Count - (kept - 1);
        return excess > 0 ? ours.Take(excess).ToList() : [];
    }

    private static bool IsStamp(string s) =>
        s.Length == 15 && s[8] == '-' && s.Where((c, i) => i != 8).All(char.IsAsciiDigit);
}
