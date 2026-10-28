using System.Security.Cryptography;
using System.Text.Json;

namespace CouchHud.Core.Plugins;

public enum PluginTrustState
{
    /// <summary>The folder is exactly what was installed (or approved).</summary>
    Trusted,

    /// <summary>Nobody installed or approved it through CouchHud: something put a plugin folder there.</summary>
    Unknown,

    /// <summary>Installed before, but the files are not the ones that were installed.</summary>
    Changed,

    /// <summary>The list of approved plugins is damaged or was not written by CouchHud: nothing unknown loads and nothing is overwritten.</summary>
    Fault
}

/// <summary>
/// A plugin is a program that runs inside the shell. The folder for user plugins is writable by every program of the
/// user, so CouchHud remembers a hash of each plugin it installed. A folder that nobody installed, or whose files
/// changed, only loads after the user approved it (which needs the couch unlocked).
/// The list is signed with an HMAC (the same secret as the play time file, protected for this Windows user): a program that
/// merely writes files, or a copied-over file, cannot add a plugin to it. A list that fails the check is not overwritten
/// but reported as <see cref="PluginTrustState.Fault"/>, and nothing unknown loads then. Against a program that runs as the
/// user it protects nothing: that one can read the secret as well.
/// </summary>
public sealed class PluginTrust(string dataDir, byte[]? key = null)
{
    private static readonly object Lock = new();

    /// <summary>Where the secret comes from when no key is given (the shell sets it at start; the catalog code has no access to it).</summary>
    public static Func<byte[]?>? KeyProvider { get; set; }

    private byte[]? Key => key ?? KeyProvider?.Invoke();

    /// <summary>Files Windows or the user adds on their own: they say nothing about the plugin and must not make it "changed".</summary>
    internal static bool Ignored(string fileName) =>
        fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private sealed class Store
    {
        public Dictionary<string, string> Entries { get; set; } = [];
        public string? Mac { get; set; }
    }

    private static string Mac(byte[] key, Dictionary<string, string> entries)
    {
        var canonical = string.Join("\n", entries.OrderBy(e => e.Key.ToLowerInvariant(), StringComparer.Ordinal).Select(e => $"{e.Key.ToLowerInvariant()}={e.Value}"));
        return Convert.ToBase64String(HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("plugin-trust\n" + canonical)));
    }

    /// <summary>True while the list exists but cannot be trusted (see <see cref="PluginTrustState.Fault"/>).</summary>
    public bool IsFaulty => !TryRead(out _);

    private string StoreFile => Path.Combine(dataDir, "plugin-trust.json");

    /// <summary>
    /// SHA-256 over all files of the folder: relative path (lower case, '/') and content, in a fixed order.
    /// Leftovers of an interrupted install do not count.
    /// </summary>
    public static string HashFolder(string dir)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var files = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !Ignored(Path.GetFileName(f)))
            .Select(f => (Full: f, Relative: Path.GetFullPath(f)[root.Length..].Replace('\\', '/').ToLowerInvariant()))
            .OrderBy(f => f.Relative, StringComparer.Ordinal)
            .ToList();
        foreach (var (full, relative) in files)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(relative);
            hash.AppendData(BitConverter.GetBytes(name.Length));
            hash.AppendData(name);
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var content = SHA256.HashData(stream);
            hash.AppendData(content);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>A short form of the hash for a dialog: the code the user sees before approving ("a1b2-c3d4-e5f6").</summary>
    public static string ShortCode(string hash) => hash.Length >= 12 ? $"{hash[..4]}-{hash[4..8]}-{hash[8..12]}" : hash;

    public PluginTrustState Check(string folder, string dir)
    {
        if (!TryRead(out var all)) return PluginTrustState.Fault;
        if (!all.TryGetValue(folder, out var known)) return PluginTrustState.Unknown;
        string now;
        try
        {
            now = HashFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginTrustState.Changed;
        }
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(known), System.Text.Encoding.ASCII.GetBytes(now))
            ? PluginTrustState.Trusted
            : PluginTrustState.Changed;
    }

    /// <summary>Remembers what the folder holds now (after an install, or after the user approved it). Returns the hash.</summary>
    public string Trust(string folder, string dir)
    {
        var hash = HashFolder(dir);
        lock (Lock)
        {
            if (!TryRead(out var all)) throw new InvalidOperationException(CouchHud.Core.Localization.Strings.Get("plugin.trustFault"));
            all[folder] = hash;
            Write(all);
        }
        return hash;
    }

    /// <summary>
    /// The first time (no trust file yet): plugins that are already in the user folder were put there by the user in earlier
    /// versions, so they count as approved. From then on only what CouchHud installs or the user approves is trusted.
    /// </summary>
    public bool AdoptExisting(string pluginsDir)
    {
        lock (Lock)
        {
            // Only the very first time: a list that exists (even a damaged one) is never replaced by what lies there now.
            if (File.Exists(StoreFile)) return false;
            var all = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(pluginsDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(pluginsDir))
                {
                    var folder = Path.GetFileName(dir);
                    if (folder.StartsWith(PackageInstaller.TempPrefix, StringComparison.Ordinal)) continue;
                    try
                    {
                        all[folder] = HashFolder(dir);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"[plugins] {folder}: not adopted ({ex.Message})");
                    }
                }
            }
            Write(all);
        }
        return true;
    }

    public void Forget(string folder)
    {
        lock (Lock)
        {
            if (TryRead(out var all) && all.Remove(folder)) Write(all);
        }
    }

    /// <summary>
    /// The approved plugins. False when the file is there but damaged or its signature does not fit (not written by CouchHud,
    /// changed, or from before the signature existed); a missing file is an empty list.
    /// </summary>
    private bool TryRead(out Dictionary<string, string> all)
    {
        all = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(StoreFile)) return true;
            var store = JsonSerializer.Deserialize<Store>(File.ReadAllText(StoreFile));
            if (store?.Entries is null) return false;
            if (Key is { } k && (store.Mac is not { Length: > 0 } mac
                    || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(mac), System.Text.Encoding.UTF8.GetBytes(Mac(k, store.Entries)))))
            {
                Console.Error.WriteLine("[plugins] the list of approved plugins does not carry a valid signature, nothing unknown loads");
                return false;
            }
            all = new Dictionary<string, string>(store.Entries, StringComparer.OrdinalIgnoreCase);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[plugins] the list of approved plugins is unreadable ({ex.Message}), nothing unknown loads");
            return false;
        }
    }

    private void Write(Dictionary<string, string> all)
    {
        var store = new Store { Entries = new Dictionary<string, string>(all, StringComparer.OrdinalIgnoreCase) };
        if (Key is { } k) store.Mac = Mac(k, store.Entries);
        // Written through to the disk, so a reset does not leave zero bytes (that would only be a fault: nothing unknown loads). No .bak on
        // purpose: an older list could still approve a plugin that was taken back since.
        CouchHud.Core.Files.DurableFile.WriteAllText(StoreFile, JsonSerializer.Serialize(store), keepBackup: false);
    }
}
