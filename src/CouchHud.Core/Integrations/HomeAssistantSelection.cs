using System.Text.Json;

namespace CouchHud.Core.Integrations;

/// <summary>One entry of the picker: everything Home Assistant offers that a TV can show, with its room.</summary>
public sealed record HaCatalogEntity(string Id, string Domain, string Name, string State, string? Unit, string? DeviceClass, string? Area, bool Suggested);

/// <summary>
/// What Home Assistant's registry adds to a state: the room (own or the device's), whether the owner hid the entity, and its
/// category ("config", "diagnostic"; null for a normal entity).
/// </summary>
public sealed record HaRegistryInfo(string? Area, bool Hidden, string? Category = null);

/// <summary>
/// The entities the owner chose for the Smart Home tab (config <c>HomeAssistantEntities</c>), in their order.
/// The list comes from the config file or the UI, so it is untrusted: only well-formed ids, no duplicates, at most
/// <see cref="MaxEntities"/>. Ids Home Assistant does not know are kept (a device may be offline for a while) but never shown or switched.
/// </summary>
public static class HomeAssistantSelection
{
    /// <summary>More than anyone can use on a TV, few enough that a hand-edited config cannot blow up a request or the page.</summary>
    public const int MaxEntities = 500;

    /// <summary>Longest room name passed on to the UI.</summary>
    public const int MaxAreaLength = 100;

    /// <summary>Offered first in the picker: things you switch from the couch. Sensors, automations and buttons only when searched for.</summary>
    private static readonly HashSet<string> SuggestedDomains = ["light", "switch", "climate", "cover", "media_player", "scene", "script", "fan", "lock"];

    /// <summary>Well-formed, distinct ids in the given order, cut at <see cref="MaxEntities"/>.</summary>
    public static List<string> Sanitize(IEnumerable<string?>? ids)
    {
        var result = new List<string>();
        if (ids is null) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (result.Count >= MaxEntities) break;
            if (!HomeAssistantPolicy.IsValidEntityId(id) || !seen.Add(id!)) continue;
            result.Add(id!);
        }
        return result;
    }

    /// <summary>Same as <see cref="Sanitize(IEnumerable{string?})"/> for a JSON array from the UI; anything that is not an array is an empty choice.</summary>
    public static List<string> Sanitize(JsonElement ids)
    {
        if (ids.ValueKind != JsonValueKind.Array) return [];
        // Read no further than needed: a huge array from the page costs nothing.
        return Sanitize(ids.EnumerateArray().Take(MaxEntities * 4).Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : null));
    }

    /// <summary>
    /// The choice in effect. Installs from before the picker have none: their pins (the guide menu entries) are the start, so nobody
    /// loses them silently. A stored choice always prunes the pins to itself, so an empty choice has no pins left to fall back to.
    /// </summary>
    public static List<string> Effective(IEnumerable<string?>? selection, IEnumerable<string?>? pinned)
    {
        var chosen = Sanitize(selection);
        return chosen.Count > 0 ? chosen : Sanitize(pinned);
    }

    /// <summary>
    /// What the Smart Home tab, the guide menu and the assistant see: the chosen entities in the chosen order, then pinned ones that
    /// are not chosen. Ids Home Assistant does not list are left out.
    /// </summary>
    public static List<HaEntity> InScope(IReadOnlyList<HaEntity> all, IEnumerable<string?>? selection, IEnumerable<string?>? pinned)
    {
        var byId = new Dictionary<string, HaEntity>(StringComparer.Ordinal);
        foreach (var e in all) byId.TryAdd(e.Id, e);
        var result = new List<HaEntity>();
        foreach (var id in Effective(selection, pinned))
        {
            if (byId.Remove(id, out var e)) result.Add(e with { Selected = true });
        }
        foreach (var id in Sanitize(pinned))
        {
            if (byId.Remove(id, out var e)) result.Add(e with { Selected = false });
        }
        return result;
    }

    /// <summary>May this id be switched or shown at all: chosen for the tab or pinned to the guide menu.</summary>
    public static bool IsInScope(string? entityId, IEnumerable<string?>? selection, IEnumerable<string?>? pinned)
    {
        if (!HomeAssistantPolicy.IsValidEntityId(entityId)) return false;
        var pins = Sanitize(pinned);
        return pins.Contains(entityId!, StringComparer.Ordinal) || Effective(selection, pins).Contains(entityId!, StringComparer.Ordinal);
    }

    /// <summary>Pins only for chosen entities: a choice that drops an entity also takes it out of the guide menu.</summary>
    public static List<string> PinsAfterChoice(IEnumerable<string?>? pinned, IReadOnlyCollection<string> selection) =>
        Sanitize(pinned).Where(p => selection.Contains(p, StringComparer.Ordinal)).ToList();

    /// <summary>A suggestion: something to switch from the couch, not hidden by the owner and no device setting or diagnostic value.</summary>
    public static bool IsSuggested(HaEntity entity, HaRegistryInfo? info) =>
        info is not { Hidden: true } && info?.Category is null && SuggestedDomains.Contains(entity.Domain);

    /// <summary>The picker's list: every entity Home Assistant offers for a TV, with room and suggestion, in Home Assistant's order (the UI groups).</summary>
    public static List<HaCatalogEntity> Catalog(IReadOnlyList<HaEntity> all, IReadOnlyDictionary<string, HaRegistryInfo> registry) =>
        all.Select(e =>
            {
                registry.TryGetValue(e.Id, out var info);
                return new HaCatalogEntity(e.Id, e.Domain, e.Name, e.State, e.Unit, e.DeviceClass, info?.Area, IsSuggested(e, info));
            })
            .ToList();

    /// <summary>
    /// Reads Home Assistant's registry answers (WebSocket API, none of them admin-only):
    /// <c>config/entity_registry/list_for_display</c> → <c>{"entity_categories":{"0":"config","1":"diagnostic"},"entities":[{"ei":…,"ai":…,"di":…,"ec":0,"hb":true}]}</c>,
    /// <c>config/area_registry/list</c> → <c>[{"area_id":…,"name":…}]</c>, <c>config/device_registry/list</c> → <c>[{"id":…,"area_id":…}]</c>.
    /// An entity's own area wins over its device's. Anything malformed is skipped; without entities the map is empty (the picker groups by type).
    /// </summary>
    public static Dictionary<string, HaRegistryInfo> ParseRegistry(JsonElement entities, JsonElement areas, JsonElement devices)
    {
        var result = new Dictionary<string, HaRegistryInfo>(StringComparer.Ordinal);
        if (entities.ValueKind != JsonValueKind.Object || !entities.TryGetProperty("entities", out var list) || list.ValueKind != JsonValueKind.Array) return result;

        var areaNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (areas.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in areas.EnumerateArray())
            {
                if (Str(a, "area_id") is { } id && CleanArea(Str(a, "name")) is { } name) areaNames.TryAdd(id, name);
            }
        }
        var deviceAreas = new Dictionary<string, string>(StringComparer.Ordinal);
        if (devices.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in devices.EnumerateArray())
            {
                if (Str(d, "id") is { } id && Str(d, "area_id") is { } area) deviceAreas.TryAdd(id, area);
            }
        }
        var categories = new Dictionary<int, string>();
        if (entities.TryGetProperty("entity_categories", out var cats) && cats.ValueKind == JsonValueKind.Object)
        {
            foreach (var c in cats.EnumerateObject())
            {
                if (int.TryParse(c.Name, out var index) && c.Value.ValueKind == JsonValueKind.String && c.Value.GetString() is { Length: > 0 and <= 32 } value)
                {
                    categories.TryAdd(index, value);
                }
            }
        }

        foreach (var e in list.EnumerateArray())
        {
            var id = Str(e, "ei");
            if (!HomeAssistantPolicy.IsValidEntityId(id)) continue;
            var areaId = Str(e, "ai") ?? (Str(e, "di") is { } device && deviceAreas.TryGetValue(device, out var viaDevice) ? viaDevice : null);
            var area = areaId is not null && areaNames.TryGetValue(areaId, out var name) ? name : null;
            var hidden = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("hb", out var hb) && hb.ValueKind == JsonValueKind.True;
            string? category = null;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("ec", out var ec) && ec.ValueKind == JsonValueKind.Number)
            {
                // Any category at all means "not a normal entity"; an index we do not know still counts.
                category = ec.TryGetInt32(out var index) && categories.TryGetValue(index, out var known) ? known : "other";
            }
            result.TryAdd(id!, new HaRegistryInfo(area, hidden, category));
        }
        return result;
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 and <= 255 } s ? s : null;

    private static string? CleanArea(string? area)
    {
        if (string.IsNullOrWhiteSpace(area)) return null;
        var clean = new string(area.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > MaxAreaLength ? clean[..MaxAreaLength] : clean;
    }
}
