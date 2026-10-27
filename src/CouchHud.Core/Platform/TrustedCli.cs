namespace CouchHud.Core.Platform;

/// <summary>
/// The shared check for running an installed program from the user's session (CaseHUD's API switch, the WebView2 runtime): where the
/// program may be run from. The Windows side (canonical path, Program Files roots) is in the shell's <c>TrustedCliRunner</c>.
/// </summary>
public static class TrustedCli
{
    /// <summary>
    /// The installed exe may be run from the user's session: a full, canonical path to <paramref name="exeName"/> in a folder below one of
    /// <paramref name="trustedRoots"/> (Program Files; only administrators write there). No relative paths, no "..", no UNC, never directly
    /// in the root itself.
    /// </summary>
    public static bool IsTrustedInstallPath(string? exe, string exeName, IEnumerable<string?> trustedRoots)
    {
        if (!IsDrivePath(exe) || string.IsNullOrEmpty(exeName)) return false;
        var parts = exe!.Split('\\', '/');
        if (parts.Any(part => part is "." or "..") || !string.Equals(parts[^1], exeName, StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var root in trustedRoots)
        {
            if (!IsDrivePath(root) || root!.Split('\\', '/').Any(part => part is "." or "..")) continue;
            var prefix = root.TrimEnd('\\', '/') + '\\';
            if (exe.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && exe.Length > prefix.Length + exeName.Length) return true;
        }
        return false;
    }

    /// <summary>"C:\…": a local drive path (written out here, so the check is the same on every system the tests run on).</summary>
    public static bool IsDrivePath(string? path) =>
        path is { Length: >= 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
}
