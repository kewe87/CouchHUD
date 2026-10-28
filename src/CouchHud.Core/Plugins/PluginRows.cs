using System.Globalization;
using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>One option of a choice row as the page gets it.</summary>
public sealed record PluginOptionView(string Id, string Label);

/// <summary>
/// A plugin row as the page gets it: only plain, bounded values (text cut to its limit, an image only as a checked PNG data URI), never
/// what the plugin handed over as is.
/// </summary>
public sealed record PluginRowView(string Id, string Label)
{
    public string? Detail { get; init; }
    public string? Icon { get; init; }
    public string? Value { get; init; }
    public bool On { get; init; }
    public string? Section { get; init; }
    public string? Image { get; init; }
    public string? Badge { get; init; }

    /// <summary>action, toggle, stepper, choice or link.</summary>
    public string Kind { get; init; } = "action";

    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public int? SettleMs { get; init; }
    public IReadOnlyList<PluginOptionView>? Options { get; init; }
    public bool AllowedWhileLocked { get; init; }
}

/// <summary>The host's filter for what a plugin's page shows, and its checks for what a page sends back.</summary>
public static class PluginRows
{
    public const int MaxRows = 200;
    public const int MaxOptions = 50;
    public const int MaxId = 100;
    public const int MaxLabel = 200;
    public const int MaxDetail = 500;
    public const int MaxShort = 100;
    public const int MaxMark = 16;
    public const int MaxImageSide = 4096;

    private static string? Cut(string? text, int max) => text is null ? null : text.Length <= max ? text : text[..max];

    /// <summary>The rows a page may show: at most <see cref="MaxRows"/>, rows without a usable id dropped, every text bounded.</summary>
    public static IReadOnlyList<PluginRowView> Filter(IReadOnlyList<PluginRow>? rows)
    {
        var views = new List<PluginRowView>();
        foreach (var row in rows ?? [])
        {
            if (views.Count >= MaxRows) break;
            if (row is null || string.IsNullOrEmpty(row.Id) || row.Id.Length > MaxId) continue;
            views.Add(View(row));
        }
        return views;
    }

    private static PluginRowView View(PluginRow row)
    {
        var view = new PluginRowView(row.Id, Cut(row.Label, MaxLabel) ?? "")
        {
            Detail = Cut(row.Detail, MaxDetail),
            Icon = Cut(row.Icon, MaxMark),
            Value = Cut(row.Value, MaxShort),
            On = row.On,
            Section = Cut(row.Section, MaxId),
            Image = ImageUri(row.Image),
            Badge = Cut(row.Badge, MaxMark),
            AllowedWhileLocked = row.AllowedWhileLocked
        };
        return row.Kind switch
        {
            PluginRowKind.ToggleKind => view with { Kind = "toggle" },
            PluginRowKind.Stepper s when StepperOk(s) => view with { Kind = "stepper", Min = s.Min, Max = s.Max, Step = s.Step, SettleMs = Math.Clamp(s.SettleMs, 0, 5000) },
            PluginRowKind.Choice c => view with
            {
                Kind = "choice",
                Options = (c.Options ?? []).Where(o => o is not null && !string.IsNullOrEmpty(o.Id) && o.Id.Length <= MaxShort).Take(MaxOptions)
                    .Select(o => new PluginOptionView(o.Id, Cut(o.Label, MaxLabel) ?? "")).ToList()
            },
            PluginRowKind.Link => view with { Kind = "link" },
            // A stepper with a range that makes no sense is shown as a plain row (a press does what the plugin's Activate does).
            _ => view
        };
    }

    private static bool StepperOk(PluginRowKind.Stepper s) =>
        double.IsFinite(s.Min) && double.IsFinite(s.Max) && double.IsFinite(s.Step) && s.Min <= s.Max && s.Step > 0;

    /// <summary>A PNG of at most <see cref="PluginRow.MaxImageBytes"/> and <see cref="MaxImageSide"/> pixels a side as a data URI; anything else is dropped.</summary>
    public static string? ImageUri(byte[]? image)
    {
        if (image is null || image.Length == 0 || image.Length > PluginRow.MaxImageBytes) return null;
        if (CatalogMediaCache.PngSize(image) is not { } size || size.Width > MaxImageSide || size.Height > MaxImageSide
            || (long)size.Width * size.Height > CatalogMediaCache.MaxPixels) return null;
        return "data:image/png;base64," + Convert.ToBase64String(image);
    }

    /// <summary>
    /// The page a link row opens: only a page of the same plugin. The host opens it itself, the plugin's code is never asked. Null when the row
    /// is no link or its target does not exist.
    /// </summary>
    public static string? LinkTarget(PluginRow row, Func<string, bool> pageExists) =>
        row.Kind is PluginRowKind.Link { PageId: { Length: > 0 and <= MaxId } target } && pageExists(target) ? target : null;

    /// <summary>
    /// A value a page sent for a stepper or choice row: only one of the row's options, or a number inside the row's range on its steps.
    /// Returns the value in the form the plugin gets (numbers in invariant form), or null to refuse.
    /// </summary>
    public static string? CheckValue(PluginRow row, string? value)
    {
        if (value is null || value.Length > MaxShort) return null;
        switch (row.Kind)
        {
            case PluginRowKind.Choice c:
                return (c.Options ?? []).Any(o => o is not null && o.Id == value) ? value : null;
            case PluginRowKind.Stepper s when StepperOk(s):
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return null;
                if (number < s.Min || number > s.Max) return null;
                var steps = (number - s.Min) / s.Step;
                if (Math.Abs(steps - Math.Round(steps)) > 1e-6) return null;
                return number.ToString(CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }
}
