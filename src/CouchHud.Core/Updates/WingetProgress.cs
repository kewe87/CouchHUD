using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CouchHud.Core.Updates;

/// <summary>What winget is doing with the current package.</summary>
public enum WingetPhase
{
    /// <summary>Found, nothing visible happening yet (license text, dependency check, hash check, or a package without a download).</summary>
    Prepare,
    Download,
    Install
}

/// <summary>Where a running <c>winget upgrade</c> stands. Total is 0 while winget did not say how many packages come.</summary>
public sealed record WingetProgressInfo(int Index, int Total, string? Id, WingetPhase Phase, long BytesDone, long BytesTotal, double? Percent);

/// <summary>
/// Reads the console stream of a running <c>winget upgrade</c> (stdout redirected) and tells where it is. winget has no machine readable
/// progress on the command line, its words are localized, and it animates with carriage returns, backspaces and escape sequences. So
/// the parser keys on structure only, never on words: the leading "(i/n)" counter, the package id in square brackets, a url,
/// "x MB / y MB" with a unit, a bare percent, and a line of block characters (the bar of the install step).
/// <para>
/// Winget's output is untrusted text (a package name or an installer's own words end up in it): a line is cut at
/// <see cref="MaxLine"/> characters (the rest is dropped until the next line break), an id at <see cref="MaxId"/>, numbers are
/// range checked, nothing is ever executed or passed on. Single threaded: feed it from one reader.
/// </para>
/// </summary>
public sealed partial class WingetProgressParser
{
    public const int MaxLine = 2048;
    public const int MaxId = 128;
    /// <summary>More than this is no real package count (a counter such as "(1/99999)" is ignored).</summary>
    public const int MaxPackages = 999;
    private const long MaxBytes = 1L << 42;

    private readonly StringBuilder _line = new();
    private bool _overflow;
    private bool _sawCounter;

    public WingetProgressInfo? Current { get; private set; }

    /// <summary>Feeds the next piece of output; true when <see cref="Current"/> changed.</summary>
    public bool Feed(ReadOnlySpan<char> chunk)
    {
        var changed = false;
        foreach (var c in chunk)
        {
            switch (c)
            {
                case '\r':
                case '\n':
                    changed |= Process(_line.ToString());
                    _line.Clear();
                    _overflow = false;
                    break;
                case '\b':
                    // The spinner: "-\b\\\b|" leaves the last frame.
                    if (_line.Length > 0 && !_overflow) _line.Length--;
                    break;
                default:
                    if (_line.Length >= MaxLine) _overflow = true;
                    else _line.Append(c);
                    break;
            }
        }
        return changed;
    }

    /// <summary>The output ended: a last line without a line break counts too.</summary>
    public bool Flush()
    {
        var changed = Process(_line.ToString());
        _line.Clear();
        _overflow = false;
        return changed;
    }

    private bool Process(string raw)
    {
        var line = Escape().Replace(raw, "").Trim();
        if (line.Length <= 1) return false; // empty, or a spinner frame
        var before = Current;

        if (TryPackage(line)) return Current != before;

        if (Current is not { } cur) return false; // nothing before the first package line is about a package

        var size = Transfer().Match(line);
        if (size.Success)
        {
            var done = ToBytes(size.Groups[1].Value, size.Groups[2].Value);
            var total = ToBytes(size.Groups[3].Value, size.Groups[4].Value);
            if (done is null || total is null || total <= 0) return false;
            done = Math.Min(done.Value, total.Value);
            // Everything is here: what follows is the setup running (hash check, installer).
            Current = done >= total
                ? cur with { Phase = WingetPhase.Install, BytesDone = 0, BytesTotal = 0, Percent = null }
                : cur with { Phase = WingetPhase.Download, BytesDone = done.Value, BytesTotal = total.Value, Percent = null };
            return Current != before;
        }

        // A url (the word before it is localized) opens a download.
        if (Url().IsMatch(line))
        {
            Current = cur with { Phase = WingetPhase.Download, BytesDone = 0, BytesTotal = 0, Percent = null };
            return Current != before;
        }

        var bare = Blocks().Replace(line, "").Trim();
        var percent = Percent().Match(bare);
        if (percent.Success && int.Parse(percent.Groups[1].Value, CultureInfo.InvariantCulture) is var p and <= 100)
        {
            Current = cur with { Phase = cur.Phase == WingetPhase.Install ? WingetPhase.Install : WingetPhase.Download, Percent = p / 100.0 };
            return Current != before;
        }

        // A bar of blocks without a size: the setup is running (the download's own bar always carries its numbers).
        if (bare.Length == 0 && line.Length >= 3 && cur.Phase != WingetPhase.Download)
        {
            Current = cur with { Phase = WingetPhase.Install, BytesDone = 0, BytesTotal = 0, Percent = null };
            return Current != before;
        }
        return false;
    }

    private bool TryPackage(string line)
    {
        var counter = Counter().Match(line);
        if (counter.Success)
        {
            var index = int.Parse(counter.Groups[1].Value, CultureInfo.InvariantCulture);
            var total = int.Parse(counter.Groups[2].Value, CultureInfo.InvariantCulture);
            if (index < 1 || total < 1 || index > total || total > MaxPackages) return false;
            _sawCounter = true;
            Current = new WingetProgressInfo(index, total, counter.Groups[3].Value, WingetPhase.Prepare, 0, 0, null);
            return true;
        }
        // Without a counter (one package, or an old winget): "Found <name> [<id>] Version <x>", an id in brackets followed by a version.
        // Only once, before any package is known: a later "[INFO] ... 5" line is never a second package.
        if (_sawCounter || Current is not null) return false;
        var found = Found().Match(line);
        if (!found.Success) return false;
        Current = new WingetProgressInfo(1, 0, found.Groups[1].Value, WingetPhase.Prepare, 0, 0, null);
        return true;
    }

    /// <summary>"1.5", "1,5", "1.234,5" or "1,234.5" with its unit, as bytes; null for nonsense.</summary>
    internal static long? ToBytes(string number, string unit)
    {
        if (!Number().IsMatch(number)) return null;
        var decimalAt = Math.Max(number.LastIndexOf(','), number.LastIndexOf('.'));
        var digits = new StringBuilder();
        for (var i = 0; i < number.Length; i++)
        {
            if (char.IsAsciiDigit(number[i])) digits.Append(number[i]);
            // One or two digits after the last separator: a fraction. Three ("1,234") is a thousands separator, dropped.
            else if (i == decimalAt && number.Length - i - 1 is >= 1 and <= 2) digits.Append('.');
        }
        if (!double.TryParse(digits.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || value < 0) return null;
        var factor = unit.ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" or "KIB" => 1024d,
            "MB" or "MIB" => 1024d * 1024,
            "GB" or "GIB" => 1024d * 1024 * 1024,
            "TB" or "TIB" => 1024d * 1024 * 1024 * 1024,
            _ => 0d
        };
        var bytes = value * factor;
        return factor == 0 || double.IsNaN(bytes) || bytes > MaxBytes ? null : (long)bytes;
    }

    [GeneratedRegex(@"^(?:\d{1,3}(?:[.,]\d{3})+|\d+)(?:[.,]\d{1,2})?$")]
    private static partial Regex Number();

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)?|[@-Z\\-_])")]
    private static partial Regex Escape();

    [GeneratedRegex(@"^\((\d{1,9})/(\d{1,9})\)\s.*?\[([A-Za-z0-9][^\]\s]{0,127})\]")]
    private static partial Regex Counter();

    [GeneratedRegex(@"\[([A-Za-z0-9][^\]\s]{0,127})\]\s+.*\d")]
    private static partial Regex Found();

    [GeneratedRegex(@"(\d[\d.,]{0,15})\s*(B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)\s*/\s*(\d[\d.,]{0,15})\s*(B|KB|MB|GB|TB|KiB|MiB|GiB|TiB)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Transfer();

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"[▀-▟]+")]
    private static partial Regex Blocks();

    [GeneratedRegex(@"^(\d{1,3})\s*%$")]
    private static partial Regex Percent();
}
