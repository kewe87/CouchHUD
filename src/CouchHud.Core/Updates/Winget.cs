using System.Text.RegularExpressions;

namespace CouchHud.Core.Updates;

/// <summary>An available update, from winget or Windows Update.</summary>
/// <param name="SizeBytes">What the download will really be, only when it is known well enough (<see cref="UpdateSize.Shown(long?, long?, bool)"/>);
/// null when unknown, too uncertain or already downloaded.</param>
/// <param name="Downloaded">Windows Update: already downloaded, only the installation is left.</param>
public sealed record UpdateItem(
    string Id,
    string Source,
    string Name,
    string? Version,
    string? Available,
    long? SizeBytes = null,
    bool Important = false,
    bool Downloaded = false);

/// <summary>
/// Reads the table that <c>winget upgrade</c> prints. winget has no machine
/// readable output for upgrades, and the headers are localized, so columns are
/// found by position: the header line sits directly above the dashed rule.
/// </summary>
public static partial class Winget
{
    public static IReadOnlyList<UpdateItem> ParseUpgrades(string output)
    {
        var lines = Lines(output);
        var result = new List<UpdateItem>();
        var rule = lines.FindIndex(l => l.Length >= 10 && l.All(c => c == '-' || c == '─'));
        if (rule < 1) return result;

        var starts = HeaderWord().Matches(lines[rule - 1]).Select(m => m.Index).ToList();
        // Name, Id, Version, Available, (Source). Fewer means this isn't the upgrade table.
        if (starts.Count < 4) return result;

        foreach (var line in lines.Skip(rule + 1))
        {
            // The table ends at the first blank line ("n upgrades available" and the
            // "require explicit targeting" table follow; the latter can't be upgraded with --all).
            if (string.IsNullOrWhiteSpace(line)) break;
            if (line.Length <= starts[3]) continue;

            string Column(int i)
            {
                var from = starts[i];
                if (from >= line.Length) return "";
                var to = i + 1 < starts.Count ? Math.Min(starts[i + 1], line.Length) : line.Length;
                return line[from..to].Trim();
            }

            var id = Column(1);
            var available = Column(3);
            if (id.Length == 0 || id.Contains(' ') || available.Length == 0) continue;
            result.Add(new UpdateItem(
                id,
                "winget",
                Column(0).TrimEnd('…').TrimEnd(),
                NullIfUnknown(Column(2)),
                available,
                null));
        }
        return result;
    }

    /// <summary>Arguments for upgrading one package (or all with <paramref name="id"/> null), without any prompt.</summary>
    public static IReadOnlyList<string> UpgradeArguments(string? id)
    {
        var args = new List<string> { "upgrade" };
        if (id is null) args.Add("--all");
        else args.AddRange(["--id", id, "--exact"]);
        args.AddRange(["--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"]);
        return args;
    }

    /// <summary>
    /// Console output split into lines. winget animates a spinner and progress
    /// bars with carriage returns; only the text after the last one is what stays visible.
    /// </summary>
    private static List<string> Lines(string output) =>
        output.Replace("\r\n", "\n")
            .Split('\n')
            .Select(l => l[(l.LastIndexOf('\r') + 1)..].TrimEnd())
            .ToList();

    private static string? NullIfUnknown(string version) => version is "" or "Unknown" or "Unbekannt" or "<" ? null : version;

    [GeneratedRegex(@"\S+")]
    private static partial Regex HeaderWord();
}
