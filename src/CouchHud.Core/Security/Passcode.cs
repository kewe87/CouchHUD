using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Security;

/// <summary>
/// A console-style passcode: a sequence of controller buttons (like the Xbox passkey).
/// Only a salted PBKDF2 hash is stored.
/// </summary>
public static class Passcode
{
    public static readonly IReadOnlySet<string> Buttons = new HashSet<string> { "a", "b", "x", "y", "lb", "rb", "up", "down", "left", "right" };
    public const int MinLength = 6;
    public const int MaxLength = 8;
    private const int Iterations = 100_000;

    /// <summary>
    /// What the config keeps instead of the hash once the CouchHUD service holds the code (P9): the service checks the buttons, and no
    /// copy of the hash stays in the user's own files (where any program of the user could read it and guess offline, without the
    /// service's lockout). Not a hash: <see cref="Verify"/> never accepts anything against it.
    /// </summary>
    public const string HeldByService = "service";

    /// <summary>A real hash in the stored format (not empty, not <see cref="HeldByService"/>).</summary>
    public static bool IsLocalHash(string? stored) => !string.IsNullOrEmpty(stored) && stored.StartsWith("pbkdf2-sha256$", StringComparison.Ordinal);

    /// <summary>
    /// Whether the code entered is the one stored locally: only a real hash that verifies. Nothing stored, <see cref="HeldByService"/> or
    /// anything else is no match (the confirmation of a lock of 0.4.0 then asks first, like for a code that does not match: review 4).
    /// </summary>
    public static bool MatchesLocal(IReadOnlyList<string> sequence, string? stored) => IsLocalHash(stored) && Verify(sequence, stored);

    public static void Validate(IReadOnlyList<string> sequence)
    {
        if (sequence.Count is < MinLength or > MaxLength) throw new ArgumentException(Strings.Get("passcode.length", MinLength, MaxLength));
        if (sequence.Any(b => !Buttons.Contains(b))) throw new ArgumentException(Strings.Get("passcode.allowed"));
    }

    public static string Hash(IReadOnlyList<string> sequence)
    {
        Validate(sequence);
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Derive(sequence, salt, Iterations);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(IReadOnlyList<string> sequence, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            return CryptographicOperations.FixedTimeEquals(Derive(sequence, salt, iterations), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(IReadOnlyList<string> sequence, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(string.Join(',', sequence)), salt, iterations, HashAlgorithmName.SHA256, 32);
}

/// <summary>
/// Slows down guessing: after five wrong codes the next try has to wait,
/// and every further miss doubles the wait (30 s, 60 s, 120 s … at most 30 min).
/// With a state file the count survives a restart, so rebooting gives no fresh tries.
/// </summary>
public sealed class Lockout
{
    private const int FreeTries = 5;
    /// <summary>No wait is longer than this, whatever the clock says (a stored time far ahead after the clock was turned back).</summary>
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);
    private readonly Func<DateTimeOffset> clock;
    private readonly string? _file;
    private int _failures;
    private DateTimeOffset _until = DateTimeOffset.MinValue;
    private readonly object _gate = new();

    public Lockout() : this(() => DateTimeOffset.UtcNow)
    {
    }

    public Lockout(Func<DateTimeOffset> clock, string? stateFile = null)
    {
        this.clock = clock;
        _file = stateFile;
        Load();
    }

    private sealed record State(int Failures, DateTimeOffset Until);

    private void Load()
    {
        if (_file is null || !File.Exists(_file) && !File.Exists(CouchHud.Core.Files.DurableFile.BackupOf(_file))) return;
        // The file, or its copy from before when a reset left the file empty or broken.
        var text = CouchHud.Core.Files.DurableFile.ReadValid(_file, t => TryParse(t) is not null, out var fromBackup);
        if (text is not null && TryParse(text) is { } state)
        {
            if (fromBackup) Console.Error.WriteLine($"[lockout] {Path.GetFileName(_file)} unreadable, using the copy from before");
            _failures = Math.Max(0, state.Failures);
            var latest = clock() + MaxWait;
            _until = state.Until > latest ? latest : state.Until;
            return;
        }
        // Neither can be read: no fresh set of tries. The right code still works at once, the next wrong one starts the wait.
        Console.Error.WriteLine($"[lockout] {Path.GetFileName(_file)} and its copy are unreadable: the next wrong code starts the wait");
        _failures = FreeTries - 1;
    }

    private static State? TryParse(string text)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<State>(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private void Save()
    {
        if (_file is null) return;
        try
        {
            CouchHud.Core.Files.DurableFile.WriteAllText(_file, System.Text.Json.JsonSerializer.Serialize(new State(_failures, _until)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Remaining wait, or zero when a try is allowed.</summary>
    public TimeSpan Wait
    {
        get
        {
            lock (_gate)
            {
                var left = _until - clock();
                return left > MaxWait ? MaxWait : left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }
    }

    public void Failed()
    {
        lock (_gate)
        {
            _failures++;
            if (_failures >= FreeTries)
            {
                var seconds = Math.Min(30 * Math.Pow(2, _failures - FreeTries), 1800);
                _until = clock() + TimeSpan.FromSeconds(seconds);
            }
            Save();
        }
    }

    public void Succeeded()
    {
        lock (_gate)
        {
            _failures = 0;
            _until = DateTimeOffset.MinValue;
            Save();
        }
    }
}
