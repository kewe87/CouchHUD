using CouchHud.Core.Localization;

namespace CouchHud.Core.Integrations;

/// <summary>How finishing a sign-in ended (see <see cref="HomeAssistantSignIn.FinishAsync"/>).</summary>
public enum HaFinishOutcome
{
    /// <summary>Signed in: the new session is saved and in use.</summary>
    Ok,

    /// <summary>Home Assistant gave no tokens (code refused, not reachable, odd answer).</summary>
    Failed,

    /// <summary>The connection changed while the code was exchanged (signed out, another address or token): the result is revoked.</summary>
    Stale,

    /// <summary>The host refused to take it (the couch got locked meanwhile): revoked.</summary>
    Blocked,

    /// <summary>Saving failed: revoked, so no sign-in is left at Home Assistant that CouchHUD does not know of.</summary>
    NotSaved
}

/// <summary>The end of a sign-in; the error is a CouchHUD text for the person, never with a code or a token in it.</summary>
public sealed record HaFinish(HaFinishOutcome Outcome, string? Error = null)
{
    public bool Ok => Outcome == HaFinishOutcome.Ok;
}

/// <summary>
/// The connection's version and the last step of a sign-in. Every change of the Home Assistant connection (sign-out, another address,
/// a long-lived token, a finished sign-in) bumps <see cref="Generation"/>; a sign-in carries the version it started with (see
/// <see cref="HomeAssistantLogin.Start"/>) and its tokens are kept only if nothing changed in between. Whatever is not kept is revoked
/// at once at the Home Assistant that issued it: no valid sign-in is left behind that CouchHUD has forgotten.
/// </summary>
public sealed class HomeAssistantSignIn(HomeAssistantAuth auth)
{
    private long _generation;

    /// <summary>The connection's current version.</summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>The connection changed: a sign-in started before is not kept any more.</summary>
    public long Changed() => Interlocked.Increment(ref _generation);

    /// <summary>
    /// Exchanges the code of an Ok callback (with its PKCE verifier) at the address the sign-in started with. Then, on the caller's
    /// context: discarded and revoked when the connection changed meanwhile or <paramref name="blocked"/> says so; otherwise
    /// <paramref name="commit"/> saves the new session (it throws when saving fails: revoked as well). A kept sign-in bumps the version.
    /// </summary>
    public async Task<HaFinish> FinishAsync(HaCallback callback, Func<string?> blocked, Action<HomeAssistantSession> commit, CancellationToken ct = default)
    {
        if (!callback.Ok || callback.Code is null || callback.BaseUrl is null) return new(HaFinishOutcome.Failed, Strings.Get("ha.signInFailed", callback.Outcome));
        HaTokens tokens;
        try
        {
            tokens = await auth.ExchangeCodeAsync(callback.BaseUrl, callback.Code, callback.Verifier, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            // The messages are CouchHUD's own texts (status, redirect target without query): no code, no token.
            var reason = ex is OperationCanceledException ? Strings.Get("ha.status", "timeout") : ex.Message;
            return new(HaFinishOutcome.Failed, Strings.Get("ha.signInFailed", reason));
        }
        var session = new HomeAssistantSession(auth, callback.BaseUrl, tokens.RefreshToken!);
        session.Seed(tokens);
        if (Generation != callback.Generation)
        {
            await RevokeQuietlyAsync(session);
            return new(HaFinishOutcome.Stale, Strings.Get("ha.signInStale"));
        }
        if (blocked() is { } reasonBlocked)
        {
            await RevokeQuietlyAsync(session);
            return new(HaFinishOutcome.Blocked, reasonBlocked);
        }
        try
        {
            commit(session);
        }
        catch (Exception)
        {
            await RevokeQuietlyAsync(session);
            return new(HaFinishOutcome.NotSaved, Strings.Get("ha.signInNotSaved"));
        }
        Changed();
        return new(HaFinishOutcome.Ok);
    }

    /// <summary>Revokes a session at its own Home Assistant; false when that did not work (nothing about the token in any message).</summary>
    public async Task<bool> RevokeQuietlyAsync(HomeAssistantSession session)
    {
        try
        {
            return await auth.RevokeAsync(session.BaseUrl, session.RefreshToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }
}
