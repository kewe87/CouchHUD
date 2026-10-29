using System.Text;

namespace CouchHud.Core.Updates;

/// <summary>Reads a child process's console stream to the end. A callback that throws never stops the reading: a full pipe would stall the child.</summary>
public static class OutputPump
{
    /// <summary>
    /// Reads <paramref name="reader"/> in pieces (winget animates with carriage returns), tells <paramref name="onOutput"/> about each (an
    /// empty piece at the end) and keeps a copy of at most <paramref name="maxKept"/> characters (+ one piece) if <paramref name="keep"/>.
    /// A throwing callback is reported once to <paramref name="onError"/> and then not called again; the stream is read on to its end.
    /// </summary>
    public static async Task<string> ReadAsync(TextReader reader, Action<ReadOnlyMemory<char>>? onOutput, bool keep, int maxKept, Action<Exception>? onError = null)
    {
        var kept = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer)) > 0)
        {
            if (keep && kept.Length < maxKept) kept.Append(buffer, 0, read);
            onOutput = Call(onOutput, buffer.AsMemory(0, read), onError);
        }
        Call(onOutput, ReadOnlyMemory<char>.Empty, onError);
        return kept.ToString();
    }

    /// <summary>Calls the callback; null back when it threw (reported once), so it is not called again.</summary>
    private static Action<ReadOnlyMemory<char>>? Call(Action<ReadOnlyMemory<char>>? callback, ReadOnlyMemory<char> piece, Action<Exception>? onError)
    {
        if (callback is null) return null;
        try
        {
            callback(piece);
            return callback;
        }
        catch (Exception ex)
        {
            try { onError?.Invoke(ex); } catch { }
            return null;
        }
    }
}
