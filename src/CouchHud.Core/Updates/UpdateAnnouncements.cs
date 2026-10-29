using System.Text.Json;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Updates;

/// <summary>
/// Which update versions the notification center has told about already, so every version is announced once: not at the 12 hour
/// checks, not at the quiet checks when Downloads opens, not after a restart. Small and bounded (the newest <see cref="MaxKeys"/> keys of
/// plugins and skins), read defensively (a damaged file only means one repeated notice).
/// </summary>
public sealed class UpdateAnnouncements
{
    // Why a plain first-in-first-out list of 64 is enough: a key is only needed while its update is still offered, and a catalog offers a few
    // dozen updates at most. A key that falls out after 64 newer ones belongs to an update that has long been installed or replaced; if it
    // ever came back, the worst case is one repeated notice. The file stays tiny and a hostile catalog cannot grow it.
    public const int MaxKeys = 64;
    public const int MaxKeyLength = 200;

    private readonly string? _path;
    private readonly List<string> _keys = [];
    private readonly object _lock = new();

    /// <summary>Loads from <paramref name="path"/> (null: memory only).</summary>
    public UpdateAnnouncements(string? path)
    {
        _path = path;
        if (path is null) return;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return;
            var list = JsonSerializer.Deserialize<List<string?>>(File.ReadAllText(path));
            if (list is null) return;
            foreach (var key in list)
                if (!string.IsNullOrEmpty(key) && key.Length <= MaxKeyLength && !_keys.Contains(key, StringComparer.OrdinalIgnoreCase)) _keys.Add(key);
            if (_keys.Count > MaxKeys) _keys.RemoveRange(0, _keys.Count - MaxKeys);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _keys.Clear();
        }
    }

    public static string Key(string kind, string id, string version) => $"{kind}:{id}@{version}";

    /// <summary>The keys not announced before; they are remembered (and saved) now.</summary>
    public List<string> TakeFresh(IEnumerable<string> keys)
    {
        var fresh = new List<string>();
        lock (_lock)
        {
            foreach (var key in keys)
            {
                if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength || _keys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                _keys.Add(key);
                fresh.Add(key);
            }
            if (fresh.Count == 0) return fresh;
            if (_keys.Count > MaxKeys) _keys.RemoveRange(0, _keys.Count - MaxKeys);
            Save();
        }
        return fresh;
    }

    /// <summary>
    /// A plugin or skin update was applied by itself: the keys not told as "applied" before come back (once per version). The plain
    /// "available" key is taken too, so a version that was never announced is not announced as available afterwards, and one that was
    /// announced as available earlier is still told as applied.
    /// </summary>
    public List<string> TakeApplied(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        TakeFresh(list);
        var fresh = TakeFresh(list.Select(k => "applied:" + k));
        return fresh.Select(k => k["applied:".Length..]).ToList();
    }

    /// <summary>An update needs new permissions: told once per version ("perm:" key); the plain key is taken so the general notice stays away.</summary>
    public List<string> TakePermission(IEnumerable<string> keys)
    {
        var list = keys.ToList();
        TakeFresh(list);
        return TakeFresh(list.Select(k => "perm:" + k)).Select(k => k["perm:".Length..]).ToList();
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_keys));
            File.Move(temp, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not saved: the worst case is one repeated notice after a restart.
        }
    }

    /// <summary>A CouchHUD version is announced when nothing was announced before or it is newer than the last announced one.</summary>
    public static bool SelfIsNew(string? announced, string? version)
    {
        if (!SemVer.TryParse(version, out var v)) return false;
        return !SemVer.TryParse(announced, out var last) || v > last;
    }

    /// <summary>The names for a notification: cleaned like plugin titles, at most four, each capped.</summary>
    public static string Names(IEnumerable<string?> names) =>
        string.Join(", ", names.Select(n => PluginText.Strip(n, 40)).Where(n => n.Length > 0).Take(4));
}
