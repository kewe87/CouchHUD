namespace CouchHud.Sdk;

public enum ItemType
{
    Game,
    App
}

public enum LaunchKind
{
    /// <summary>Opened with the default handler, e.g. steam://, https://.</summary>
    Url,
    /// <summary>Executable started directly.</summary>
    Exe
}

public sealed record LaunchTarget(LaunchKind Kind, string Target)
{
    public IReadOnlyList<string> Args { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    /// <summary>An http(s) address that opens in CouchHud's own browser instead of the system's.</summary>
    public bool Embedded { get; init; }
}

/// <summary>Candidate image URLs per slot, tried in order by the UI.</summary>
public sealed record Artwork
{
    public IReadOnlyList<string> Cover { get; init; } = [];
    public IReadOnlyList<string> Hero { get; init; } = [];
    public IReadOnlyList<string> Logo { get; init; } = [];
    /// <summary>Square app icon (exe icon, package logo); the UI draws it on a generated banner when there is no cover.</summary>
    public IReadOnlyList<string> Icon { get; init; } = [];

    public static Artwork Empty { get; } = new();
}

public sealed record LibraryItem
{
    /// <summary>Stable, unique id like "steam:1245620" or "myplugin:foo".</summary>
    public required string Id { get; init; }
    /// <summary>Short source key shown in the UI ("steam", "epic", "custom", plugin id).</summary>
    public required string Source { get; init; }
    public required string Name { get; init; }
    public ItemType Type { get; init; } = ItemType.Game;
    public required LaunchTarget Launch { get; init; }
    public Artwork Art { get; init; } = Artwork.Empty;
    /// <summary>Unix epoch milliseconds, 0 if unknown.</summary>
    public long LastPlayed { get; init; }
    public bool Favorite { get; init; }
    /// <summary>Install folder if known; used for size, details and uninstall lookup.</summary>
    public string? InstallDir { get; init; }
    /// <summary>Size in bytes as reported by the store, 0 if unknown.</summary>
    public long SizeOnDisk { get; init; }
    /// <summary>Hidden by the user; still known so it can be shown again.</summary>
    public bool Hidden { get; init; }
    /// <summary>Total minutes played as tracked by CouchHud.</summary>
    public double PlaytimeMinutes { get; init; }
    /// <summary>Minutes of the last play session as tracked by CouchHud, 0 if unknown.</summary>
    public double LastSessionMinutes { get; init; }
}
