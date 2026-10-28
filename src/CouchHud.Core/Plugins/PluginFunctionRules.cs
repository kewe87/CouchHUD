using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>
/// The levels of plugin functions as pure decisions. A plugin declares functions (<see cref="PluginFunction"/>) and tags its items with their
/// ids; the owner chooses per function who may use it: admin only, restricted too (locked couch and child profile), nobody. The host applies
/// these rules before it shows or runs anything tagged; the plugin is never asked, and what it tags or suggests can only make things stricter
/// than the owner's choice, never looser (a suggestion above admin only counts for CouchHUD's own plugins).
/// </summary>
public static class PluginFunctionRules
{
    public const int MaxPerPlugin = 12;
    public const int MaxId = 32;
    public const int MaxLabel = 60;

    public const string AdminId = "admin";
    public const string RestrictedId = "restricted";
    public const string OffId = "off";

    /// <summary>Lower case letters, digits, "-" and "_", at most <see cref="MaxId"/>.</summary>
    public static bool ValidId(string? id) =>
        id is { Length: > 0 and <= MaxId } && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>The label with control characters dropped and the length bounded; null when nothing is left.</summary>
    public static string? CleanLabel(string? label)
    {
        if (label is null) return null;
        var clean = new string(label.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > MaxLabel) clean = clean[..MaxLabel];
        return clean.Length == 0 ? null : clean;
    }

    public static string LevelId(PluginLevel level) => level switch
    {
        PluginLevel.Restricted => RestrictedId,
        PluginLevel.Off => OffId,
        _ => AdminId
    };

    /// <summary>The level named by a stored text; null for anything else (a tampered or old value).</summary>
    public static PluginLevel? ParseLevel(string? id) => id switch
    {
        AdminId => PluginLevel.Admin,
        RestrictedId => PluginLevel.Restricted,
        OffId => PluginLevel.Off,
        _ => null
    };

    /// <summary>
    /// The level that applies: the owner's stored choice when it is valid; else what the plugin suggested, but only CouchHUD's own plugins may
    /// suggest more than admin; a function that is not declared (unknown id) is admin. Fail closed: admin is the loosest default there is.
    /// </summary>
    public static PluginLevel Effective(PluginFunction? declared, string? stored, bool own)
    {
        if (declared is null) return PluginLevel.Admin;
        if (ParseLevel(stored) is { } chosen) return chosen;
        return own && declared.Suggested == PluginLevel.Restricted ? PluginLevel.Restricted : PluginLevel.Admin;
    }

    /// <summary>
    /// Why a function of this level may not be used now (a text key); null when it may. Off: never. Admin: not on the locked couch and not in
    /// a child's profile. Restricted: always.
    /// </summary>
    public static string? Refusal(PluginLevel level, bool locked, bool childProfile) => level switch
    {
        PluginLevel.Off => "plugin.fn.off",
        PluginLevel.Restricted => null,
        _ => locked ? "lock.unlockFirst" : childProfile ? "plugin.gate.child" : null
    };

    /// <summary>What a person may do with a page, row, guide entry or tab of this level now: refused (a text key), or allowed, with the parents' code first.</summary>
    public readonly record struct FunctionAccess(string? Refused, bool ParentCode)
    {
        public bool Allowed => Refused is null;
    }

    /// <summary>
    /// For pages, rows, guide entries and tabs: off is refused; restricted is for everyone; admin is refused on the locked couch (the unlock first,
    /// as before), and in a child's profile it is allowed with the parents' code (one use, asked for this plugin, function and item; the couch code
    /// never stands in). Tools (assistant, MCP) cannot be asked for a code, so for them <see cref="Refusal"/> applies: admin in a child's profile is refused.
    /// </summary>
    public static FunctionAccess Access(PluginLevel level, bool locked, bool childProfile) => level switch
    {
        PluginLevel.Off => new("plugin.fn.off", false),
        PluginLevel.Restricted => new(null, false),
        _ => locked ? new("lock.unlockFirst", false) : new(null, childProfile)
    };

    /// <summary>Can the restricted level make a difference at all? Only with the couch lock or the parental controls on.</summary>
    public static bool RestrictionsPossible(string? couchLock, bool parentalEnabled) => parentalEnabled || !string.Equals(couchLock, "off", StringComparison.Ordinal);

    /// <summary>The stored level of a function (ids compared without case); null when none is stored or the map is damaged.</summary>
    public static string? Stored(IReadOnlyDictionary<string, Dictionary<string, string>>? map, string pluginId, string functionId)
    {
        if (map is null) return null;
        // Case variants of the same key (a hand-edited file) must not let the first one win by luck: the strictest valid one counts.
        PluginLevel? best = null;
        foreach (var (plugin, levels) in map)
        {
            if (!string.Equals(plugin, pluginId, StringComparison.OrdinalIgnoreCase) || levels is null) continue;
            foreach (var (fn, level) in levels)
            {
                if (!string.Equals(fn, functionId, StringComparison.OrdinalIgnoreCase) || ParseLevel(level) is not { } parsed) continue;
                if (best is null || Strictness(parsed) > Strictness(best.Value)) best = parsed;
            }
        }
        return best is { } b ? LevelId(b) : null;
    }

    private static int Strictness(PluginLevel level) => level switch { PluginLevel.Off => 2, PluginLevel.Admin => 1, _ => 0 };

    /// <summary>Stores a level under the plugin's and the function's own ids and removes every case variant of the same key.</summary>
    public static void Set(Dictionary<string, Dictionary<string, string>> map, string pluginId, string functionId, PluginLevel level)
    {
        foreach (var key in map.Keys.Where(k => string.Equals(k, pluginId, StringComparison.OrdinalIgnoreCase) && k != pluginId).ToList())
        {
            // A variant of the plugin's key: its levels move under the exact key (the variants of one function are cleaned below).
            var variant = map[key];
            map.Remove(key);
            if (variant is null) continue;
            if (!map.TryGetValue(pluginId, out var own) || own is null) map[pluginId] = own = new();
            foreach (var (fn, value) in variant) own.TryAdd(fn, value);
        }
        if (!map.TryGetValue(pluginId, out var levels) || levels is null) map[pluginId] = levels = new();
        foreach (var key in levels.Keys.Where(k => string.Equals(k, functionId, StringComparison.OrdinalIgnoreCase)).ToList()) levels.Remove(key);
        levels[functionId] = LevelId(level);
    }

    /// <summary>Drops the entries of plugins that are not there (any case); true when something went.</summary>
    public static bool Prune(Dictionary<string, Dictionary<string, string>>? map, IEnumerable<string> presentPluginIds)
    {
        if (map is null) return false;
        var present = presentPluginIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var gone = map.Keys.Where(k => !present.Contains(k)).ToList();
        foreach (var key in gone) map.Remove(key);
        return gone.Count > 0;
    }
}
