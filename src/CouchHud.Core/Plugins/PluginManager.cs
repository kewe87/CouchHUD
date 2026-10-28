using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Assistant;
using CouchHud.Core.Config;
using CouchHud.Sdk;
using CouchHud.Core.Library;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Plugins;

/// <summary>
/// plugin.json; everything optional. <c>description</c> is a text, or an object per language: <c>{ "de": "…", "en": "…" }</c>.
/// SDK 3 adds <c>id</c>, <c>capabilities</c> (what the plugin needs; null for older plugins, which keep the SDK 2 surface) and <c>license</c>.
/// For a signed package the statement counts, not this file.
/// </summary>
public sealed record PluginManifest(string? Name, string? Version, string? Author, LocalizedText? Description, string? Homepage, int Sdk = 1,
    string? Id = null, IReadOnlyList<string>? Capabilities = null, string? License = null);

/// <summary>A text that exists in several languages. In JSON a plain string (all languages) or { "de": …, "en": … }.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(LocalizedTextConverter))]
public sealed record LocalizedText(IReadOnlyDictionary<string, string> Values)
{
    public static LocalizedText Plain(string text) => new(new Dictionary<string, string> { ["*"] = text });

    /// <summary>The text in <paramref name="language"/>; falls back to the other language, then to any.</summary>
    public string? For(string language)
    {
        if (Values.TryGetValue(language, out var t) && !string.IsNullOrWhiteSpace(t)) return t.Trim();
        if (Values.TryGetValue("*", out t) && !string.IsNullOrWhiteSpace(t)) return t.Trim();
        var other = language == "de" ? "en" : "de";
        if (Values.TryGetValue(other, out t) && !string.IsNullOrWhiteSpace(t)) return t.Trim();
        return Values.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
    }
}

internal sealed class LocalizedTextConverter : System.Text.Json.Serialization.JsonConverter<LocalizedText>
{
    public override LocalizedText? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return reader.GetString() is { } s ? LocalizedText.Plain(s) : null;
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("description: text or { \"de\": …, \"en\": … }");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var key = reader.GetString() ?? "";
            reader.Read();
            if (reader.TokenType == JsonTokenType.String) values[key] = reader.GetString() ?? "";
            else reader.Skip();
        }
        return new LocalizedText(values);
    }

    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value.Values, options);
}

/// <summary>A plugin folder as the settings show it, loaded or not.</summary>
public sealed record PluginInfo(
    string Folder,
    string Id,
    string Name,
    string? Version,
    string? Author,
    string? Description,
    string? Homepage,
    bool Enabled,
    bool Loaded,
    string? Error,
    bool UserInstalled)
{
    /// <summary>The description in every language the plugin.json gave; <see cref="Description"/> is the one for the active language.</summary>
    public LocalizedText? DescriptionText { get; init; }

    /// <summary>Not loaded: nobody installed this folder through CouchHud, or its files changed. Needs the user's approval.</summary>
    public bool NeedsApproval { get; init; }

    /// <summary>The code shown with the approval question; it changes with the files.</summary>
    public string? ApprovalCode { get; init; }

    /// <summary>The whole hash behind <see cref="ApprovalCode"/>: the approval sends it back and holds only for exactly these files.</summary>
    public string? ApprovalHash { get; init; }

    /// <summary>True if the folder was installed before and its files are not the installed ones any more.</summary>
    public bool Changed { get; init; }

    // ---- SDK 3 ----

    /// <summary>Where it comes from: CouchHUD's own (signed or shipped), a third-party developer ("Fremdentwickler"), or local.</summary>
    public PluginOrigin Origin { get; init; } = PluginOrigin.Local;

    /// <summary>The pinned key of a signed package (lower-case hex SHA-256), for the details.</summary>
    public string? KeyFingerprint { get; init; }

    /// <summary>What it may use (granted, not merely declared).</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    /// <summary>Withdrawn by CouchHUD's signed catalog: not loaded until the parents accept it for exactly these files.</summary>
    public bool Revoked { get; init; }

    public string? RevokedReason { get; init; }
}

/// <summary>
/// Loads plugins from plugins/&lt;name&gt;/ folders. Each folder is one plugin with its own
/// load context, so plugins can ship their own dependency versions; only the SDK
/// assembly is shared with the host. The entry assembly is &lt;name&gt;.dll; other
/// DLLs in the folder are dependencies and are resolved on demand.
/// A broken plugin is reported, never fatal, and every call into it has a timeout.
/// </summary>
public sealed class PluginManager(IReadOnlyList<(string Dir, bool UserInstalled)> pluginDirs, string dataDir, Func<string, bool> isEnabled)
{
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    private readonly List<PluginInfo> _infos = [];
    private readonly List<ILibraryProvider> _providers = [];
    private readonly List<(string Plugin, PluginPage Page)> _pages = [];
    private readonly List<(string Plugin, PluginTab Tab)> _tabs = [];
    private readonly List<(string Plugin, QuickAction Action)> _actions = [];
    private readonly List<(string Plugin, PluginSetting Setting)> _settings = [];
    private readonly List<(string Plugin, PluginTool Tool, ToolSpec Spec)> _tools = [];
    private readonly List<(string Plugin, PluginFunction Function)> _functions = [];
    private readonly Dictionary<string, SettingsStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    public PluginEvents Events { get; } = new();

    /// <summary>Hashes of installed plugins; a user plugin that is not (or no longer) in it waits for approval. Null: no check (tests, tools).</summary>
    public PluginTrust? Trust { get; init; }

    /// <summary>Asked by plugins (<see cref="IPluginContext.Locked"/>): is the couch locked right now?</summary>
    public Func<bool>? IsLocked { get; set; }

    /// <summary>Set by the host: is a child's profile active right now? Asked on every decision about a tool (a tool cannot ask for a code).</summary>
    public Func<bool>? IsChildProfile { get; set; }

    /// <summary>Set by the host: (plugin name, title, body).</summary>
    public Action<string, string, string>? Notifier { get; set; }

    /// <summary>Set by the host: opens an http(s) address in the couch browser (plugin name, address); false when refused.</summary>
    public Func<string, string, bool>? UrlOpener { get; set; }

    // ---- SDK 3 host services ----

    /// <summary>Restricted programs, startup adapters and the command-line checks.</summary>
    public PluginHostServices Host { get; } = new();

    /// <summary>The downloads page (plugin lines go there as <see cref="DownloadKind.Plugin"/>); null: plugins get no downloads.</summary>
    public DownloadCenter? DownloadCenter { get; set; }

    public IPluginNetwork Network { get; set; } = new PluginNetworkState();

    /// <summary>Set by the shell: runs a checked, confirmed program (plugin name, canonical exe, arguments, timeout).</summary>
    public Func<string, string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<PluginCliResult>>? CliRunner { get; set; }

    /// <summary>Set by the shell: hands a setup to Windows after its own checks (plugin id, plugin name, path, sha256).</summary>
    public Func<string, string, string, string, CancellationToken, Task<bool>>? SetupRunner { get; set; }

    /// <summary>
    /// Set by the shell: encrypts a plugin's secret for the Windows user (plugin id, plain value, stored text) and decrypts it again
    /// (plugin id, stored text, plain value; empty when it cannot). The plugin id is part of the encryption, so a stored value of one
    /// plugin means nothing to another. Without it secrets are not kept at all (<see cref="IPluginSettings.SetSecret"/> stores nothing).
    /// </summary>
    public Func<string, string, string>? ProtectSecret { get; set; }

    /// <inheritdoc cref="ProtectSecret"/>
    public Func<string, string, string>? UnprotectSecret { get; set; }

    /// <summary>
    /// Set by the shell, for plugins with the capability "browser.signin": opens a sign-in page in the couch browser and hands the redirect
    /// back (plugin id, plugin name, page address, redirect host, receiver); false when refused. See <see cref="IPluginSignIn"/>.
    /// </summary>
    public Func<string, string, string, string, Action<string, string>, bool>? SignInOpener { get; set; }

    /// <summary>Set by the shell: ends the sign-in of a plugin (plugin id).</summary>
    public Action<string>? SignInCanceller { get; set; }

    /// <summary>A plugin said a page's rows changed (plugin id, page id).</summary>
    public event Action<string, string>? PageInvalidated;

    /// <summary>A plugin registered a tab, or the plugins finished loading: the tab bar asks again (<see cref="ResolveTabs"/>).</summary>
    public event Action? TabsChanged;

    /// <summary>
    /// The plugin tabs to show now (see <see cref="PluginTabRules.Resolve"/>): only plugins that are loaded and hold the "tabs" capability,
    /// only for pages that exist, with the area switches and the profile applied.
    /// </summary>
    public IReadOnlyList<PluginTabView> ResolveTabs(AreaSet areas, bool childProfile)
    {
        List<PluginTabRules.Registered> registered;
        HashSet<string> loaded;
        lock (_gate)
        {
            registered = _tabs.Where(t => _grants.ContainsKey(t.Plugin))
                .Select(t => new PluginTabRules.Registered(t.Plugin, _grants[t.Plugin].Origin, _grants[t.Plugin].Capabilities, t.Tab)).ToList();
            loaded = _infos.Where(i => i.Loaded).Select(i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        // The state now, not the one at load time: a plugin that was switched off or uninstalled may stay loaded until the restart, but its
        // tab goes at once (the host raises TabsChanged on those changes).
        bool Running(string plugin)
        {
            if (!loaded.Contains(plugin) || GrantOf(plugin) is not { } grant) return false;
            return isEnabled(grant.Folder) && Trust?.IsRemoved(grant.Folder) != true;
        }
        return PluginTabRules.Resolve(registered, Running, (plugin, page) => FindPage(plugin, page) is not null, Strings.Language, areas, childProfile, locked: IsLocked?.Invoke() ?? false, levelOf: LevelOf);
    }

    /// <summary>A plugin was switched on or off, approved, accepted, installed or uninstalled: the tab bar asks again.</summary>
    public void RaiseTabsChanged() => TabsChanged?.Invoke();

    /// <summary>The revocation list of CouchHUD's signed catalog (from its cache), read before the plugins load.</summary>
    public IReadOnlyList<CatalogRevocation> Revocations { get; set; } = [];

    /// <summary>Settings a plugin starts with when it has none yet (plugin id → key → value), e.g. what CouchHUD kept for CaseHUD before it was a plugin.</summary>
    public Func<string, IReadOnlyDictionary<string, string?>?>? SettingSeeds { get; set; }

    /// <summary>A seed went into the plugin's settings (plugin id): the host drops it so it is used only once.</summary>
    public Action<string>? SeedApplied { get; set; }

    private readonly Dictionary<string, PluginGrant> _grants = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a loaded plugin got: its origin and the capabilities it may use.</summary>
    public sealed record PluginGrant(string Folder, PluginOrigin Origin, IReadOnlySet<string> Capabilities);

    /// <summary>The grant of a loaded plugin (by id); null when it is not loaded.</summary>
    public PluginGrant? GrantOf(string pluginId)
    {
        lock (_gate) return _grants.GetValueOrDefault(pluginId);
    }

    /// <summary>The origin of a loaded plugin; a plugin that is not loaded counts as local (the strictest rules for a page that is not its own).</summary>
    public PluginOrigin OriginOf(string pluginId) => GrantOf(pluginId)?.Origin ?? PluginOrigin.Local;

    // Plugins may load on a background thread while the UI is already up: lists are only touched under this lock.
    private readonly object _gate = new();
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when all plugins are loaded (or failed to).</summary>
    public Task Loaded => _loaded.Task;

    public IReadOnlyList<PluginInfo> Plugins => Snapshot(_infos)
        .Select(i => i.DescriptionText is { } d ? i with { Description = d.For(Strings.Language) } : i).ToList();
    public IReadOnlyList<ILibraryProvider> LibraryProviders => Snapshot(_providers);
    public IReadOnlyList<(string Plugin, PluginPage Page)> Pages => Snapshot(_pages);
    public IReadOnlyList<(string Plugin, QuickAction Action)> QuickActions => Snapshot(_actions);
    public IReadOnlyList<(string Plugin, PluginSetting Setting)> Settings => Snapshot(_settings);

    /// <summary>The functions the plugins declared (<see cref="IPluginContext.RegisterFunctions"/>).</summary>
    public IReadOnlyList<(string Plugin, PluginFunction Function)> Functions => Snapshot(_functions);

    /// <summary>Set by the host: the level the owner stored for a function (plugin id, function id) as its text, or null. Asked on every decision.</summary>
    public Func<string, string, string?>? StoredLevel { get; set; }

    /// <summary>
    /// The level that applies to a function of a plugin now (<see cref="PluginFunctionRules.Effective"/>): null for an untagged item (no function id),
    /// admin for an id the plugin never declared (fail closed), the owner's stored choice otherwise, else the plugin's suggestion (own plugins only
    /// above admin). A damaged store counts as nothing stored.
    /// </summary>
    public PluginLevel? LevelOf(string pluginId, string? functionId)
    {
        if (string.IsNullOrEmpty(functionId)) return null;
        PluginFunction? declared;
        lock (_gate) declared = _functions.FirstOrDefault(f => f.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase) && f.Function.Id == functionId).Function;
        string? stored = null;
        if (declared is null) LogUndeclared(pluginId, functionId);
        if (declared is not null)
        {
            try { stored = StoredLevel?.Invoke(pluginId, functionId); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { stored = null; }
        }
        return PluginFunctionRules.Effective(declared, stored, PluginPolicy.IsOwn(OriginOf(pluginId)));
    }

    private static readonly CouchHud.Core.Guard.LogThrottle UndeclaredLog = new(TimeSpan.FromMinutes(10));

    /// <summary>An item names a function the plugin never declared: it counts as admin only; said once in a while, with the text cleaned (it comes from the plugin).</summary>
    private static void LogUndeclared(string pluginId, string functionId)
    {
        var clean = new string(functionId.Where(c => !char.IsControl(c)).Take(40).ToArray());
        var line = $"[plugins] {pluginId}: an item names the function '{clean}' that the plugin never declared (treated as admin only)";
        if (UndeclaredLog.Allow(line, DateTime.UtcNow, out _)) Console.Error.WriteLine(line);
    }

    /// <summary>The ids as the plugin declared them (the stored levels are kept under these); false when the plugin does not declare the function.</summary>
    public bool TryCanonicalFunction(string pluginId, string functionId, out string canonicalPlugin, out string canonicalFunction)
    {
        foreach (var (plugin, function) in Functions)
        {
            if (!plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase) || !function.Id.Equals(functionId, StringComparison.OrdinalIgnoreCase)) continue;
            canonicalPlugin = plugin;
            canonicalFunction = function.Id;
            return true;
        }
        canonicalPlugin = canonicalFunction = "";
        return false;
    }

    /// <summary>
    /// Every id a plugin folder may be known by: the folder name (what a plugin that did not load is listed under), the id in its plugin.json,
    /// the id in the trust list, and the id of a loaded one. A stored choice for a plugin is kept under its real id, which is none of these alone.
    /// </summary>
    public IReadOnlySet<string> PluginIdsOf(string folder)
    {
        // Without a readable trust list the origin of the folder is unknown and reserved ids cannot be told from CouchHUD's own plugin. This lookup
        // does not throw; whoever decides to forget something on its basis goes through InstalledPluginIds / PluginIdsToForget (RequireReadableTrust).
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { folder };
        if (Trust?.Entry(folder)?.PluginId is { Length: > 0 } trusted) ids.Add(trusted);
        foreach (var (dir, _) in pluginDirs)
        {
            try
            {
                if (ReadManifest(Path.Combine(dir, folder))?.Id is { Length: > 0 } manifestId) ids.Add(manifestId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A folder that cannot be read keeps the ids it has.
            }
        }
        foreach (var info in Snapshot(_infos).Where(i => i.Folder.Equals(folder, StringComparison.OrdinalIgnoreCase))) ids.Add(info.Id);
        // Only CouchHUD's own plugins may be known by a reserved id: a folder of anyone else that claims "homeassistant" (by its name or its
        // plugin.json) neither keeps the owner's levels for the real one alive nor wipes them when it is removed.
        if (Trust?.Entry(folder)?.Origin != PluginOrigin.FirstParty) ids.RemoveWhere(id => PluginNames.IsReserved(id));
        return ids;
    }

    /// <summary>
    /// Fail-safe for everything that forgets a stored choice: when the trust list cannot be read (damaged, locked, tampered with) the origins and the
    /// removal marks are unknown, so the plugin ids cannot be judged and nothing may be forgotten on that basis. Throws then; the caller keeps all choices.
    /// </summary>
    private void RequireReadableTrust()
    {
        if (Trust is { Readable: false }) throw new IOException("the list of approved plugins cannot be read");
        // A deleted list reads as an empty one, which is right for a fresh install and wrong for plugins that are already there: with plugin folders
        // on the disk and no list at all, the origins (CouchHUD's own plugins) are unknown, so nothing is judged gone on that basis.
        if (Trust is { HasStore: false } && AnyPluginFolder()) throw new IOException("the list of approved plugins is missing");
    }

    private bool AnyPluginFolder()
    {
        foreach (var (dir, _) in pluginDirs)
        {
            try
            {
                if (Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any()) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return true;
            }
        }
        return false;
    }

    private IEnumerable<(string Folder, IReadOnlySet<string> Ids)> InstalledFolders()
    {
        RequireReadableTrust();
        foreach (var (dir, _) in pluginDirs)
        {
            string[] folders;
            try
            {
                folders = Directory.Exists(dir) ? Directory.GetDirectories(dir) : [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable: nothing is judged gone (the caller keeps everything it cannot name).
                throw new IOException("plugin folders unreadable", ex);
            }
            foreach (var path in folders)
            {
                var folder = Path.GetFileName(path);
                if (Trust?.IsRemoved(folder) == true) continue;
                yield return (folder, PluginIdsOf(folder));
            }
        }
    }

    /// <summary>
    /// The ids to forget when this folder is uninstalled: every id it was known by, except those another installed folder also holds (two folders
    /// that claim one id must not wipe the other's choices).
    /// </summary>
    public IReadOnlySet<string> PluginIdsToForget(string folder)
    {
        RequireReadableTrust();
        var ids = new HashSet<string>(PluginIdsOf(folder), StringComparer.OrdinalIgnoreCase);
        foreach (var (other, otherIds) in InstalledFolders())
            if (!other.Equals(folder, StringComparison.OrdinalIgnoreCase)) ids.ExceptWith(otherIds);
        return ids;
    }

    /// <summary>
    /// The ids of every plugin that is still installed: a folder is there and is not marked for removal. Whether it loaded, is switched off,
    /// waits for approval or is withdrawn does not matter: the owner's choices for it stay until it is really gone.
    /// </summary>
    public IReadOnlySet<string> InstalledPluginIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, folderIds) in InstalledFolders()) ids.UnionWith(folderIds);
        return ids;
    }

    /// <summary>Does the plugin declare this function?</summary>
    public bool HasFunction(string pluginId, string functionId) => Functions.Any(f => f.Plugin.Equals(pluginId, StringComparison.OrdinalIgnoreCase) && f.Function.Id == functionId);

    /// <summary>Plugin tools as the assistant sees them, names prefixed with the plugin id.</summary>
    public IReadOnlyList<ToolSpec> ToolSpecs => Snapshot(_tools).Select(t => ToolSpecNow(t.Plugin, t.Tool, t.Spec)).Where(s => s is not null).Select(s => s!).ToList();

    /// <summary>A tool tagged with a function: off hides it; restricted lets it run on the locked couch; admin keeps it off the locked couch. Untagged: as registered.</summary>
    private ToolSpec? ToolSpecNow(string plugin, PluginTool tool, ToolSpec spec)
    {
        if (LevelOf(plugin, tool.Function) is not { } level) return spec;
        // A tool cannot be asked for a code: admin in a child's profile is refused like the locked couch, off is gone for everybody.
        if (PluginFunctionRules.Refusal(level, IsLocked?.Invoke() ?? false, IsChildProfile?.Invoke() ?? false) is not null) return null;
        return spec with { AllowedWhileLocked = level == PluginLevel.Restricted };
    }

    private List<T> Snapshot<T>(List<T> list)
    {
        lock (_gate) return [.. list];
    }

    private void Add<T>(List<T> list, T item)
    {
        lock (_gate) list.Add(item);
    }

    private void Remove<T>(List<T> list, Predicate<T> match)
    {
        lock (_gate) list.RemoveAll(match);
    }

    /// <summary>Puts the item where the one it replaces was (same place in the lists), or adds it.</summary>
    private void Replace<T>(List<T> list, T item, Predicate<T> same)
    {
        lock (_gate)
        {
            var i = list.FindIndex(same);
            if (i >= 0) list[i] = item;
            else list.Add(item);
        }
    }

    /// <summary>Loads on a worker thread, so a slow first start (disk, virus scanner) never keeps the HUD from showing.</summary>
    public Task StartLoading() => Task.Run(LoadAll);

    public void LoadAll()
    {
        try
        {
            LoadAllCore();
        }
        finally
        {
            _loaded.TrySetResult();
            TabsChanged?.Invoke();
        }
    }

    private void LoadAllCore()
    {
        foreach (var (pluginsDir, userInstalled) in pluginDirs)
        {
            if (!Directory.Exists(pluginsDir)) continue;
            if (!userInstalled)
            {
                // Since 0.6 nothing is shipped next to the exe: every plugin, CouchHUD's own included, comes from the signed catalog and is
                // pinned. A folder there (left by an older setup, or put there by something else) is never loaded.
                Console.Error.WriteLine($"[plugins] {pluginsDir}: bundled plugins are not loaded any more (install them from the catalog)");
                continue;
            }
            foreach (var dir in Directory.EnumerateDirectories(pluginsDir))
            {
                var folder = Path.GetFileName(dir);
                // Leftovers of an interrupted install (.couchhud-new-…) are not plugins.
                if (folder.StartsWith(PackageInstaller.TempPrefix, StringComparison.Ordinal)) continue;
                LoadFolder(dir, folder);
            }
        }
    }

    private void LoadFolder(string dir, string folder)
    {
        // Removed by the user while it was loaded: it goes at this start (or the next one) and is never offered for approval.
        if (Trust?.IsRemoved(folder) == true)
        {
            Console.Error.WriteLine($"[plugins] {folder}: removed, not loaded");
            return;
        }
        var manifest = ReadManifest(dir);
        var name = manifest?.Name ?? folder;
        PluginTrustEntry? entry = null;
        if (Trust is not null)
        {
            var trust = Trust.Check(folder, dir);
            if (trust != PluginTrustState.Trusted)
            {
                // Dropped into the folder by something else, or changed since: not run until the user says so.
                Console.Error.WriteLine($"[plugins] {folder}: {trust}, waiting for approval");
                if (trust == PluginTrustState.Fault)
                {
                    // The list itself cannot be trusted: nothing to approve against until it is repaired or removed.
                    Add(_infos, Info(folder, folder, name, manifest, enabled: true, loaded: false, Strings.Get("plugin.trustFault"), true));
                    return;
                }
                var shown = SafeHash(dir);
                var code = PluginTrust.ShortCode(shown);
                Add(_infos, Info(folder, folder, name, manifest, enabled: true, loaded: false, Strings.Get(trust == PluginTrustState.Changed ? "plugin.changed" : "plugin.unknown"), true)
                    with { NeedsApproval = true, ApprovalCode = code, ApprovalHash = shown.Length == 64 ? shown : null, Changed = trust == PluginTrustState.Changed, Origin = Trust.Entry(folder)?.Origin ?? PluginOrigin.Local });
                return;
            }
            entry = Trust.Entry(folder);
        }

        var origin = entry?.Origin ?? PluginOrigin.Local;
        var granted = PluginPolicy.Granted(origin, manifest?.Capabilities, manifest?.Sdk ?? 1, entry?.Capabilities);
        var info = Info(folder, folder, name, manifest, enabled: true, loaded: false, null, true) with
        {
            Origin = origin,
            KeyFingerprint = entry?.KeyFingerprint,
            Capabilities = [.. granted.Order(StringComparer.Ordinal)]
        };

        // Withdrawn by CouchHUD's signed catalog: only the parents can let exactly these files run again.
        var id = entry?.PluginId ?? manifest?.Id ?? folder;
        var version = entry?.Version ?? manifest?.Version;
        if (Revocations.FirstOrDefault(r => r.Matches(id, version, entry?.KeyFingerprint)) is { } revoked
            && (entry?.RevocationAccepted is null || entry.RevocationAccepted != entry.Hash))
        {
            Console.Error.WriteLine($"[plugins] {folder}: revoked ({revoked.Reason})");
            Add(_infos, info with { Error = Strings.Get("plugin.revoked", name), Revoked = true, RevokedReason = revoked.Reason });
            return;
        }

        if (!isEnabled(folder))
        {
            Add(_infos, info with { Enabled = false });
            return;
        }
        try
        {
            if (manifest is not null && manifest.Sdk > SdkVersion.Current)
            {
                throw new InvalidOperationException(Strings.Get("plugin.needsNewer", manifest.Sdk));
            }
            var dll = FindEntryAssembly(dir) ?? throw new InvalidOperationException(Strings.Get("plugin.dllMissing", folder));
            foreach (var loadedId in LoadAssembly(dll, name, new PluginGrant(folder, origin, granted), entry?.PluginId))
            {
                Add(_infos, info with { Id = loadedId, Loaded = true });
            }
        }
        catch (Exception ex)
        {
            // A broken plugin must never keep the shell from starting.
            Console.Error.WriteLine($"[plugins] {dir}: {ex.Message}");
            Add(_infos, info with { Error = ex.Message });
        }
    }

    private static string SafeHash(string dir)
    {
        try
        {
            return PluginTrust.HashFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private string UserFolder(string folder)
    {
        if (folder.Length == 0 || folder.Trim('.').Length == 0 || folder.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || folder.StartsWith(PackageInstaller.TempPrefix, StringComparison.Ordinal)) throw new ArgumentException(Strings.Get("plugin.pageNotFound"));
        return pluginDirs.Where(d => d.UserInstalled).Select(d => Path.Combine(d.Dir, folder)).FirstOrDefault(Directory.Exists)
               ?? throw new ArgumentException(Strings.Get("plugin.pageNotFound"));
    }

    /// <summary>
    /// The user said yes to this folder (the host's gate: expert mode and the parents' code): its files are remembered as they are now, as a
    /// local plugin with the capabilities its plugin.json declares (an SDK 2 plugin: the SDK 2 surface), and it loads at the next start.
    /// Never more than a local plugin may have: no first-party capability, whatever the file says. A plugin that was pinned to a key loses
    /// the pin (and with it the updates from its developer) and is local from then on.
    /// The approval holds for exactly the files whose code the user was shown (<paramref name="shownHash"/>, <see cref="PluginInfo.ApprovalHash"/>):
    /// files swapped in between are refused, the code is checked like a local package (<see cref="PluginLint.CheckFolder"/>), and the hash
    /// recorded is the one that was shown, so anything changed afterwards is "changed" again.
    /// </summary>
    public void Approve(string folder, string? shownHash)
    {
        if (Trust is null) return;
        var dir = UserFolder(folder);
        RequireShown(dir, shownHash);
        var manifest = ReadManifest(dir);
        if (PluginPolicy.CheckDeclared(PluginOrigin.Local, manifest?.Capabilities) is { } problem) throw new InvalidOperationException(Strings.Get(problem));
        var caps = manifest?.Capabilities is { } declared ? declared.ToList() : null;
        PluginLint.CheckFolder(dir, PluginOrigin.Local, caps ?? []);
        // The check read the files once more: they must still be the ones that were shown.
        RequireShown(dir, shownHash);
        Trust.Record(folder, shownHash!, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.Local, Capabilities = caps });
    }

    /// <summary>
    /// A package the user installed through the host's gate was just unpacked: the files as they were unpacked (<paramref name="unpackedHash"/>,
    /// taken before they moved into place) are the approved ones, as a local plugin with the capabilities its plugin.json declares.
    /// </summary>
    public void ApproveInstalled(string folder, string unpackedHash)
    {
        if (Trust is null) return;
        var dir = UserFolder(folder);
        var manifest = ReadManifest(dir);
        if (PluginPolicy.CheckDeclared(PluginOrigin.Local, manifest?.Capabilities) is { } problem) throw new InvalidOperationException(Strings.Get(problem));
        var caps = manifest?.Capabilities is { } declared ? declared.ToList() : null;
        PluginLint.CheckFolder(dir, PluginOrigin.Local, caps ?? []);
        RequireShown(dir, unpackedHash);
        Trust.Record(folder, unpackedHash, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.Local, Capabilities = caps });
    }

    private static void RequireShown(string dir, string? shownHash)
    {
        var now = SafeHash(dir);
        if (shownHash is not { Length: 64 } || now.Length != 64
                || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(shownHash.ToLowerInvariant()), System.Text.Encoding.ASCII.GetBytes(now)))
            throw new InvalidOperationException(Strings.Get("plugin.approveMismatch"));
    }

    /// <summary>The parents let a revoked plugin run again, for exactly the files it has now (the host's gate asks for their code every time).</summary>
    public void AcceptRevocation(string folder)
    {
        if (Trust is null) return;
        Trust.AcceptRevocation(folder, UserFolder(folder));
    }

    /// <summary>
    /// The parents confirmed a program whose command line this plugin may run (<see cref="IPluginContext.RunTrustedCliAsync"/>): only an
    /// installed program below Program Files (canonical, no links). Returns the canonical path.
    /// </summary>
    public string ConfirmExe(string folder, string exe)
    {
        if (Trust is null) throw new InvalidOperationException(Strings.Get("plugin.trustFault"));
        UserFolder(folder);
        var canonical = Host.Canonical(exe) ?? throw new ArgumentException(Strings.Get("plugin.exeNotTrusted"));
        Trust.ConfirmExe(folder, canonical);
        return canonical;
    }

    private static PluginInfo Info(string folder, string id, string name, PluginManifest? m, bool enabled, bool loaded, string? error, bool user) =>
        new(folder, id, name, m?.Version, m?.Author, m?.Description?.For(Strings.Language), m?.Homepage, enabled, loaded, error, user) { DescriptionText = m?.Description };

    internal static PluginManifest? ReadManifest(string dir)
    {
        var file = Path.Combine(dir, "plugin.json");
        if (!File.Exists(file)) return null;
        try
        {
            return JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Console.Error.WriteLine($"[plugins] {file}: {ex.Message}");
            return null;
        }
    }

    internal static string? FindEntryAssembly(string dir)
    {
        var byName = Path.Combine(dir, Path.GetFileName(dir) + ".dll");
        if (File.Exists(byName)) return byName;
        // Fallback: the one assembly that comes with a .deps.json (what "dotnet publish" produces).
        var withDeps = Directory.EnumerateFiles(dir, "*.deps.json")
            .Select(d => Path.Combine(dir, Path.GetFileName(d)[..^".deps.json".Length] + ".dll"))
            .Where(File.Exists)
            .ToList();
        return withDeps.Count == 1 ? withDeps[0] : null;
    }

    /// <param name="expectedId">The id in the signed statement: the plugin must answer with exactly that one.</param>
    private List<string> LoadAssembly(string dll, string name, PluginGrant grant, string? expectedId)
    {
        var context = new PluginLoadContext(dll, grant.Origin == PluginOrigin.FirstParty);
        var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(dll));
        var types = assembly.GetExportedTypes()
            .Where(t => typeof(IPlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false });

        var ids = new List<string>();
        foreach (var type in types)
        {
            var plugin = (IPlugin)Activator.CreateInstance(type)!;
            var id = plugin.Id;
            // The id decides whose pages, settings, seeds and tools these are: a plugin cannot take another one's, nor one of CouchHUD's own
            // names unless its statement is signed with CouchHUD's key.
            // The id also names the plugin's data folder: plain characters only (no "..", no separators).
            if (id is null || !System.Text.RegularExpressions.Regex.IsMatch(id, "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$")
                || (expectedId is not null && !id.Equals(expectedId, StringComparison.OrdinalIgnoreCase))
                || (!PluginPolicy.IsOwn(grant.Origin) && PluginNames.IsReserved(id)))
                throw new InvalidOperationException(Strings.Get("plugin.idMismatch", id ?? ""));
            lock (_gate)
            {
                if (_grants.ContainsKey(id)) throw new InvalidOperationException(Strings.Get("plugin.idTaken", id));
                _grants[id] = grant;
            }
            plugin.Initialize(new Context(this, id, name, grant));
            ids.Add(id);
            Console.WriteLine($"[plugins] loaded {plugin.Id} from {Path.GetFileName(dll)}");
        }
        if (ids.Count == 0) throw new InvalidOperationException(Strings.Get("plugin.noIPlugin"));
        return ids;
    }

    public IPluginSettings SettingsOf(string pluginId)
    {
        lock (_stores)
        {
            if (!_stores.TryGetValue(pluginId, out var store))
            {
                // Seeds only for CouchHUD's own plugins (what the core kept for CaseHUD before it became one).
                var seed = PluginPolicy.IsOwn(OriginOf(pluginId)) ? SettingSeeds?.Invoke(pluginId) : null;
                store = new SettingsStore(Path.Combine(PluginDataDir(pluginId), "settings.json"), () => Snapshot(_settings).Where(s => s.Plugin == pluginId).Select(s => s.Setting),
                    seed, () => SeedApplied?.Invoke(pluginId),
                    ProtectSecret is { } protect ? plain => protect(pluginId, plain) : null,
                    UnprotectSecret is { } unprotect ? stored => unprotect(pluginId, stored) : null);
                _stores[pluginId] = store;
            }
            return store;
        }
    }

    private string PluginDataDir(string pluginId)
    {
        var dir = Path.Combine(dataDir, "plugin-data", pluginId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    // -----------------------------------------------------------------------
    // Calls into plugins: always with a timeout, errors become messages.

    public static async Task<T> SafeAsync<T>(string what, Func<CancellationToken, Task<T>> call)
    {
        using var cts = new CancellationTokenSource(CallTimeout);
        try
        {
            return await Task.Run(() => call(cts.Token), cts.Token).WaitAsync(CallTimeout);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(ex is TimeoutException or OperationCanceledException
                ? Strings.Get("plugin.noAnswer", what)
                : $"{what}: {ex.Message}");
        }
    }

    public (PluginPage Page, string Plugin)? FindPage(string pluginId, string pageId)
    {
        if (PluginSettingRules.IsReservedPageId(pageId)) return null;
        var hit = Snapshot(_pages).FirstOrDefault(p => p.Plugin == pluginId && p.Page.Id == pageId);
        return hit.Page is null ? null : (hit.Page, hit.Plugin);
    }

    public QuickAction? FindAction(string pluginId, string actionId) =>
        Snapshot(_actions).FirstOrDefault(a => a.Plugin == pluginId && a.Action.Id == actionId).Action;

    public bool HasTool(string name) => Snapshot(_tools).Any(t => t.Spec.Name == name);

    public ToolSpec? FindTool(string name) => Snapshot(_tools).FirstOrDefault(t => t.Spec.Name == name).Spec;

    public Task<string> CallToolAsync(string name, JsonElement args)
    {
        var tool = Snapshot(_tools).FirstOrDefault(t => t.Spec.Name == name);
        if (tool.Tool is null) throw new ArgumentException(Strings.Get("plugin.unknownTool", name));
        // Off for everyone: refused here too, whatever the caller (assistant, MCP) believes it saw.
        if (LevelOf(tool.Plugin, tool.Tool.Function) is { } toolLevel
            && PluginFunctionRules.Refusal(toolLevel, IsLocked?.Invoke() ?? false, IsChildProfile?.Invoke() ?? false) is { } why)
            throw new ArgumentException(Strings.Get(why));
        return SafeAsync($"Plugin {tool.Plugin}", ct => tool.Tool.Run(args, ct));
    }

    /// <summary>Plugin tool names: "plugin_&lt;plugin&gt;_&lt;tool&gt;" with only [a-z0-9_]. The prefix keeps them apart from the built-in tools.</summary>
    internal static string ToolName(string pluginId, string tool) =>
        new string($"{ToolCatalog.PluginPrefix}{pluginId}_{tool}".ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray());

    // Plugin code that hangs must not pile up threads: one Detail call per action at a time.
    private readonly Dictionary<(string, string), Task<string?>> _details = [];
    public TimeSpan DetailTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The second line of a guide-menu entry, asked from the plugin on a worker thread with a short timeout:
    /// never on the UI thread, and a plugin that does not answer simply has no second line.
    /// </summary>
    public async Task<string?> DetailAsync(string plugin, QuickAction action)
    {
        if (action.Detail is not { } detail) return null;
        Task<string?> call;
        lock (_details)
        {
            if (_details.TryGetValue((plugin, action.Id), out var running) && !running.IsCompleted) return null;
            call = Task.Run(() => detail());
            _details[(plugin, action.Id)] = call;
        }
        try
        {
            return await call.WaitAsync(DetailTimeout);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[plugins] {plugin}: {(ex is TimeoutException ? "detail did not answer" : ex.Message)}");
            return null;
        }
    }

    /// <summary>
    /// What a plugin sees of CouchHUD. Every member checks its capability (<see cref="PluginPolicy.ContextMembers"/>) on every call; a
    /// missing one throws <see cref="PluginCapabilityException"/>. A test makes sure no member of <see cref="IPluginContext"/> is left out.
    /// </summary>
    /// <summary>For tests: the context a plugin with this grant would get (the real one, nothing mocked).</summary>
    internal IPluginContext ContextFor(string pluginId, PluginGrant grant, bool markLoaded = false)
    {
        lock (_gate)
        {
            _grants[pluginId] = grant;
            if (markLoaded) _infos.Add(Info(grant.Folder, pluginId, pluginId, null, enabled: true, loaded: true, null, true));
        }
        return new Context(this, pluginId, pluginId, grant);
    }

    private sealed class Context(PluginManager owner, string pluginId, string pluginName, PluginGrant grant) : IPluginContext
    {
        private IPluginDownloads? _downloads;

        private void Need(string member)
        {
            // A member that is not in the table is refused: a new member cannot slip in without a gate.
            if (!PluginPolicy.ContextMembers.TryGetValue(member, out var cap)) throw new PluginCapabilityException(Strings.Get("plugin.cap.denied", pluginName, member));
            if (cap is not null && !grant.Capabilities.Contains(cap)) throw new PluginCapabilityException(Strings.Get("plugin.cap.denied", pluginName, cap));
        }

        public string DataDirectory
        {
            get
            {
                Need(nameof(DataDirectory));
                return owner.PluginDataDir(pluginId);
            }
        }

        public IPluginSettings Settings
        {
            get
            {
                Need(nameof(Settings));
                return owner.SettingsOf(pluginId);
            }
        }

        public IPluginEvents Events
        {
            get
            {
                Need(nameof(Events));
                return owner.Events;
            }
        }

        public void RegisterLibraryProvider(ILibraryProvider provider)
        {
            Need(nameof(RegisterLibraryProvider));
            owner.Add(owner._providers, provider);
        }

        public void Log(string message)
        {
            Need(nameof(Log));
            Console.WriteLine($"[{pluginId}] {message}");
        }

        public string Language
        {
            get
            {
                Need(nameof(Language));
                return Strings.Language;
            }
        }

        public bool Locked
        {
            get
            {
                Need(nameof(Locked));
                return owner.IsLocked?.Invoke() ?? false;
            }
        }

        // Registering an id again replaces the earlier one (a plugin re-registers its texts when the language changes).
        public void RegisterSettings(params PluginSetting[] settings)
        {
            Need(nameof(RegisterSettings));
            foreach (var s in settings) owner.Replace(owner._settings, (Plugin: pluginId, Setting: s), x => x.Plugin == pluginId && x.Setting.Key == s.Key);
        }

        public void RegisterPage(PluginPage page)
        {
            Need(nameof(RegisterPage));
            // Ids starting with "$" belong to the core (the settings page of a plugin has a visit under "$settings"): a plugin page cannot take one.
            if (PluginSettingRules.IsReservedPageId(page.Id))
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: page not registered (ids starting with '$' are reserved)");
                return;
            }
            owner.Replace(owner._pages, (Plugin: pluginId, Page: page), x => x.Plugin == pluginId && x.Page.Id == page.Id);
        }

        public void RegisterTab(PluginTab tab)
        {
            Need(nameof(RegisterTab));
            // The same checks as when the tabs are listed (PluginTabRules.Resolve), so a refused tab is told to the log at once.
            if (PluginTabRules.Problem(tab, PluginPolicy.IsOwn(grant.Origin)) is { } problem)
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: tab not registered ({problem})");
                return;
            }
            lock (owner._gate)
            {
                var at = owner._tabs.FindIndex(x => x.Plugin == pluginId && x.Tab.Id == tab.Id);
                if (at < 0 && owner._tabs.Count(x => x.Plugin == pluginId) >= PluginTabRules.MaxPerPlugin)
                {
                    Console.Error.WriteLine($"[plugins] {pluginId}: tab {tab.Id} not registered (at most {PluginTabRules.MaxPerPlugin} tabs per plugin)");
                    return;
                }
                if (at >= 0) owner._tabs[at] = (pluginId, tab);
                else owner._tabs.Add((pluginId, tab));
            }
            owner.TabsChanged?.Invoke();
        }

        public void RegisterFunctions(params PluginFunction[] functions)
        {
            Need(nameof(RegisterFunctions));
            foreach (var f in functions ?? [])
            {
                var de = PluginFunctionRules.CleanLabel(f?.LabelDe);
                var en = PluginFunctionRules.CleanLabel(f?.LabelEn);
                if (f is null || !PluginFunctionRules.ValidId(f.Id) || (de is null && en is null))
                {
                    Console.Error.WriteLine($"[plugins] {pluginId}: function not registered (id or label)");
                    continue;
                }
                var clean = f with { LabelDe = de ?? en!, LabelEn = en ?? de! };
                lock (owner._gate)
                {
                    var at = owner._functions.FindIndex(x => x.Plugin == pluginId && x.Function.Id == clean.Id);
                    if (at < 0 && owner._functions.Count(x => x.Plugin == pluginId) >= PluginFunctionRules.MaxPerPlugin)
                    {
                        Console.Error.WriteLine($"[plugins] {pluginId}: function {clean.Id} not registered (at most {PluginFunctionRules.MaxPerPlugin})");
                        continue;
                    }
                    if (at >= 0) owner._functions[at] = (pluginId, clean);
                    else owner._functions.Add((pluginId, clean));
                }
            }
        }

        public void RegisterQuickAction(QuickAction action)
        {
            Need(nameof(RegisterQuickAction));
            owner.Replace(owner._actions, (Plugin: pluginId, Action: action), x => x.Plugin == pluginId && x.Action.Id == action.Id);
        }

        public void RegisterTool(PluginTool tool)
        {
            Need(nameof(RegisterTool));
            JsonObject schema;
            try
            {
                schema = JsonNode.Parse(tool.InputSchema) as JsonObject ?? new JsonObject { ["type"] = "object" };
            }
            catch (JsonException)
            {
                schema = new JsonObject { ["type"] = "object" };
            }
            var name = ToolName(pluginId, tool.Name);
            // A name that is taken (by a built-in tool or another plugin tool) is refused, never replaced.
            if (ToolCatalog.Find(name) is not null || owner.HasTool(name))
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: tool {name} already exists, not registered");
                return;
            }
            // Sensitive: over MCP even a read-only plugin tool is confirmed on the TV. A plugin that is not CouchHUD's own counts as
            // writing, so the TV asks before each of its tools runs, whatever it says about itself.
            owner.Add(owner._tools, (pluginId, tool, new ToolSpec(name, $"[{pluginName}] {tool.Description}", schema, PluginPolicy.ToolWrites(grant.Origin, tool.Writes), Sensitive: true)
            {
                AllowedWhileLocked = PluginPolicy.ToolAllowedWhileLocked(grant.Origin, tool.AllowedWhileLocked)
            }));
        }

        public void RemoveQuickAction(string id)
        {
            Need(nameof(RemoveQuickAction));
            owner.Remove(owner._actions, x => x.Plugin == pluginId && x.Action.Id == id);
        }

        private IPluginSignIn? _signIn;

        public IPluginSignIn? SignIn
        {
            get
            {
                Need(nameof(SignIn));
                return owner.SignInOpener is null ? null : _signIn ??= new PluginSignIn(owner, pluginId, pluginName);
            }
        }

        public void Notify(string title, string body)
        {
            Need(nameof(Notify));
            owner.Notifier?.Invoke(pluginName, title, body);
        }

        public bool OpenUrl(string url)
        {
            Need(nameof(OpenUrl));
            return WebLinks.IsWebUrl(url) && owner.UrlOpener?.Invoke(pluginName, url) == true;
        }

        // ---- SDK 3 ----

        public void Invalidate(string pageId)
        {
            Need(nameof(Invalidate));
            if (owner.FindPage(pluginId, pageId) is null) return;
            owner.PageInvalidated?.Invoke(pluginId, pageId);
        }

        public IPluginDownloads? Downloads
        {
            get
            {
                Need(nameof(Downloads));
                if (owner.DownloadCenter is not { } center) return null;
                return _downloads ??= new PluginDownloads(center, pluginId, pluginName);
            }
        }

        public IPluginNetwork? Network
        {
            get
            {
                Need(nameof(Network));
                return owner.Network;
            }
        }

        public async Task<PluginCliResult> RunTrustedCliAsync(string exePath, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Need(nameof(RunTrustedCliAsync));
            var confirmed = owner.Trust?.Entry(grant.Folder)?.ConfirmedExes ?? [];
            var refused = owner.Host.CheckCli(exePath, arguments ?? [], timeout, grant.Origin, owner.IsLocked?.Invoke() ?? true, confirmed, out var canonical);
            if (refused is not null)
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: command line refused ({refused})");
                return new PluginCliResult(null, "", false, refused);
            }
            if (owner.CliRunner is not { } run) return new PluginCliResult(null, "", false, "unsupported");
            return await run(pluginName, canonical!, [.. arguments!], timeout, cancellationToken);
        }

        public Task<bool> RunSetupAsync(string path, string sha256, CancellationToken cancellationToken)
        {
            Need(nameof(RunSetupAsync));
            return owner.SetupRunner is { } run ? run(pluginId, pluginName, path, sha256, cancellationToken) : Task.FromResult(false);
        }

        public bool RestrictWhileLocked(string exePath)
        {
            Need(nameof(RestrictWhileLocked));
            return owner.Host.Restrict(pluginId, exePath);
        }

        public bool AdaptStartupArgs(string exePath, Func<string, string?> adapt)
        {
            Need(nameof(AdaptStartupArgs));
            return adapt is not null && owner.Host.AddStartupAdapter(pluginId, exePath, adapt);
        }
    }

    /// <summary>
    /// CouchHUD's own assemblies besides the SDK. No plugin gets them, CouchHUD's own included (they need only the SDK): with the host's
    /// types a plugin could reach past its capabilities (the trust store, the services). PluginLint refuses a reference to them before the
    /// install; this is the second wall at load time. Not a sandbox: code in the same process can still use reflection.
    /// </summary>
    internal static readonly IReadOnlySet<string> HostAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CouchHud.Core", "CouchHud.Shell", "CouchHud.Service", "CouchHud.Service.Contracts", "CouchHud.ServiceClient", "CouchHud.Privileged"
    };

    /// <param name="firstParty">CouchHUD's own plugins may share what the host ships (WebView2 and the like); every other plugin gets from
    /// the default context only the SDK and the .NET shared framework, and brings anything else itself.</param>
    internal sealed class PluginLoadContext(string mainAssembly, bool firstParty) : AssemblyLoadContext(isCollectible: false)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        protected override Assembly? Load(AssemblyName name)
        {
            // Share the SDK so IPlugin is the same type on both sides.
            if (string.Equals(name.Name, typeof(IPlugin).Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)) return null;
            // Returning null would hand out the host's copy from the default context: refused instead (also a copy next to the plugin).
            if (name.Name is { } n && HostAssemblies.Contains(n)) throw new FileLoadException(Strings.Get("plugin.hostAssembly", n), n);
            var path = _resolver.ResolveAssemblyToPath(name);
            if (path is not null) return LoadFromAssemblyPath(path);
            // Null falls through to the default context: for anything but CouchHUD's own plugins only the shared framework, never one of
            // the host's own libraries (WebView2, QRCoder, ServiceController, ...).
            if (!firstParty && !SharedFramework.Contains(name.Name)) throw new FileLoadException(Strings.Get("plugin.hostLibrary", name.Name ?? "?"), name.Name);
            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

/// <summary>Raised by the host; a throwing plugin handler is logged and skipped.</summary>
public sealed class PluginEvents : IPluginEvents
{
    public event Action<GameEvent>? GameStarted;
    public event Action<GameEvent>? GameEnded;
    public event Action? PcModeEntered;
    public event Action? CouchModeEntered;
    public event Action<string>? LanguageChanged;
    public event Action<bool>? LockChanged;

    public void RaiseGameStarted(GameEvent e) => Raise(GameStarted, h => h(e));
    public void RaiseGameEnded(GameEvent e) => Raise(GameEnded, h => h(e));
    public void RaisePcModeEntered() => Raise(PcModeEntered, h => h());
    public void RaiseCouchModeEntered() => Raise(CouchModeEntered, h => h());
    public void RaiseLanguageChanged(string language) => Raise(LanguageChanged, h => h(language));
    public void RaiseLockChanged(bool locked) => Raise(LockChanged, h => h(locked));

    private static void Raise<T>(T? handlers, Action<T> invoke) where T : Delegate
    {
        if (handlers is null) return;
        foreach (var h in handlers.GetInvocationList().Cast<T>())
        {
            // Off the caller's thread: a slow plugin must not stall the HUD.
            _ = Task.Run(() =>
            {
                try
                {
                    invoke(h);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[plugins] event handler failed: {ex.Message}");
                }
            });
        }
    }
}

/// <summary>The sign-in of one plugin; the checks of the addresses are the shell's (it knows the browser).</summary>
internal sealed class PluginSignIn(PluginManager owner, string pluginId, string pluginName) : IPluginSignIn
{
    public bool Open(string pageUrl, string redirectHost, Action<string, string> onRedirect) =>
        onRedirect is not null && owner.SignInOpener?.Invoke(pluginId, pluginName, pageUrl, redirectHost, onRedirect) == true;

    public void Cancel() => owner.SignInCanceller?.Invoke(pluginId);
}

/// <summary>A plugin's settings as a small JSON file in its data folder.</summary>
/// <param name="seed">Values to start with when the plugin has no settings file yet; written once, then <paramref name="seedApplied"/> is called.</param>
/// <param name="protect">Encrypts a secret for the file; null: secrets are not kept.</param>
/// <param name="unprotect">Decrypts a stored secret; null: a stored secret reads as empty.</param>
internal sealed class SettingsStore(string file, Func<IEnumerable<PluginSetting>> declared, IReadOnlyDictionary<string, string?>? seed = null, Action? seedApplied = null,
    Func<string, string>? protect = null, Func<string, string>? unprotect = null) : IPluginSettings
{
    /// <summary>A secret in the file starts with this (the shell's DPAPI text); everything else is plain.</summary>
    internal const string SecretPrefix = "dpapi:";

    private readonly object _lock = new();
    private Dictionary<string, string?>? _values;

    public string? Get(string key)
    {
        lock (_lock)
        {
            Load();
            if (_values!.TryGetValue(key, out var v)) return v is not null && v.StartsWith(SecretPrefix, StringComparison.Ordinal) ? unprotect?.Invoke(v) ?? "" : v;
        }
        return declared().FirstOrDefault(s => s.Key == key)?.Default;
    }

    public bool GetBool(string key, bool fallback = false) => Get(key) is { } v ? v.Equals("true", StringComparison.OrdinalIgnoreCase) : fallback;

    /// <summary>A setting the plugin declared as <see cref="SettingKind.Secret"/> is kept encrypted, whichever way it is set (also by the settings page).</summary>
    public void Set(string key, string? value) =>
        Store(key, value, declared().Any(s => s.Key == key && s.Kind == SettingKind.Secret));

    public void SetSecret(string key, string? value) => Store(key, value, true);

    /// <summary>Longest value a plugin may keep (characters), and the largest settings file (bytes). A plugin's settings are small; a runaway one must not fill the disk.</summary>
    internal const int MaxValueLength = 64 * 1024;

    internal const int MaxFileBytes = 1024 * 1024;

    private void Store(string key, string? value, bool secret)
    {
        if (key is null or { Length: 0 or > 200 } || (value is not null && value.Length > MaxValueLength))
        {
            Console.Error.WriteLine($"[plugins] {file}: a setting was refused (too long)");
            return;
        }
        // A secret is never written as plain text: without a protector it is not kept at all. A protector that fails is a refusal,
        // not an exception thrown into the plugin (and the old value stays).
        if (secret && !string.IsNullOrEmpty(value))
        {
            try
            {
                value = protect is null ? null : protect(value);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[plugins] {file}: a secret was not kept ({ex.GetType().Name})");
                return;
            }
            if (value is not null && value.Length > MaxValueLength) return;
        }
        // A plain value that looks like a stored secret would be read as one: it is not kept.
        else if (value is not null && value.StartsWith(SecretPrefix, StringComparison.Ordinal)) value = null;
        lock (_lock)
        {
            Load();
            var next = new Dictionary<string, string?>(_values!) { [key] = value };
            var json = JsonSerializer.Serialize(next);
            if (Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            {
                Console.Error.WriteLine($"[plugins] {file}: the settings are full, a setting was refused");
                return;
            }
            _values = next;
            try
            {
                File.WriteAllText(file, json);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[plugins] {file}: {ex.Message}");
            }
        }
    }

    private void Load()
    {
        if (_values is not null) return;
        if (seed is { Count: > 0 } && !File.Exists(file))
        {
            _values = new Dictionary<string, string?>(seed);
            try
            {
                File.WriteAllText(file, JsonSerializer.Serialize(_values));
                seedApplied?.Invoke();
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"[plugins] {file}: {ex.Message}");
            }
            return;
        }
        try
        {
            _values = File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(file)) ?? new() : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _values = new();
        }
    }
}
