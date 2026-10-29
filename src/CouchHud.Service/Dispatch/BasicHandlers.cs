using CouchHud.Service.Contracts;
using CouchHud.Service.Logging;

namespace CouchHud.Service.Dispatch;

/// <summary>What the service knows about itself for Hello and Status.</summary>
public sealed class ServiceState(string version, Func<bool> unsafeInstall, Func<long> uptimeSeconds)
{
    public string Version { get; } = version;

    public bool UnsafeInstall => unsafeInstall();

    public long UptimeSeconds => uptimeSeconds();

    /// <summary>State of the task migration when nothing better is known ("none": no migration support).</summary>
    public string Migration { get; set; } = "none";

    /// <summary>The migration state of one account (P5 sets it); Hello and Status ask it for the caller's own SID only.</summary>
    public Func<string, string>? MigrationLookup { get; set; }

    public string MigrationOf(string sid) => MigrationLookup?.Invoke(sid) ?? Migration;

    /// <summary>The state of the last release of every account (P5 sets it), for Status.</summary>
    public Func<string>? ReleaseLookup { get; set; }

    /// <summary>The caller's own codes and lock mode (P9 sets it), for Status: couch code, parents' code, stored mode (nulls: unknown).</summary>
    public Func<string, (bool? Couch, bool? Parent, string? Mode)>? CodesLookup { get; set; }

    /// <summary>Whether the caller's lock was taken over from 0.4.0 and waits for an administrator's confirmation (P5 sets it).</summary>
    public Func<string, bool>? LegacyLookup { get; set; }

    /// <summary>Whether the code file is damaged (P9 sets it), for Status: the shell then says so and offers the administrator's reset.</summary>
    public Func<bool>? CodesDamagedLookup { get; set; }
}

/// <summary>
/// The harmless operations of this package: health and status, and the caller's own recent events. Everything else answers
/// <see cref="ErrorCodes.NotImplemented"/> until its package (P5, P6, P9) adds a handler.
/// </summary>
public static class BasicHandlers
{
    /// <summary>Status for the caller: only its own codes and mode (the SID from the token), never another account's.</summary>
    private static StatusData StatusOf(ServiceState state, string sid)
    {
        (bool? Couch, bool? Parent, string? Mode) codes = (null, null, null);
        bool? legacy = null;
        bool? damaged = null;
        try
        {
            if (state.CodesDamagedLookup is { } damagedLookup) damaged = damagedLookup() ? true : null;
            if (state.CodesLookup is { } lookup) codes = lookup(sid);
            if (state.LegacyLookup is { } legacyLookup) legacy = legacyLookup(sid) ? true : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
        return new StatusData(state.Version, state.UnsafeInstall, state.MigrationOf(sid), state.UptimeSeconds, state.ReleaseLookup?.Invoke(), codes.Couch, codes.Parent, codes.Mode,
            legacy, damaged);
    }

    public static Dictionary<Operation, Handler> Create(ServiceState state, RecentEvents events, Func<long> wallClockMs)
    {
        var handlers = new Dictionary<Operation, Handler>();
        // The migration state is the caller's own (token SID), never another account's.
        handlers[Operation.Hello] = (context, _) => ValueTask.FromResult(ProtocolJson.Serialize(Response<HelloData>.Success(
            new HelloData(Protocol.Version, state.Version, handlers.Keys.Select(o => o.ToString()).Order(StringComparer.Ordinal).ToArray(), state.UnsafeInstall, state.MigrationOf(context.Caller.Sid)))));
        handlers[Operation.Status] = (context, _) => ValueTask.FromResult(ProtocolJson.Serialize(Response<StatusData>.Success(
            StatusOf(state, context.Caller.Sid))));
        handlers[Operation.Ping] = (_, _) => ValueTask.FromResult(ProtocolJson.Serialize(Response<PingData>.Success(new PingData(wallClockMs()))));
        handlers[Operation.GetRecentEvents] = (context, _) =>
        {
            var count = context.Request.GetArgs<GetRecentEventsArgs>().Count;
            // The caller's own SID from the token: no argument can name another account.
            return ValueTask.FromResult(ProtocolJson.Serialize(Response<EventsData>.Success(new EventsData(events.For(context.Caller.Sid, count)))));
        };
        return handlers;
    }
}
