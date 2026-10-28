namespace CouchHud.Core.Library;

/// <summary>What the watch of a batch start target's hidden cmd.exe does next.</summary>
public enum ScriptStep
{
    /// <summary>Let it run and look again later.</summary>
    Wait,

    /// <summary>It has nothing running and waits on input nobody can see (<c>pause</c>, <c>set /p</c>, <c>choice</c>): end it.</summary>
    EndWaiting,

    /// <summary>Its game session is over and nothing runs under it any more (before <see cref="ScriptWatch.Grace"/>): end it.</summary>
    EndSessionOver,
}

/// <summary>
/// A batch start target runs in a cmd.exe without a console window (LaunchOptions.StartInfo). A script that asks for input would wait
/// unseen and keep the batch file held, and neither Tasks nor Quit nor SafeTermination touch a Windows program; so the launcher watches
/// exactly the cmd.exe it started (pid and start time) and ends exactly that one, only after <see cref="EmptyChecks"/> checks in a row
/// found no child doing work (a game started in the foreground, <c>timeout</c>, <c>ping</c>, <c>tasklist</c> … are children and keep
/// it; one empty sample between two of them never ends it): once it has run for <see cref="Grace"/>, or earlier when its game session is
/// over. Never by name, never another cmd.exe. The decisions are here, the process calls in the shell.
/// </summary>
public static class ScriptWatch
{
    /// <summary>How long a script may run before an empty one counts as waiting for input.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    /// <summary>How often the watch looks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>Checks in a row without a working child before anything is ended (about 15 s).</summary>
    public const int EmptyChecks = 3;

    // Children that are no work: the console host every console process gets, and choice.exe, which waits on input just like pause.
    private static readonly string[] NoWork = ["conhost.exe", "choice.exe"];

    /// <param name="sinceStart">How long the cmd.exe runs.</param>
    /// <param name="sessionOver">Its game session (one that started after it) has ended and none started again.</param>
    /// <param name="emptyInARow">Checks in a row, this one included, that found no working child.</param>
    public static ScriptStep Decide(TimeSpan sinceStart, bool sessionOver, int emptyInARow)
    {
        if (emptyInARow < EmptyChecks) return ScriptStep.Wait;
        if (sinceStart >= Grace) return ScriptStep.EndWaiting;
        return sessionOver ? ScriptStep.EndSessionOver : ScriptStep.Wait;
    }

    /// <summary>
    /// Children of the cmd.exe (pid, start time) that do work. A process counts as a child only when its parent id is the pid and it was
    /// created after the cmd.exe (a parent id may name a dead process whose number was handed out again). Unknown is counted as work, so a
    /// doubt never ends a script: a child whose start time or image cannot be read counts.
    /// </summary>
    public static int WorkingChildren(int pid, long start, IReadOnlyDictionary<int, int> parents, Func<int, long> startOf, Func<int, string?> imageOf, string windowsDir)
    {
        var count = 0;
        foreach (var (child, parent) in parents)
        {
            if (parent != pid || child == pid || child <= 0) continue;
            var created = startOf(child);
            if (created > 0 && created < start) continue;
            if (created > 0 && IsNoWork(imageOf(child), windowsDir)) continue;
            count++;
        }
        return count;
    }

    /// <summary>conhost.exe or choice.exe from the Windows folder (System32 or SysWOW64); anything else, or an unknown image, is work.</summary>
    public static bool IsNoWork(string? image, string windowsDir)
    {
        if (string.IsNullOrEmpty(image) || string.IsNullOrEmpty(windowsDir)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(image);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        foreach (var dir in new[] { "System32", "SysWOW64" })
        {
            foreach (var name in NoWork)
            {
                if (string.Equals(full, Path.Combine(windowsDir, dir, name), StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
}

/// <summary>
/// One watched cmd.exe: which entry it belongs to, when it started, whether its game session is over, and how many checks in a row were
/// empty. Written from the session events (UI thread) and read by the watch loop, so every field goes through Interlocked/Volatile.
/// </summary>
public sealed class ScriptRun(string itemId, string itemName, int pid, long start, DateTimeOffset started)
{
    private long _sessionOver; // UTC ticks when the session ended; 0 while it is not over
    private int _empty;

    public string ItemId { get; } = itemId;
    public string ItemName { get; } = itemName;
    public int Pid { get; } = pid;

    /// <summary>The creation time of exactly this cmd.exe (FILETIME ticks), compared on the handle that would end it.</summary>
    public long Start { get; } = start;

    public DateTimeOffset Started { get; } = started;

    public bool SessionOver => Interlocked.Read(ref _sessionOver) != 0;

    /// <summary>The last check found no working child.</summary>
    public bool Empty => Volatile.Read(ref _empty) > 0;

    /// <summary>
    /// A game session of this entry ended. It counts only for a script started before that session: a script started later (while an
    /// older session was still running) has its own game still ahead.
    /// </summary>
    public void SessionEnded(string itemId, DateTimeOffset sessionStarted, DateTimeOffset now)
    {
        if (itemId != ItemId || Started > sessionStarted) return;
        Interlocked.CompareExchange(ref _sessionOver, Math.Max(1, now.UtcTicks), 0);
    }

    /// <summary>A session of this entry started (again): the game runs, its end is ahead again.</summary>
    public void SessionStarted(string itemId)
    {
        if (itemId == ItemId) Interlocked.Exchange(ref _sessionOver, 0);
    }

    /// <summary>One check: counts the empty checks in a row and decides.</summary>
    public ScriptStep Observe(DateTimeOffset now, int workingChildren)
    {
        int empty;
        if (workingChildren == 0) empty = Interlocked.Increment(ref _empty);
        else
        {
            Interlocked.Exchange(ref _empty, 0);
            empty = 0;
        }
        return ScriptWatch.Decide(now - Started, SessionOver, empty);
    }
}
