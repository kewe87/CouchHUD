using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

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
/// What the trust store knows about one plugin folder (format 2). <see cref="Capabilities"/> null: a plugin from before the capabilities
/// (format 1, or approved by hand without any), which gets the SDK 2 surface at most. <see cref="KeyFingerprint"/> is the pinned publisher
/// key of a signed package (trust on first use; an update must carry the same key).
/// </summary>
public sealed record PluginTrustEntry
{
    public required string Hash { get; init; }
    public PluginOrigin Origin { get; init; } = PluginOrigin.Local;
    public string? PluginId { get; init; }
    public string? Version { get; init; }
    public string? KeyFingerprint { get; init; }
    public IReadOnlyList<string>? Capabilities { get; init; }

    /// <summary>Programs (canonical paths) the user confirmed for <see cref="CouchHud.Sdk.IPluginContext.RunTrustedCliAsync"/>.</summary>
    public IReadOnlyList<string> ConfirmedExes { get; init; } = [];

    /// <summary>The parents let this exact folder (hash) run although it is revoked.</summary>
    public string? RevocationAccepted { get; init; }

    /// <summary>The newest version that was ever installed for this plugin id (no downgrade below it while the plugin stays).</summary>
    public string? HighestVersion { get; init; }
}

/// <summary>
/// A plugin is a program that runs inside the shell. The folder for user plugins is writable by every program of the
/// user, so CouchHUD remembers a hash of each plugin it installed. A folder that nobody installed, or whose files
/// changed, only loads after the user approved it (which needs the couch unlocked).
/// The list is signed with an HMAC (the same secret as the play time file, protected for this Windows user): a program that
/// merely writes files, or a copied-over file, cannot add a plugin to it. A list that fails the check is not overwritten
/// but reported as <see cref="PluginTrustState.Fault"/>, and nothing unknown loads then. Against a program that runs as the
/// user it protects nothing: that one can read the secret as well.
/// Format 2 adds per folder the origin, the pinned publisher key, the granted capabilities and the confirmed programs. A format 1 list
/// with a valid signature is taken over as "local, no capabilities recorded" (the SDK 2 surface): the migration grants nothing new.
/// </summary>
public sealed class PluginTrust(string dataDir, byte[]? key = null)
{
    private static readonly object Lock = new();

    private static Func<byte[]?>? _keyProvider;

    /// <summary>
    /// Where the secret comes from when no key is given. Set once, at start, by the shell (a second call is refused, so nothing loaded later
    /// can swap it). Without a secret (none set, or it cannot be read: an access error, the file replaced by a folder) an existing list counts as
    /// <see cref="PluginTrustState.Fault"/> and nothing is written: the signature is never skipped.
    /// </summary>
    public static void UseKey(Func<byte[]?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (Interlocked.CompareExchange(ref _keyProvider, provider, null) is not null) throw new InvalidOperationException("the key of the plugin list is set already");
    }

    /// <summary>The secret, or null when there is none (an empty key counts as none).</summary>
    private byte[]? Key
    {
        get
        {
            try
            {
                return (key ?? _keyProvider?.Invoke()) is { Length: > 0 } k ? k : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or InvalidOperationException)
            {
                Console.Error.WriteLine($"[plugins] the key of the list of approved plugins cannot be read: {ex.Message}");
                return null;
            }
        }
    }

    /// <summary>Files Windows or the user adds on their own: they say nothing about the plugin and must not make it "changed".</summary>
    internal static bool Ignored(string fileName) =>
        fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    private sealed class StoreV2
    {
        public int Format { get; set; } = 2;
        public Dictionary<string, PluginTrustEntry> Plugins { get; set; } = [];
        /// <summary>Updates that wait for the next start: what was verified when they were staged (hash = the package's SHA-256).</summary>
        public Dictionary<string, PluginTrustEntry> Pending { get; set; } = [];
        /// <summary>Folders the user removed while their DLLs were loaded: deleted at the next start before anything loads, never loaded again.</summary>
        public List<string>? Removed { get; set; }
        public string? Mac { get; set; }
    }

    private sealed record State(Dictionary<string, PluginTrustEntry> Plugins, Dictionary<string, PluginTrustEntry> Pending)
    {
        public HashSet<string> Removed { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string MacV1(byte[] key, Dictionary<string, string> entries)
    {
        var canonical = string.Join("\n", entries.OrderBy(e => e.Key.ToLowerInvariant(), StringComparer.Ordinal).Select(e => $"{e.Key.ToLowerInvariant()}={e.Value}"));
        return Convert.ToBase64String(HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("plugin-trust\n" + canonical)));
    }

    /// <summary>Format 2: over the canonical JSON of both maps (keys in lower case and sorted), under its own context line.</summary>
    private static string MacV2(byte[] key, State state)
    {
        static SortedDictionary<string, PluginTrustEntry> Sorted(Dictionary<string, PluginTrustEntry> d) =>
            new(d.ToDictionary(e => e.Key.ToLowerInvariant(), e => e.Value), StringComparer.Ordinal);
        // The removal marks are covered too (only when there are any, so a list without them keeps its signature).
        var canonical = state.Removed.Count == 0
            ? JsonSerializer.Serialize(new { plugins = Sorted(state.Plugins), pending = Sorted(state.Pending) }, Json)
            : JsonSerializer.Serialize(new { plugins = Sorted(state.Plugins), pending = Sorted(state.Pending), removed = state.Removed.Select(f => f.ToLowerInvariant()).Order(StringComparer.Ordinal).ToList() }, Json);
        return Convert.ToBase64String(HMACSHA256.HashData(key, System.Text.Encoding.UTF8.GetBytes("plugin-trust-v2\n" + canonical)));
    }

    /// <summary>True while the list exists but cannot be trusted (see <see cref="PluginTrustState.Fault"/>).</summary>
    public bool IsFaulty => !TryRead(out _);

    private string StoreFile => Path.Combine(dataDir, "plugin-trust.json");

    /// <summary>The list exists (adopted, written or damaged): the one-time takeover of an install from 0.5.x or older is done.</summary>
    public bool HasStore => File.Exists(StoreFile);

    /// <summary>A folder twice in a different case ("Foo" and "foo") is ambiguous: such a list was not written by CouchHud.</summary>
    private static bool HasCaseDuplicates(IEnumerable<string> folders)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return folders.Any(f => !seen.Add(f));
    }

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
        if (!TryRead(out var state)) return PluginTrustState.Fault;
        if (state.Removed.Contains(folder)) return PluginTrustState.Unknown;
        if (!state.Plugins.TryGetValue(folder, out var known)) return PluginTrustState.Unknown;
        string now;
        try
        {
            now = HashFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PluginTrustState.Changed;
        }
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(known.Hash), System.Text.Encoding.ASCII.GetBytes(now))
            ? PluginTrustState.Trusted
            : PluginTrustState.Changed;
    }

    /// <summary>What is recorded for the folder; null when nothing is (or the list cannot be trusted).</summary>
    public PluginTrustEntry? Entry(string folder) => TryRead(out var state) && state.Plugins.TryGetValue(folder, out var e) ? e : null;

    /// <summary>The publisher key pinned for a plugin id (any folder), or null.</summary>
    public string? PinnedKey(string pluginId) =>
        TryRead(out var state) ? state.Plugins.Values.FirstOrDefault(e => string.Equals(e.PluginId, pluginId, StringComparison.OrdinalIgnoreCase) && e.KeyFingerprint is not null)?.KeyFingerprint : null;

    /// <summary>All recorded entries for a plugin id (pin and downgrade checks). A list that cannot be trusted refuses (a catalog error, never a crash).</summary>
    public IReadOnlyList<(string Folder, PluginTrustEntry Entry)> ForId(string pluginId) =>
        TryRead(out var state)
            ? state.Plugins.Where(e => string.Equals(e.Value.PluginId, pluginId, StringComparison.OrdinalIgnoreCase)).Select(e => (e.Key, e.Value)).ToList()
            : throw new CatalogException(CouchHud.Core.Localization.Strings.Get("plugin.trustFault"));

    /// <summary>The folder was removed by the user and waits for the next start to be deleted (it never loads again).</summary>
    public bool IsRemoved(string folder) => TryRead(out var state) && state.Removed.Contains(folder);

    /// <summary>The folders that wait for their removal (empty when the list cannot be trusted: then nothing loads anyway).</summary>
    internal IReadOnlyList<string> RemovedFolders() => TryRead(out var state) ? [.. state.Removed] : [];

    /// <summary>
    /// Remembers what the folder holds now, by hand (after the user approved it, or a plain .zip without a statement): always
    /// <see cref="PluginOrigin.Local"/>, with the capabilities that were shown (null: none recorded, the SDK 2 surface). Returns the hash.
    /// </summary>
    internal string Trust(string folder, string dir, IReadOnlyList<string>? capabilities = null) =>
        Trust(folder, dir, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.Local, Capabilities = capabilities });

    /// <summary>Remembers the folder with what its verified statement says (origin, id, version, key, capabilities). Returns the hash.</summary>
    internal string Trust(string folder, string dir, PluginTrustEntry meta) => Record(folder, HashFolder(dir), meta);

    /// <summary>
    /// The same with the hash of exactly the files that were checked (the unpacked package before it moved into place, the folder whose code
    /// the user saw): whatever lies in the folder later must match it, or it is <see cref="PluginTrustState.Changed"/>.
    /// </summary>
    internal string Record(string folder, string hash, PluginTrustEntry meta)
    {
        if (hash.Length != 64 || !hash.All(Uri.IsHexDigit)) throw new ArgumentException("the folder needs its SHA-256");
        hash = hash.ToLowerInvariant();
        Update(state =>
        {
            state.Removed.Remove(folder);
            var old = state.Plugins.GetValueOrDefault(folder);
            var highest = Highest(old?.HighestVersion, meta.Version);
            // Programs the user confirmed stay confirmed for the same plugin (an update), never for another one.
            var exes = old is not null && string.Equals(old.PluginId, meta.PluginId, StringComparison.OrdinalIgnoreCase) && old.Origin == meta.Origin ? old.ConfirmedExes : [];
            state.Plugins[folder] = meta with { Hash = hash, HighestVersion = highest, ConfirmedExes = exes, RevocationAccepted = null };
        });
        return hash;
    }

    private static string? Highest(string? a, string? b)
    {
        if (!SemVer.TryParse(a, out var va)) return b;
        if (!SemVer.TryParse(b, out var vb)) return a;
        return va >= vb ? a : b;
    }

    /// <summary>The parents confirmed a program for this plugin's <c>RunTrustedCliAsync</c>.</summary>
    internal void ConfirmExe(string folder, string canonicalExe) => Update(state =>
    {
        if (!state.Plugins.TryGetValue(folder, out var e)) throw new ArgumentException(CouchHud.Core.Localization.Strings.Get("plugin.pageNotFound"));
        if (!e.ConfirmedExes.Contains(canonicalExe, StringComparer.OrdinalIgnoreCase)) state.Plugins[folder] = e with { ConfirmedExes = [.. e.ConfirmedExes, canonicalExe] };
    });

    /// <summary>The parents let a revoked plugin run again: for exactly the files it has now.</summary>
    internal void AcceptRevocation(string folder, string dir)
    {
        var hash = HashFolder(dir);
        Update(state =>
        {
            if (!state.Plugins.TryGetValue(folder, out var e) || e.Hash != hash) throw new ArgumentException(CouchHud.Core.Localization.Strings.Get("plugin.pageNotFound"));
            state.Plugins[folder] = e with { RevocationAccepted = hash };
        });
    }

    /// <summary>
    /// An update waits for the next start: what was verified now, with the SHA-256 of the verified package (hex) in
    /// <see cref="PluginTrustEntry.Hash"/>. Every staged package has such an entry; one without it is never put in place.
    /// </summary>
    internal void StagePending(string folder, PluginTrustEntry verified)
    {
        if (verified.Hash.Length != 64 || !verified.Hash.All(Uri.IsHexDigit)) throw new ArgumentException("the staged package needs its SHA-256");
        Update(state => state.Pending[folder] = verified with { Hash = verified.Hash.ToLowerInvariant() });
    }

    /// <summary>The staged update of the folder, without taking it (null when there is none, or the list cannot be trusted).</summary>
    internal PluginTrustEntry? PendingFor(string folder) => TryRead(out var state) ? state.Pending.GetValueOrDefault(folder) : null;

    /// <summary>
    /// The user removed the folder while its DLLs are loaded: its approval and any staged update go, and a mark under the signature makes
    /// the next start delete it before anything loads (it never shows up as an unknown plugin to approve).
    /// </summary>
    internal void MarkRemoved(string folder) => Update(state =>
    {
        state.Plugins.Remove(folder);
        state.Pending.Remove(folder);
        state.Removed.Add(folder);
    });

    /// <summary>The folder is gone: its mark goes as well.</summary>
    internal void ClearRemoved(string folder) => Update(state => state.Removed.Remove(folder));

    /// <summary>Takes (and removes) the staged update of the folder.</summary>
    internal PluginTrustEntry? TakePending(string folder)
    {
        PluginTrustEntry? taken = null;
        Update(state =>
        {
            if (state.Pending.Remove(folder, out var e)) taken = e;
        });
        return taken;
    }

    private void Update(Action<State> change)
    {
        lock (Lock)
        {
            if (!TryRead(out var state)) throw new InvalidOperationException(CouchHud.Core.Localization.Strings.Get("plugin.trustFault"));
            change(state);
            Write(state);
        }
    }

    /// <summary>
    /// Once, on the upgrade from 0.5.x or older (the shell calls it only while config.json lacks the marker
    /// <see cref="CouchHud.Core.Config.AppConfig.PluginTrustAdopted"/>, and sets it as soon as the list exists): plugins that are already in the
    /// user folder were put there by the user in earlier versions, so they count as approved (local, no capabilities recorded). From then on
    /// only what CouchHud installs or the user approves is trusted, and a list that goes missing later adopts nothing.
    /// </summary>
    public bool AdoptExisting(string pluginsDir)
    {
        lock (Lock)
        {
            // Only the very first time: a list that exists (even a damaged one) is never replaced by what lies there now.
            if (File.Exists(StoreFile)) return false;
            // Without the secret nothing is written (it could not be signed): nothing unknown loads until it can be read again.
            if (Key is null)
            {
                Console.Error.WriteLine("[plugins] no key for the list of approved plugins, nothing adopted");
                return false;
            }
            var all = new Dictionary<string, PluginTrustEntry>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(pluginsDir))
            {
                foreach (var dir in Directory.EnumerateDirectories(pluginsDir))
                {
                    var folder = Path.GetFileName(dir);
                    if (folder.StartsWith(PackageInstaller.TempPrefix, StringComparison.Ordinal)) continue;
                    try
                    {
                        all[folder] = new PluginTrustEntry { Hash = HashFolder(dir) };
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"[plugins] {folder}: not adopted ({ex.Message})");
                    }
                }
            }
            Write(new State(all, new(StringComparer.OrdinalIgnoreCase)));
        }
        return true;
    }

    internal void Forget(string folder)
    {
        lock (Lock)
        {
            if (TryRead(out var state) && state.Plugins.Remove(folder) | state.Pending.Remove(folder) | state.Removed.Remove(folder)) Write(state);
        }
    }

    /// <summary>The list can be read and verified right now. When it cannot (damaged, locked, no key), what it says about origins and removals is unknown, not "nothing".</summary>
    internal bool Readable => TryRead(out _);

    /// <summary>
    /// The approved plugins. False when the file is there but damaged or its signature does not fit (not written by CouchHud,
    /// changed, or from before the signature existed); a missing file is an empty list. A valid format 1 list is migrated (and written
    /// back as format 2) with every folder local and no capabilities recorded.
    /// </summary>
    private bool TryRead(out State state)
    {
        state = new State(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        try
        {
            if (!File.Exists(StoreFile)) return true;
            // Without the secret an existing list cannot be checked: it counts as damaged (never read unsigned).
            if (Key is not { } k)
            {
                Console.Error.WriteLine("[plugins] no key for the list of approved plugins, nothing unknown loads");
                return false;
            }
            var text = File.ReadAllText(StoreFile);
            if (JsonNode.Parse(text) is not JsonObject root) return false;
            if (root["Format"] is null) return ReadV1(text, k, out state);
            var store = JsonSerializer.Deserialize<StoreV2>(text, Json);
            if (store is null || store.Format != 2 || store.Plugins is null || store.Pending is null) return false;
            if (HasCaseDuplicates(store.Plugins.Keys) || HasCaseDuplicates(store.Pending.Keys))
            {
                Console.Error.WriteLine("[plugins] the list of approved plugins names a folder twice, nothing unknown loads");
                return false;
            }
            var read = new State(new(store.Plugins, StringComparer.OrdinalIgnoreCase), new(store.Pending, StringComparer.OrdinalIgnoreCase))
            {
                Removed = new(store.Removed ?? [], StringComparer.OrdinalIgnoreCase)
            };
            if (read.Plugins.Values.Concat(read.Pending.Values).Any(e => e is null || string.IsNullOrEmpty(e.Hash))) return false;
            if (store.Mac is not { Length: > 0 } mac
                    || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(mac), System.Text.Encoding.UTF8.GetBytes(MacV2(k, read))))
            {
                Console.Error.WriteLine("[plugins] the list of approved plugins does not carry a valid signature, nothing unknown loads");
                return false;
            }
            state = read;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            Console.Error.WriteLine($"[plugins] the list of approved plugins is unreadable ({ex.Message}), nothing unknown loads");
            return false;
        }
    }

    private sealed class StoreV1
    {
        public Dictionary<string, string>? Entries { get; set; }
        public string? Mac { get; set; }
    }

    private bool ReadV1(string text, byte[] k, out State state)
    {
        state = new State(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
        var v1 = JsonSerializer.Deserialize<StoreV1>(text);
        if (v1?.Entries is null || HasCaseDuplicates(v1.Entries.Keys)) return false;
        if (v1.Mac is not { Length: > 0 } mac
                || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.UTF8.GetBytes(mac), System.Text.Encoding.UTF8.GetBytes(MacV1(k, v1.Entries))))
        {
            Console.Error.WriteLine("[plugins] the list of approved plugins does not carry a valid signature, nothing unknown loads");
            return false;
        }
        // Nothing new: every folder local, no origin, no key, no capabilities recorded (= the SDK 2 surface it always had).
        foreach (var (folder, hash) in v1.Entries) state.Plugins[folder] = new PluginTrustEntry { Hash = hash };
        try
        {
            lock (Lock) Write(state);
            Console.WriteLine("[plugins] list of approved plugins migrated to format 2");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Read as migrated anyway; written next time.
            Console.Error.WriteLine($"[plugins] migration not written: {ex.Message}");
        }
        return true;
    }

    private void Write(State state)
    {
        // Never an unsigned list: without the secret nothing is written.
        var k = Key ?? throw new InvalidOperationException(CouchHud.Core.Localization.Strings.Get("plugin.trustFault"));
        var store = new StoreV2
        {
            Plugins = new Dictionary<string, PluginTrustEntry>(state.Plugins, StringComparer.OrdinalIgnoreCase),
            Pending = new Dictionary<string, PluginTrustEntry>(state.Pending, StringComparer.OrdinalIgnoreCase),
            Removed = state.Removed.Count == 0 ? null : [.. state.Removed.Order(StringComparer.OrdinalIgnoreCase)],
            Mac = MacV2(k, state)
        };
        // Written through to the disk, so a reset does not leave zero bytes (that would only be a fault: nothing unknown loads). No .bak on
        // purpose: an older list could still approve a plugin that was taken back since.
        CouchHud.Core.Files.DurableFile.WriteAllText(StoreFile, JsonSerializer.Serialize(store, Json), keepBackup: false);
    }
}
