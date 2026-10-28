using System.Text.Json;
using CouchHud.Core.Config;
using CouchHud.Core.Platform;

namespace CouchHud.Core.Plugins;

/// <summary>One plugin of the catalog worth pointing out, and why.</summary>
/// <param name="Reason">"program" (a matching program is installed: <paramref name="Program"/>), "wand" (Wand used to ship with CouchHUD) or "feature" (the household used a feature that is a plugin now: <paramref name="Program"/> names it).</param>
public sealed record PluginSuggestion(string EntryId, string Name, string Reason, string? Program);

/// <summary>
/// Suggests catalog plugins: when a program named in an entry's <c>suggestWhen.uninstallEntry</c> is installed (exact display name of an
/// HKLM uninstall entry), and Wand to those who used it while it shipped with CouchHUD. A suggestion only points at the catalog page: it
/// never installs anything, and installing goes through the same gates as any other. Each entry is offered once per Windows user; on the
/// locked couch nothing is offered (the suggestions wait until the next unlocked moment and are not marked).
/// </summary>
public sealed class PluginSuggestions(string dataDir, IRegistry registry, string windowsUser, Func<string, bool>? usedFeature = null)
{
    /// <summary>The features of the core that became plugins; a catalog entry names one in <c>suggestWhen.feature</c>.</summary>
    public static readonly IReadOnlySet<string> Features = new HashSet<string>(StringComparer.Ordinal) { "homeAssistant" };

    public const string UninstallRoot = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string UninstallRoot32 = @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    public const string WandId = "wand";
    private const int MaxRemembered = 500;

    private readonly object _lock = new();
    private string StoreFile => Path.Combine(dataDir, "plugin-suggestions.json");

    /// <summary>
    /// What may be shown now. Only signed entries of the index (with a statement, or from CouchHUD's own index without one), only those not
    /// installed and not offered to this user before. Locked: nothing (and nothing is marked).
    /// </summary>
    public IReadOnlyList<PluginSuggestion> Due(IReadOnlyList<CatalogEntry> entries, IReadOnlyCollection<string> installedIds, bool locked)
    {
        if (locked || entries.Count == 0) return [];
        var offered = Offered();
        var installed = new HashSet<string>(installedIds, StringComparer.OrdinalIgnoreCase);
        var programs = InstalledPrograms();
        var list = new List<PluginSuggestion>();
        foreach (var entry in entries)
        {
            if (entry.Kind != CatalogKind.Plugin || installed.Contains(entry.Id) || offered.Contains(entry.Id)) continue;
            if (entry.Id.Equals(WandId, StringComparison.OrdinalIgnoreCase) && UsedBundledWand())
            {
                list.Add(new PluginSuggestion(entry.Id, entry.Name, "wand", null));
                continue;
            }
            if (entry.SuggestWhenFeature is { } feature && usedFeature?.Invoke(feature) == true)
            {
                list.Add(new PluginSuggestion(entry.Id, entry.Name, "feature", feature));
                continue;
            }
            if (entry.SuggestWhenUninstallEntry is { } program && NameMatches(programs, program))
                list.Add(new PluginSuggestion(entry.Id, entry.Name, "program", program));
        }
        return list;
    }

    /// <summary>The suggestion was shown: it is not offered to this Windows user again.</summary>
    public void MarkOffered(string entryId)
    {
        lock (_lock)
        {
            var all = ReadAll();
            if (!all.TryGetValue(UserKey, out var ids)) all[UserKey] = ids = [];
            if (ids.Contains(entryId, StringComparer.OrdinalIgnoreCase) || ids.Count >= MaxRemembered) return;
            ids.Add(entryId);
            try
            {
                Directory.CreateDirectory(dataDir);
                Files.DurableFile.WriteAllText(StoreFile, JsonSerializer.Serialize(all), keepBackup: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[plugins] suggestions not saved: {ex.Message}");
            }
        }
    }

    public IReadOnlySet<string> Offered()
    {
        lock (_lock) return ReadAll().TryGetValue(UserKey, out var ids) ? ids.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>();
    }

    /// <summary>Wand ran while it was bundled: its settings folder exists (its id was always "wand").</summary>
    private bool UsedBundledWand() => Directory.Exists(Path.Combine(dataDir, "plugin-data", WandId));

    /// <summary>
    /// Is a program of this name installed? An uninstall entry is called exactly so ("CaseHUD") or the name followed by its version the way installers
    /// write it ("CaseHUD version 1.0.5", "CaseHUD 1.0.5", "CaseHUD v1.0.5"). Another program whose name only begins alike ("CaseHUD Tools") does not count.
    /// </summary>
    internal static bool NameMatches(IEnumerable<string> displayNames, string program)
    {
        foreach (var name in displayNames)
        {
            if (name.Equals(program, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Length <= program.Length + 1 || !name.StartsWith(program + " ", StringComparison.OrdinalIgnoreCase)) continue;
            var rest = name[(program.Length + 1)..];
            if (char.IsAsciiDigit(rest[0]) || rest.StartsWith("version ", StringComparison.OrdinalIgnoreCase) || (rest.Length > 1 && (rest[0] is 'v' or 'V') && char.IsAsciiDigit(rest[1]))) return true;
        }
        return false;
    }

    /// <summary>The display names of the machine-wide uninstall entries (written by administrators; a per-user entry does not count).</summary>
    internal HashSet<string> InstalledPrograms()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { UninstallRoot, UninstallRoot32 })
        {
            foreach (var sub in registry.SubKeys(root))
            {
                if (registry.ReadString(root + "\\" + sub, "DisplayName") is { } name && name.Trim() is { Length: >= 3 and <= 100 } trimmed) names.Add(trimmed);
            }
        }
        return names;
    }

    private string UserKey => windowsUser.ToLowerInvariant();

    private Dictionary<string, List<string>> ReadAll()
    {
        try
        {
            if (!File.Exists(StoreFile)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(StoreFile)) is { } d
                ? new Dictionary<string, List<string>>(d, StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A broken list only means a suggestion may come again.
            return [];
        }
    }
}

/// <summary>
/// The settings CouchHUD kept for CaseHUD while it was part of the core, handed to the CaseHUD plugin as its first settings: the plugin
/// starts where the user left off. Only for the plugin id "casehud", which only a first-party plugin can have (reserved name, checked at
/// load), and only once (the manager writes a seed only when the plugin has no settings yet).
/// </summary>
public static partial class PluginSettingSeeds
{
    public const string CaseHudId = "casehud";
    public const string HomeAssistantId = "homeassistant";
    public const string VpnId = "vpn";

    /// <summary>
    /// Takes a secret the core keeps (its stored text, encrypted for the program) over to the plugin: decrypts it and encrypts it again for
    /// the plugin. Null when that does not work. The result must be the stored text (never the plain value); anything else is dropped.
    /// </summary>
    public delegate string? SecretMover(string pluginId, string storedByCore);

    public static IReadOnlyDictionary<string, string?>? For(AppConfig config, string pluginId, SecretMover? moveSecret = null)
    {
        if (pluginId.Equals(CaseHudId, StringComparison.OrdinalIgnoreCase))
        {
            return new Dictionary<string, string?>
            {
                // Only the game report is a setting of the plugin. The setup options (start with Windows, FPS detection, interface after the
                // installation) are asked by CaseHUD's own setup now, so they are not carried over.
                ["gamingLease"] = Bool(config.CaseHudGamingLease)
            };
        }
            if (pluginId.Equals(VpnId, StringComparison.OrdinalIgnoreCase)) return Vpn(config);
        return pluginId.Equals(HomeAssistantId, StringComparison.OrdinalIgnoreCase) ? HomeAssistant(config, moveSecret) : null;
    }

    /// <summary>
    /// What CouchHUD kept for the VPN while it was part of the core, as the plugin's first settings: the Guide menu switch and the country chosen last
    /// (only a two-letter lower-case id; the plugin checks it against its own list again, anything else is left out). There is no secret: the core never
    /// held a VPN credential.
    /// </summary>
    private static IReadOnlyDictionary<string, string?> Vpn(AppConfig config)
    {
        var seed = new Dictionary<string, string?> { ["inGuide"] = Bool(config.VpnInGuide) };
        if (config.VpnLastCountry is { Length: 2 } last && last.All(char.IsAsciiLetterLower)) seed["lastCountry"] = last;
        return seed;
    }

    /// <summary>The client_id the core's former Home Assistant sign-in used; a refresh token taken over belongs to it (Home Assistant refuses it for another).</summary>
    public const string LegacyHomeAssistantClientId = "https://couchhud.invalid/";

    private const int MaxEntities = 500;

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-z_]+\.[a-z0-9_]+\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex EntityIdPattern();

    /// <summary>Well-formed, distinct entity ids in the given order, cut at 500 (the old config file is untrusted).</summary>
    private static List<string> Sanitize(IEnumerable<string?>? ids)
    {
        var result = new List<string>();
        if (ids is null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (result.Count >= MaxEntities) break;
            if (string.IsNullOrEmpty(id) || id.Length > 255 || !EntityIdPattern().IsMatch(id) || !seen.Add(id)) continue;
            result.Add(id);
        }
        return result;
    }

    /// <summary>The choice in effect: an install from before the picker has its pins as the choice.</summary>
    private static List<string> Effective(IEnumerable<string?>? selection, IEnumerable<string?>? pinned)
    {
        var chosen = Sanitize(selection);
        return chosen.Count > 0 ? chosen : Sanitize(pinned);
    }

    /// <summary>The plugin's secrets are stored with this prefix (the shell's encrypted text); a plain value is never handed over.</summary>
    private const string StoredPrefix = "dpapi:";

    /// <summary>
    /// What CouchHUD kept for Home Assistant while it was part of the core, as the plugin's first settings: the address, the chosen and the
    /// pinned entities, and the sign-in or the long-lived token (encrypted again for the plugin; nothing in plain text, nothing through
    /// the UI). The credentials come along only with a usable address and are bound to it (<c>credentialUrl</c>): the plugin sends them
    /// nowhere else. Null for a household that never set Home Assistant up. The config file is untrusted: a broken address, lists or
    /// token end up as less, never as an error.
    /// </summary>
    private static IReadOnlyDictionary<string, string?>? HomeAssistant(AppConfig config, SecretMover? moveSecret)
    {
        var url = PluginSignInRules.NormalizeBase(config.HomeAssistantUrl);
        // The choice in effect (an install from before the picker has its pins as the choice).
        var entities = Effective(config.HomeAssistantEntities, config.HomeAssistantPinned);
        var pinned = Sanitize(config.HomeAssistantPinned);
        var hasToken = !string.IsNullOrWhiteSpace(config.HomeAssistantToken);
        var hasSession = !string.IsNullOrWhiteSpace(config.HomeAssistantRefreshToken);
        if (url is null && entities.Count == 0 && pinned.Count == 0 && !hasToken && !hasSession && string.IsNullOrWhiteSpace(config.HomeAssistantUrl)) return null;

        var seed = new Dictionary<string, string?>
        {
            ["url"] = url ?? "",
            ["entities"] = JsonSerializer.Serialize(entities),
            ["pinned"] = JsonSerializer.Serialize(pinned.Where(entities.Contains).ToList())
        };
        // Without a usable address nobody knows which server a token belongs to: it stays behind.
        if (url is null || moveSecret is null) return seed;
        seed["credentialUrl"] = url;
        // One credential at a time, as in the core: the sign-in wins over a token that was left behind.
        if (hasSession)
        {
            if (Move(moveSecret, config.HomeAssistantRefreshToken) is { } refresh)
            {
                seed["refreshToken"] = refresh;
                // The refresh token belongs to the client_id it was issued for (the core's); Home Assistant refuses it for another one.
                // The plugin refreshes and revokes a taken-over sign-in with this one; its own new sign-ins use the plugin's.
                seed["clientId"] = LegacyHomeAssistantClientId;
            }
        }
        else if (hasToken && Move(moveSecret, config.HomeAssistantToken) is { } token)
        {
            seed["token"] = token;
        }
        return seed;
    }

    /// <summary>
    /// After a Home Assistant seed that carried a secret was written into the plugin's settings: drops the encrypted legacy token fields so
    /// no second copy stays in config.json. Only when the seed really held the re-encrypted token or sign-in (<c>token</c> / <c>refreshToken</c>);
    /// a seed without them (the move failed, no usable address) leaves the old fields where they are, so a later start can still hand them over.
    /// Returns whether anything was cleared.
    /// </summary>
    public static bool ClearLegacyHomeAssistantSecrets(AppConfig config, IReadOnlyDictionary<string, string?>? writtenSeed)
    {
        if (writtenSeed is null || !(writtenSeed.ContainsKey("token") || writtenSeed.ContainsKey("refreshToken"))) return false;
        var had = config.HomeAssistantToken.Length > 0 || config.HomeAssistantRefreshToken.Length > 0;
        config.HomeAssistantToken = "";
        config.HomeAssistantRefreshToken = "";
        return had;
    }

    private static string? Move(SecretMover move, string stored)
    {
        try
        {
            var moved = move(HomeAssistantId, stored);
            return moved is not null && moved.Length > StoredPrefix.Length && moved.StartsWith(StoredPrefix, StringComparison.Ordinal) && moved != stored ? moved : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[plugins] a Home Assistant secret was not taken over: {ex.GetType().Name}");
            return null;
        }
    }

    private static string Bool(bool b) => b ? "true" : "false";
}
