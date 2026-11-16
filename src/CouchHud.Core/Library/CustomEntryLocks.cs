using CouchHud.Core.Config;
using CouchHud.Core.Parental;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>
/// Own entries are identified by their name's slug, with a number for a second and third entry of the same name. Removing one shifts the ids of
/// the later ones with the same slug (custom:x-2 becomes custom:x). Everything stored under an id must move with the entry, and the rules that
/// restrict it (parental locks, hidden, per-game play time) must never fall away from an entry that still exists.
/// </summary>
public static class CustomEntryLocks
{
    /// <summary>
    /// True when the entry is locked for any profile, judged as the parental service judges it (<see cref="ProfileView.EffectiveRules"/>: the profile's
    /// list and the household's for all children, everything locked while that list is unreadable or the rules say lock everything, and entries that
    /// share a game with a locked one). Profiles the app ignores (<see cref="AppConfig.Profiles"/> entries that are not usable) are looked at too.
    /// </summary>
    public static bool IsLocked(AppConfig c, LibraryItem item, IEnumerable<LibraryItem> all)
    {
        var items = all.ToList();
        if (c.LockedItemsAllChildren is null || c.LockedItemsAllChildrenUnreadable || c.LockedItemsAllChildren.Contains(item.Id)) return true;
        foreach (var p in c.AllProfiles)
            if (ParentalService.IsLocked(p.EffectiveRules, item, items)) return true;
        // Profiles the app does not use (damaged entries): their own list still counts.
        foreach (var raw in c.Profiles ?? [])
            if (raw?.Data?.Rules is { } r && (r.LockEverything || r.LockedItems?.Contains(item.Id) == true)) return true;
        return false;
    }

    /// <summary>The ids that change when the entry at <paramref name="removedIndex"/> goes: (old id, new id) of every later entry whose slug number moves up.</summary>
    public static IReadOnlyList<(string From, string To)> Shifts(IReadOnlyList<string> idsBefore, int removedIndex, IReadOnlyList<string> idsAfter)
    {
        var moves = new List<(string From, string To)>();
        for (var j = removedIndex; j < idsAfter.Count; j++)
            if (idsBefore[j + 1] != idsAfter[j]) moves.Add((idsBefore[j + 1], idsAfter[j]));
        return moves;
    }

    /// <summary>
    /// After the entry at <paramref name="removedIndex"/> was removed: the locks of every profile (also unusable ones) and the household list follow the
    /// ids (union: an id is locked when it was locked or when the entry that carries it now was; an id no entry carries loses its lock), and
    /// everything else stored under an id moves with its entry. <paramref name="itemsBefore"/> and <paramref name="itemsAfter"/> are the entries in order.
    /// The removed entry's own record (favorite, override, hidden mark) is to be gone before this runs, so a shifted entry's data lands on a free id.
    /// </summary>
    public static void Rekey(AppConfig c, IReadOnlyList<LibraryItem> itemsBefore, int removedIndex, IReadOnlyList<LibraryItem> itemsAfter)
    {
        var idsBefore = itemsBefore.Select(i => i.Id).ToList();
        var idsAfter = itemsAfter.Select(i => i.Id).ToList();
        var moves = Shifts(idsBefore, removedIndex, idsAfter);
        var gone = idsBefore.Except(idsAfter).ToList();
        if (c.LockedItemsAllChildren is { } shared) ApplyLocks(shared, moves, gone);
        foreach (var rules in AllRules(c)) ApplyLocks(rules.LockedItems, moves, gone);
        // Ascending: x-2 moves to the id x just left before x-3 moves to the id x-2 just left.
        foreach (var (from, to) in moves) MoveData(c, from, to);
        for (var j = removedIndex; j < itemsAfter.Count; j++)
        {
            var oldKey = ParentalService.LimitKey(itemsBefore[j + 1]);
            var newKey = ParentalService.LimitKey(itemsAfter[j]);
            // The per-game play time is kept under the id for an entry without a folder: it follows (and adds up, never resets).
            if (oldKey != newKey) FolderIdMigration.Record(c, oldKey, newKey);
        }
    }

    /// <summary>The removed entry is out of every profile's favorites and collections (also the profiles the app ignores).</summary>
    public static void Forget(AppConfig c, string id)
    {
        // Everything stored under the id goes, so the entry that takes the id over keeps its own record (nothing of the removed one merges in).
        // The play time here is display only (the limits are counted in the ledger, which is separate), so it goes with the rest.
        c.GameProfiles.Remove(id);
        foreach (var (favorites, collections, launched, playtime, session) in Persons(c))
        {
            favorites.Remove(id);
            Collections.Forget(collections, id);
            launched.Remove(id);
            playtime.Remove(id);
            session.Remove(id);
        }
    }

    private static IEnumerable<ParentalConfig> AllRules(AppConfig c)
    {
        yield return c.Parental;
        foreach (var p in c.Profiles ?? [])
            if (p?.Data?.Rules is { LockedItems: not null } r) yield return r;
    }

    private static void ApplyLocks(List<string> locks, IReadOnlyList<(string From, string To)> moves, List<string> gone)
    {
        var before = locks.ToHashSet(StringComparer.Ordinal);
        foreach (var (from, to) in moves)
            if (before.Contains(from) && !locks.Contains(to)) locks.Add(to);
        locks.RemoveAll(gone.Contains);
    }

    /// <summary>Everything an entry has under its id, for every profile (also unusable ones): the record moves to the new id; where the new id has one, the restricting side wins (hidden, more play time).</summary>
    private static void MoveData(AppConfig c, string from, string to)
    {
        if (c.Overrides.Remove(from, out var o))
        {
            if (c.Overrides.TryGetValue(to, out var mine)) o.Hidden |= mine.Hidden;
            c.Overrides[to] = o;
        }
        if (c.GameProfiles.Remove(from, out var game) && !c.GameProfiles.ContainsKey(to)) c.GameProfiles[to] = game;
        foreach (var e in c.Ignored ?? [])
        {
            if (e?.Keys is not { } keys) continue;
            var key = LibraryIgnore.IdPrefix + from;
            var at = keys.IndexOf(key);
            if (at < 0) continue;
            keys.RemoveAt(at);
            if (!keys.Contains(LibraryIgnore.IdPrefix + to)) keys.Insert(at, LibraryIgnore.IdPrefix + to);
        }
        foreach (var (favorites, collections, launched, playtime, session) in Persons(c))
        {
            MoveInList(favorites, from, to);
            foreach (var x in collections) if (x?.Items is { } items) MoveInList(items, from, to);
            MoveKey(launched, from, to, Math.Max);
            MoveKey(playtime, from, to, (a, b) => a + b);
            MoveKey(session, from, to, Math.Max);
        }
    }

    private static IEnumerable<(List<string>, List<ItemCollection>, Dictionary<string, long>, Dictionary<string, double>, Dictionary<string, double>)> Persons(AppConfig c)
    {
        yield return (c.Favorites, c.Collections, c.LastLaunched, c.PlaytimeMinutes, c.LastSessionMinutes);
        foreach (var p in c.Profiles ?? [])
            if (p?.Data is { Favorites: not null, Collections: not null, LastLaunched: not null, PlaytimeMinutes: not null, LastSessionMinutes: not null } d)
                yield return (d.Favorites, d.Collections, d.LastLaunched, d.PlaytimeMinutes, d.LastSessionMinutes);
    }

    private static void MoveInList(List<string> list, string from, string to)
    {
        var at = list.IndexOf(from);
        if (at < 0) return;
        list.RemoveAll(x => x == from);
        if (!list.Contains(to)) list.Insert(Math.Min(at, list.Count), to);
    }

    private static void MoveKey<T>(Dictionary<string, T> map, string from, string to, Func<T, T, T> merge)
    {
        if (!map.Remove(from, out var old)) return;
        map[to] = map.TryGetValue(to, out var mine) ? merge(old, mine) : old;
    }
}
