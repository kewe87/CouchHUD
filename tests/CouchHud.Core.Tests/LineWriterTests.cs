using CouchHud.Core.Platform;

namespace CouchHud.Core.Tests;

public class LineWriterTests
{
    private static readonly DateTime Moment = new(2026, 10, 20, 12, 30, 5, 123);

    /// <summary>Counts the write calls: a torn line would be two of them.</summary>
    private sealed class RecordingStream : MemoryStream
    {
        public List<string> Writes { get; } = [];

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes.Add(System.Text.Encoding.UTF8.GetString(buffer));
            base.Write(buffer);
        }
    }

    [Fact]
    public void A_line_is_one_write_call_with_a_time_stamp()
    {
        var stream = new RecordingStream();
        var writer = new LineWriter(stream, () => Moment);
        writer.WriteLine("[app] started");
        Assert.Equal(["2026-10-20 12:30:05.123 [app] started\r\n"], stream.Writes);
    }

    [Fact]
    public void A_multi_line_message_stays_in_one_piece()
    {
        var stream = new RecordingStream();
        var writer = new LineWriter(stream, () => Moment);
        writer.WriteLine("[app] fatal: System.Exception: x\r\n   at A()\r\n   at B()");
        Assert.Single(stream.Writes);
        Assert.EndsWith("   at B()\r\n", stream.Writes[0]);
    }

    [Fact]
    public void Text_without_a_newline_waits_for_the_rest_of_its_line()
    {
        var stream = new RecordingStream();
        var writer = new LineWriter(stream, () => Moment);
        writer.Write("[a] ");
        writer.Write("b");
        Assert.Empty(stream.Writes);
        writer.WriteLine("c");
        Assert.Equal(["2026-10-20 12:30:05.123 [a] bc\r\n"], stream.Writes);
    }

    [Fact]
    public void Two_writers_on_threads_never_tear_a_line()
    {
        var stream = new RecordingStream();
        var writer = new LineWriter(stream, () => Moment);
        Parallel.For(0, 4, t =>
        {
            for (var i = 0; i < 500; i++) writer.WriteLine($"[t{t}] line {i} " + new string('x', 200));
        });
        Assert.Equal(2000, stream.Writes.Count);
        Assert.All(stream.Writes, w =>
        {
            Assert.EndsWith("\r\n", w);
            Assert.Single(w.Split("\r\n", StringSplitOptions.RemoveEmptyEntries));
        });
    }

    [Fact]
    public void A_broken_stream_never_throws_into_the_program()
    {
        var stream = new MemoryStream();
        var writer = new LineWriter(stream, () => Moment);
        stream.Dispose();
        writer.WriteLine("after the end");
    }
    [Fact]
    public void Lines_of_the_durable_view_reach_the_disk_ordinary_ones_do_not()
    {
        var stream = new RecordingStream();
        var flushes = 0;
        var writer = new LineWriter(stream, () => Moment, _ => Interlocked.Increment(ref flushes)) { DiskFlushGap = TimeSpan.FromHours(1) };
        writer.WriteLine("[hud] ordinary");
        Assert.Equal(0, flushes);
        writer.Durable.WriteLine("[config] restored from the backup");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref flushes) == 1, TimeSpan.FromSeconds(10)));
        // Same file, same format: one write call per line.
        Assert.Equal(["2026-10-20 12:30:05.123 [hud] ordinary\r\n", "2026-10-20 12:30:05.123 [config] restored from the backup\r\n"], stream.Writes);
        writer.FlushToDisk();
        Assert.Equal(2, Volatile.Read(ref flushes));
    }

    [Fact]
    public void A_burst_of_warnings_costs_one_flush_and_the_rest_follows_after_the_gap()
    {
        var flushes = 0;
        using var inFlush = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = new LineWriter(new MemoryStream(), () => Moment, _ =>
        {
            if (Interlocked.Increment(ref flushes) == 1)
            {
                inFlush.Set();
                release.Wait(TimeSpan.FromSeconds(30));
            }
        }) { DiskFlushGap = TimeSpan.FromMilliseconds(200) };
        writer.Durable.WriteLine("[x] warning 0");
        // The first flush runs (held here); the rest of the burst waits for one flush after the gap.
        Assert.True(inFlush.Wait(TimeSpan.FromSeconds(10)));
        for (var i = 1; i < 50; i++) writer.Durable.WriteLine($"[x] warning {i}");
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref flushes) == 2, TimeSpan.FromSeconds(10)));
        Thread.Sleep(400);
        Assert.Equal(2, Volatile.Read(ref flushes));
        writer.Dispose();
    }

    [Fact]
    public void A_warning_is_flushed_at_once_but_never_on_the_thread_that_writes_it()
    {
        // Console.Error is a synchronized writer: a flush on the writing thread would hold the UI thread on the disk (re-review L-a).
        using var inFlush = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var flushThread = -1;
        var writer = new LineWriter(new MemoryStream(), () => Moment, _ =>
        {
            flushThread = Environment.CurrentManagedThreadId;
            inFlush.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        }) { DiskFlushGap = TimeSpan.FromHours(1), LazyFlushGap = TimeSpan.FromHours(1) };
        var writing = Environment.CurrentManagedThreadId;
        // The disk hangs: the warning still returns, and its flush starts at once (no gap: none ran before).
        writer.Durable.WriteLine("[x] warning");
        Assert.True(inFlush.Wait(TimeSpan.FromSeconds(10)));
        Assert.NotEqual(writing, flushThread);
        release.Set();
        writer.Dispose();
    }

    [Fact]
    public void Closing_right_after_a_warning_flushes_it_once_and_never_after_the_close()
    {
        for (var round = 0; round < 50; round++)
        {
            var flushes = 0;
            var closedInFlush = false;
            var writer = new LineWriter(new MemoryStream(), () => Moment, s =>
            {
                Interlocked.Increment(ref flushes);
                closedInFlush |= !s.CanWrite;
            });
            writer.Durable.WriteLine("[x] warning");
            // The flush on the pool may be pending, running or done: the close waits for it or does it, exactly once.
            writer.Dispose();
            Thread.Sleep(5);
            Assert.Equal(1, Volatile.Read(ref flushes));
            Assert.False(closedInFlush);
            // After the close no flush is scheduled any more.
            writer.Durable.WriteLine("[x] after the close");
            Thread.Sleep(20);
            Assert.Equal(1, Volatile.Read(ref flushes));
        }
    }

    [Fact]
    public void Ordinary_lines_reach_the_disk_lazily_in_one_flush()
    {
        var flushes = 0;
        var writer = new LineWriter(new MemoryStream(), () => Moment, _ => Interlocked.Increment(ref flushes)) { LazyFlushGap = TimeSpan.FromMilliseconds(150), DiskFlushGap = TimeSpan.FromMilliseconds(50) };
        for (var i = 0; i < 50; i++) writer.WriteLine($"[hud] line {i}");
        Assert.Equal(0, Volatile.Read(ref flushes));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref flushes) == 1, TimeSpan.FromSeconds(10)));
        Thread.Sleep(400);
        Assert.Equal(1, Volatile.Read(ref flushes));
        // A warning does not wait for the lazy flush, and takes the waiting lines along.
        writer.WriteLine("[hud] waiting");
        writer.Durable.WriteLine("[x] warning");
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref flushes) == 2, TimeSpan.FromSeconds(10)));
        Thread.Sleep(400);
        Assert.Equal(2, Volatile.Read(ref flushes));
    }

    [Fact]
    public void A_slow_flush_to_the_disk_does_not_hold_up_the_threads_that_log()
    {
        using var inFlush = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stream = new RecordingStream();
        var writer = new LineWriter(stream, () => Moment, _ =>
        {
            inFlush.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });
        writer.WriteLine("[app] started");
        var flushing = Task.Run(writer.FlushToDisk);
        Assert.True(inFlush.Wait(TimeSpan.FromSeconds(10)));
        // The flush hangs (a busy disk): other threads still get their lines out.
        Assert.True(Task.Run(() => writer.WriteLine("[hud] meanwhile")).Wait(TimeSpan.FromSeconds(10)));
        Assert.True(Task.Run(() => writer.Durable.WriteLine("[x] warning meanwhile")).Wait(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, stream.Writes.Count);
        release.Set();
        Assert.True(flushing.Wait(TimeSpan.FromSeconds(10)));
        writer.Dispose();
    }

    [Fact]
    public void Closing_the_log_flushes_what_is_still_due_and_waits_for_a_running_flush()
    {
        var flushes = 0;
        var closedInFlush = false;
        var stream = new MemoryStream();
        var writer = new LineWriter(stream, () => Moment, s =>
        {
            Interlocked.Increment(ref flushes);
            closedInFlush |= !s.CanWrite;
        }) { LazyFlushGap = TimeSpan.FromHours(1) };
        writer.WriteLine("[hud] ordinary");
        Assert.Equal(0, flushes);
        writer.Dispose();
        // The lazy flush would only come in an hour: closing does it now, and nothing after the stream is closed.
        Assert.Equal(1, flushes);
        writer.FlushToDisk();
        writer.Dispose();
        Assert.Equal(1, flushes);
        Assert.False(closedInFlush);

        // Nothing written since the last flush: closing adds none.
        var quiet = 0;
        var idle = new LineWriter(new MemoryStream(), () => Moment, _ => Interlocked.Increment(ref quiet));
        idle.WriteLine("x");
        idle.FlushToDisk();
        idle.Dispose();
        Assert.Equal(1, quiet);

        // A flush running while the log closes ends on the open stream; the close waits for it.
        using var inFlush = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var open = true;
        var busy = new LineWriter(new MemoryStream(), () => Moment, s =>
        {
            inFlush.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            open &= s.CanWrite;
        });
        busy.WriteLine("y");
        var flushing = Task.Run(busy.FlushToDisk);
        Assert.True(inFlush.Wait(TimeSpan.FromSeconds(10)));
        var closing = Task.Run(busy.Dispose);
        Assert.False(closing.Wait(TimeSpan.FromMilliseconds(200)));
        release.Set();
        Assert.True(closing.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(flushing.Wait(TimeSpan.FromSeconds(10)));
        Assert.True(open);
    }

    [Fact]
    public void Zero_bytes_a_hard_reset_left_at_the_end_are_trimmed()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-log-").FullName;
        var file = Path.Combine(dir, "couchhud.log");
        try
        {
            // A complete line, then a block of zeros longer than one read.
            File.WriteAllBytes(file, [.. "2026-10-20 12:30:05.123 [app] started\r\n"u8, .. new byte[10000]]);
            Assert.Equal(10000, LineWriter.TrimTrailingZeros(file));
            Assert.Equal("2026-10-20 12:30:05.123 [app] started\r\n", File.ReadAllText(file));
            // Nothing to trim: the file stays as it is.
            Assert.Equal(0, LineWriter.TrimTrailingZeros(file));
            Assert.Equal("2026-10-20 12:30:05.123 [app] started\r\n", File.ReadAllText(file));

            // A line cut off by the reset gets its line break, so the next one starts on its own.
            File.WriteAllBytes(file, [.. "a\r\nhalf a li"u8, 0, 0, 0]);
            Assert.Equal(3, LineWriter.TrimTrailingZeros(file));
            Assert.Equal("a\r\nhalf a li\r\n", File.ReadAllText(file));

            // Only zeros: an empty log.
            File.WriteAllBytes(file, new byte[5000]);
            Assert.Equal(5000, LineWriter.TrimTrailingZeros(file));
            Assert.Equal(0, new FileInfo(file).Length);

            // Held by the other process: left for the next start.
            File.WriteAllBytes(file, [.. "a\r\n"u8, 0, 0]);
            using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                Assert.Equal(0, LineWriter.TrimTrailingZeros(file));
            Assert.Equal(5, new FileInfo(file).Length);
            Assert.Equal(0, LineWriter.TrimTrailingZeros(Path.Combine(dir, "missing.log")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void The_log_file_opened_for_appending_only_can_be_flushed_to_the_disk()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(Directory.CreateTempSubdirectory("couchhud-log-").FullName, "couchhud.log");
        // Opened like Log.Init does it.
        using (var stream = System.IO.FileSystemAclExtensions.Create(new FileInfo(file), FileMode.OpenOrCreate, System.Security.AccessControl.FileSystemRights.AppendData,
                   FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None, null))
        {
            stream.Write("line\r\n"u8);
            stream.Flush(flushToDisk: true);
        }
        Assert.Equal("line\r\n", File.ReadAllText(file));
        Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
    }
}
