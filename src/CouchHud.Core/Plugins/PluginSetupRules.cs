using System.Security.Cryptography;

namespace CouchHud.Core.Plugins;

/// <summary>
/// The checks before a setup that one of CouchHUD's own plugins downloaded is handed to Windows (<c>IPluginContext.RunSetupAsync</c>, capability
/// "setup.run", first-party plugins only). The plugin names a file and the SHA-256 it verified; the host re-checks both itself and starts nothing
/// else. The Windows side (the unlock, the held file handle, its real path, UAC) is in the shell (<c>Hud.RunPluginSetupAsync</c>).
/// </summary>
public static class PluginSetupRules
{
    /// <summary>The largest setup a plugin may hand over.</summary>
    public const long MaxSetupBytes = 400L * 1024 * 1024;

    public const int MaxPathChars = 260;

    public static readonly TimeSpan MaxRun = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Why the file may not be handed over, or null. It must be an .exe inside the calling plugin's own data folder (<paramref name="pluginDataRoot"/>,
    /// plugin-data\id: never another plugin's), named by a full local path without "..", links or control characters, and the hash must be 64 hex digits.
    /// </summary>
    public static string? Refuse(string? path, string? sha256, string pluginDataRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathChars || path.Any(char.IsControl)) return "path";
        if (!Platform.TrustedCli.IsDrivePath(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return "path";
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "type";
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) return "hash";
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "path";
        }
        // Nothing but the normal form of the path is accepted ("a\..\b" and "a/b" are not).
        if (!string.Equals(full, path, StringComparison.OrdinalIgnoreCase)) return "path";
        var root = Path.GetFullPath(pluginDataRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length == root.Length) return "place";
        return null;
    }

    /// <summary>The folder of the setup holds nothing but the setup itself (no DLL next to it for the installer to pick up).</summary>
    public static bool FolderHoldsOnlyTheSetup(string path)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (folder is null) return false;
            var entries = Directory.EnumerateFileSystemEntries(folder).Take(2).ToList();
            return entries.Count == 1 && string.Equals(entries[0], Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Does the held stream hash to <paramref name="sha256"/>? Reads at most <see cref="MaxSetupBytes"/>; leaves the stream at the start.</summary>
    public static bool HashMatches(Stream stream, string sha256)
    {
        if (stream.Length is <= 0 or > MaxSetupBytes) return false;
        stream.Position = 0;
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return actual.Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The host's proof that a person pressed something before a plugin hands over a setup: a one-shot token per plugin id, set when the plugin's own
/// row or guide menu entry that asks for the parents' code was pressed (after the host's gates passed), consumed by the hand-over. It is cleared as soon as the plugin's call returns and runs out
/// (the longest download of a plugin is 30 minutes) and is bound to the plugin id, so a press of one plugin pays for nobody else.
/// </summary>
public sealed class PluginSetupPress(Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(35);

    private readonly object _lock = new();
    private readonly Dictionary<string, DateTimeOffset> _until = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset Now => (now ?? (() => DateTimeOffset.UtcNow))();

    public void Issue(string pluginId)
    {
        lock (_lock) _until[pluginId] = Now + Lifetime;
    }

    /// <summary>True once per press and only for the plugin that was pressed.</summary>
    public bool TryConsume(string pluginId)
    {
        lock (_lock) return _until.Remove(pluginId, out var until) && until >= Now;
    }

    public void Clear()
    {
        lock (_lock) _until.Clear();
    }

    /// <summary>The press is over (the plugin's call returned): its token is gone, used or not.</summary>
    public void Clear(string pluginId)
    {
        lock (_lock) _until.Remove(pluginId);
    }
}
