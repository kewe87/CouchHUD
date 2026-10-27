using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CouchHud.Core.Localization;

namespace CouchHud.Core.Integrations.CaseHud;

public static class CaseHudClientEventsExtensions
{
    /// <summary>No byte for this long = dead connection (CaseHUD pings every 15 s).</summary>
    public static readonly TimeSpan Silence = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Reads <c>/api/v1/events</c> (SSE) until the connection ends or fails and hands every event to
    /// <paramref name="onEvent"/> (name, data). The <see cref="HttpClient"/> must have an infinite Timeout.
    /// </summary>
    public static async Task ReadEventsAsync(this CaseHudClient client, HttpClient http, Action<string, string> onEvent, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, client.EventsUrl);
        client.Authorize(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(Silence);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token);
            if (!response.IsSuccessStatusCode) throw await CaseHudClient.ReadError(response, idle.Token);
            await using var stream = await response.Content.ReadAsStreamAsync(idle.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string name = "message";
            var data = new StringBuilder();
            while (true)
            {
                var line = await reader.ReadLineAsync(idle.Token);
                if (line is null) return;
                if (line.Length > CaseHudClient.MaxJsonBytes) throw new CaseHudException("bad_response", Strings.Get("casehud.badResponse"));
                idle.CancelAfter(Silence);
                if (line.Length == 0)
                {
                    if (data.Length > 0) onEvent(name, data.ToString());
                    name = "message";
                    data.Clear();
                }
                else if (line.StartsWith("event:", StringComparison.Ordinal)) name = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0) data.Append('\n');
                    data.Append(line[5..].TrimStart());
                    if (data.Length > CaseHudClient.MaxJsonBytes) throw new CaseHudException("bad_response", Strings.Get("casehud.badResponse"));
                }
                // ":" comments and other fields are ignored.
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or IOException or OperationCanceledException) && !ct.IsCancellationRequested)
        {
            throw new CaseHudException("unreachable", Strings.Get("casehud.unreachable"));
        }
    }
}

/// <summary>
/// Keeps an event stream open to whatever CaseHUD is running right now and reconnects with a growing pause
/// (1 s, 2 s, 4 s … 30 s). Status events are delivered as typed objects; <c>connected</c> tells the UI when
/// the stream comes and goes. When CaseHUD refuses the stream (503, more than 8 clients) the status is polled every 5 s instead.
/// Nothing but cancellation ends the loop: an error in a handler or an odd answer only costs one round.
/// A refusal with 403 api_disabled (the API was switched off in CaseHUD) is reported to <c>apiDisabled</c>; <see cref="Interrupt"/>
/// ends the open stream and the pause at once (api.json deleted or rewritten with a new port and token).
/// </summary>
public sealed class CaseHudEventLoop(HttpClient http, Func<CaseHudClient?> client, Action<CaseHudStatus> onStatus, Action<bool> connected, Func<TimeSpan, CancellationToken, Task>? delay = null, Action? apiDisabled = null)
{
    private readonly object _roundLock = new();
    private CancellationTokenSource? _round;
    private volatile bool _interrupted;

    /// <summary>Ends the current stream (or pause) now; the next round asks <c>client</c> again, without the back-off.</summary>
    public void Interrupt()
    {
        _interrupted = true;
        lock (_roundLock)
        {
            try { _round?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private CancellationToken NewRound(CancellationToken ct)
    {
        lock (_roundLock)
        {
            _round?.Dispose();
            _round = CancellationTokenSource.CreateLinkedTokenSource(ct);
            return _round.Token;
        }
    }

    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(5);

    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    public static TimeSpan Backoff(int failures) => TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, Math.Pow(2, Math.Clamp(failures - 1, 0, 6))));

    public async Task RunAsync(CancellationToken ct)
    {
        var failures = 0;
        var wasConnected = false;
        while (!ct.IsCancellationRequested)
        {
            _interrupted = false;
            var round = NewRound(ct);
            var c = client();
            var gotEvent = false;
            var poll = false;
            if (c is not null)
            {
                try
                {
                    await c.ReadEventsAsync(http, (name, data) =>
                    {
                        if (name != "status") return;
                        try
                        {
                            var status = JsonSerializer.Deserialize<CaseHudStatus>(data, CaseHudJson.Options);
                            if (status is null) return;
                            gotEvent = true;
                            if (!wasConnected) { wasConnected = true; Safe(() => connected(true)); }
                            Safe(() => onStatus(status));
                        }
                        catch (JsonException) { }
                    }, round);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (OperationCanceledException) { /* interrupted: the files changed */ }
                catch (CaseHudException ex) when (CaseHudState.IsApiDisabled(ex)) { Safe(() => apiDisabled?.Invoke()); }
                catch (CaseHudException ex) { poll = ex.Status == 503; }
                catch (Exception) { /* whatever it was: the next round tries again */ }

                if (poll)
                {
                    try
                    {
                        var status = await c.StatusAsync(round);
                        if (!wasConnected) { wasConnected = true; Safe(() => connected(true)); }
                        Safe(() => onStatus(status));
                        try { await _delay(PollEvery, round); }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                        catch (OperationCanceledException) { }
                        continue;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (CaseHudException ex) when (CaseHudState.IsApiDisabled(ex)) { Safe(() => apiDisabled?.Invoke()); poll = false; }
                    catch (Exception) { poll = false; }
                }
            }
            if (wasConnected) { wasConnected = false; Safe(() => connected(false)); }
            // Interrupted because the files changed: straight to the next round with the new port and token.
            if (_interrupted)
            {
                failures = 0;
                continue;
            }
            failures = gotEvent ? 1 : failures + 1;
            try { await _delay(Backoff(failures), round); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (OperationCanceledException) { failures = 0; }
        }
    }

    /// <summary>A handler that throws must not end the loop.</summary>
    private static void Safe(Action action)
    {
        try { action(); }
        catch (Exception) { }
    }
}
