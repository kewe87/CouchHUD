using System.Text;

namespace CouchHud.Core.Platform;

/// <summary>
/// A log writer that hands every line to the stream as exactly one write call. The HUD and the watcher process write to the
/// same log file; with a stream opened for appending only (the file system then appends at the current end for every write
/// call) a line is never torn apart or overwritten by the other process. Lines get a time stamp; text written without a
/// newline waits for the rest of its line.
/// Every line goes to the file system at once, but only into its cache: a hard reset loses what the cache had not written yet. The
/// lines that matter after such a reset (start, restore, fail-closed, warnings: what goes to <see cref="Durable"/>, i.e. Console.Error)
/// are flushed to the disk as well, at once but never on the thread that writes them (Console.Error is a synchronized writer: the UI thread
/// would wait for the disk otherwise): a flush on the thread pool, at most once per <see cref="DiskFlushGap"/> (a burst of warnings costs one
/// flush, the last line of the burst follows after the gap); ordinary lines follow lazily, at most <see cref="LazyFlushGap"/> later, in one
/// flush for all of them.
/// What a reset still cuts off can leave a run of zero bytes at the end of the file (NTFS had the new length, not the data):
/// <see cref="TrimTrailingZeros"/> removes it at the next start, so the log does not keep it.
/// The flush to the disk runs outside the lock the lines are written under (it can take a while on a busy disk, and every thread that logs
/// would wait for it); flushes and the closing of the stream take turns on a lock of their own, and closing first does a flush still due.
/// The default flush is the stream's own (<see cref="FileStream.Flush(bool)"/>): on the log's unbuffered stream that is only
/// FlushFileBuffers on its handle, safe beside a write of another thread.
/// </summary>
public sealed class LineWriter(Stream stream, Func<DateTime>? now = null, Action<Stream>? toDisk = null) : TextWriter
{
    private readonly object _gate = new();
    private readonly object _flushGate = new();
    private bool _closed;
    // Set under the gate when the writer is disposed: no flush is scheduled any more.
    private bool _disposed;
    // Lines handed to the stream (under the gate) and how many of them the last flush took along (under the flush gate).
    private long _written, _flushed;
    private readonly StringBuilder _partial = new();
    private readonly Func<DateTime> _now = now ?? (() => DateTime.Now);
    private readonly Action<Stream> _toDisk = toDisk ?? (s => (s as FileStream)?.Flush(flushToDisk: true));
    private long _lastDiskFlush = long.MinValue / 2;
    private Timer? _deferred;
    private long _deferredDue;
    private TextWriter? _durable;

    /// <summary>The shortest time between two flushes to the disk.</summary>
    public TimeSpan DiskFlushGap { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long an ordinary line waits at most for its flush to the disk (one flush takes along every line written meanwhile).</summary>
    public TimeSpan LazyFlushGap { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>The same log, but every line also reaches the disk (see the class remarks). For Console.Error.</summary>
    public TextWriter Durable => _durable ??= new DurableLines(this);

    /// <summary>Puts what was written so far on the disk now (the start line).</summary>
    public void FlushToDisk()
    {
        lock (_gate) TakeDiskFlush();
        DiskFlush();
    }

    /// <summary>
    /// A durable line: flushed at once on the thread pool (never on the writing thread, security re-review L-a), or after the gap since the
    /// last flush. A flush already pending takes this line along (coalesced).
    /// </summary>
    private void RequestDiskFlush()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            ScheduleDiskFlush(Math.Max(now, _lastDiskFlush + (long)DiskFlushGap.TotalMilliseconds));
        }
    }

    /// <summary>An ordinary line: flushed with the next flush, at the latest after <see cref="LazyFlushGap"/>. Called under the gate.</summary>
    private void RequestLazyDiskFlush() => ScheduleDiskFlush(Environment.TickCount64 + (long)LazyFlushGap.TotalMilliseconds);

    /// <summary>One timer at most: a flush due later is brought forward, never pushed back. Called under the gate.</summary>
    private void ScheduleDiskFlush(long due)
    {
        if (_disposed || (_deferred is not null && _deferredDue <= due)) return;
        var wait = Math.Max(0, due - Environment.TickCount64);
        _deferredDue = due;
        if (_deferred is null)
        {
            // Only the timer that is still the pending one flushes: one disposed by a flush in between may already have started.
            Timer? timer = null;
            timer = new Timer(_ =>
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_deferred, timer)) return;
                    TakeDiskFlush();
                }
                DiskFlush();
            }, null, wait, Timeout.Infinite);
            _deferred = timer;
        }
        else
        {
            _deferred.Change(wait, Timeout.Infinite);
        }
    }

    /// <summary>A flush starts now: whatever was waiting goes along with it. Called under the gate, before <see cref="DiskFlush"/>.</summary>
    private void TakeDiskFlush()
    {
        _deferred?.Dispose();
        _deferred = null;
        _lastDiskFlush = Environment.TickCount64;
    }

    /// <summary>The flush itself, outside the gate: lines go on being written meanwhile. Never after the stream was closed.</summary>
    private void DiskFlush()
    {
        lock (_flushGate)
        {
            if (!_closed) FlushStream();
        }
    }

    /// <summary>Called under the flush gate.</summary>
    private void FlushStream()
    {
        try
        {
            var upTo = Interlocked.Read(ref _written);
            _toDisk(stream);
            _flushed = upTo;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Logging is best effort.
        }
    }

    /// <summary>Writes through the owner, then asks for a flush to the disk.</summary>
    private sealed class DurableLines(LineWriter owner) : TextWriter
    {
        public override Encoding Encoding => owner.Encoding;

        public override void Write(char value)
        {
            owner.Write(value);
            if (value == '\n') owner.RequestDiskFlush();
        }

        public override void Write(string? value)
        {
            owner.Write(value);
            if (value?.Contains('\n') == true) owner.RequestDiskFlush();
        }

        public override void WriteLine(string? value)
        {
            owner.WriteLine(value);
            owner.RequestDiskFlush();
        }

        public override void Flush() => owner.Flush();
    }

    public override Encoding Encoding { get; } = new UTF8Encoding(false);

    public override void Write(char value)
    {
        lock (_gate)
        {
            _partial.Append(value);
            if (value == '\n') FlushPartial();
        }
    }

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (_gate)
        {
            _partial.Append(value);
            if (value.Contains('\n')) FlushPartial();
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_gate)
        {
            var line = _partial.Length > 0 ? _partial.ToString() + value : value;
            _partial.Clear();
            Emit($"{_now():yyyy-MM-dd HH:mm:ss.fff} {line}\r\n");
        }
    }

    public override void Flush()
    {
        lock (_gate) stream.Flush();
    }

    /// <summary>Text with newlines written through Write(): the complete lines go out in one piece, the rest waits.</summary>
    private void FlushPartial()
    {
        var text = _partial.ToString();
        var end = text.LastIndexOf('\n') + 1;
        _partial.Clear().Append(text, end, text.Length - end);
        Emit(text[..end].Replace("\r\n", "\n").Replace("\n", "\r\n"));
    }

    private void Emit(string text)
    {
        try
        {
            stream.Write(Encoding.GetBytes(text));
            stream.Flush();
            Interlocked.Increment(ref _written);
            RequestLazyDiskFlush();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Logging is best effort.
        }
    }

    /// <summary>
    /// Removes a run of zero bytes from the end of the log file: what a hard reset leaves when NTFS had the file's new length on the disk but
    /// not the lines in it. The file then ends with a line break again, so the next line starts on its own. Only when nobody else has the
    /// file open (the other CouchHUD process may be appending: then it stays for the next start). Cheap: it reads only the tail, normally
    /// one small block. Returns how many bytes went.
    /// </summary>
    public static long TrimTrailingZeros(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            var end = file.Length;
            var buffer = new byte[4096];
            while (end > 0)
            {
                var size = (int)Math.Min(buffer.Length, end);
                file.Position = end - size;
                file.ReadExactly(buffer, 0, size);
                var last = Array.FindLastIndex(buffer, size - 1, size, b => b != 0);
                if (last >= 0)
                {
                    end = end - size + last + 1;
                    break;
                }
                end -= size;
            }
            var removed = file.Length - end;
            if (removed == 0) return 0;
            file.SetLength(end);
            if (end > 0)
            {
                file.Position = end - 1;
                if (file.ReadByte() != '\n') file.Write("\r\n"u8);
            }
            file.Flush(flushToDisk: true);
            return removed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Missing, or held by the other process: logging is best effort.
            return 0;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _disposed = true;
                _deferred?.Dispose();
                _deferred = null;
            }
            // A flush running right now (also one the timer started just before) ends first; lines no flush took along yet (written lazily, or taken by a flush that had not run)
            // reach the disk before the stream closes.
            lock (_flushGate)
            {
                if (!_closed)
                {
                    if (Interlocked.Read(ref _written) != _flushed) FlushStream();
                    _closed = true;
                    stream.Dispose();
                }
            }
        }
        base.Dispose(disposing);
    }
}
