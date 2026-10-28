using System.Net.Http;
using CouchHud.Core.Config;
using CouchHud.Core.Integrations;
using CouchHud.Core.Localization;
using CouchHud.Shell.Services;

namespace CouchHud.Shell;

/// <summary>
/// "Sign in with Home Assistant" (Home Assistant's OAuth flow, see <see cref="HomeAssistantLogin"/>): the sign-in page opens in the
/// couch browser, the browser hands the redirect with the code back without loading it, CouchHUD exchanges the code (with the PKCE
/// verifier) at the same Home Assistant and keeps the refresh token like the long-lived one (DPAPI). Starting, finishing and signing out
/// need the same as setting the token always did: an unlocked couch (the bridge refuses while locked, and every step here checks
/// again). The sign-in page never counts as use of the browser: only a person's input extends the unlock (BrowserUnlock). The old
/// connection stays until the new sign-in is saved; every change of the connection bumps its version (<see cref="HomeAssistantSignIn"/>),
/// and a sign-in finishing after such a change is revoked instead of kept.
/// </summary>
public sealed partial class Hud
{
    private static readonly HttpClient HaHttp = HomeAssistantClient.CreateHttpClient();
    private static readonly HomeAssistantAuth HaAuth = new(HaHttp);
    private static readonly HomeAssistantSignIn HaSignIn = new(HaAuth);
    private readonly HomeAssistantLogin _haLogin = new();

    // The Home Assistant whose sign-in page the couch browser shows, from opening it until the browser closes (not only while the
    // sign-in is pending): its addresses go to the bar without query (no state) and the page stays out of the recent sites.
    private string? _haSignInPage;

    /// <summary>The stored sign-in, if it belongs to the stored address.</summary>
    private static HomeAssistantSession? RestoreHaSession(AppConfig c)
    {
        var refresh = Secrets.Unprotect(c.HomeAssistantRefreshToken);
        if (refresh.Length == 0 || HomeAssistantLogin.NormalizeBase(c.HomeAssistantUrl) is not { } root) return null;
        return new HomeAssistantSession(HaAuth, root, refresh);
    }

    /// <summary>
    /// Bridge <c>signInHomeAssistant</c>: opens Home Assistant's sign-in page for the address in the couch browser. The address and the
    /// old connection stay as they are until the new sign-in is saved (a cancelled sign-in to another address loses nothing). Opening it
    /// is not a person's use of the browser (no BrowserOpenedByPerson): the page cannot keep the couch unlocked.
    /// </summary>
    public async Task<object> SignInHomeAssistantAsync(string? url)
    {
        RequireUnlocked();
        var root = HomeAssistantLogin.NormalizeBase(url) ?? throw new ArgumentException(Strings.Get("ha.signInBadAddress"));
        var page = _haLogin.Start(root, HaSignIn.Generation);
        _haSignInPage = root;
        try
        {
            await OpenBrowserAsync(page);
        }
        catch
        {
            _haLogin.Cancel();
            _haSignInPage = null;
            throw;
        }
        Console.WriteLine($"[ha] sign-in page opened ({new Uri(root).Host})");
        return new { ok = true };
    }

    /// <summary>Bridge <c>signOutHomeAssistant</c>: forgets the sign-in and revokes it at Home Assistant (best effort, 15 s at most).</summary>
    public async Task<object> SignOutHomeAssistantAsync()
    {
        RequireUnlocked();
        _haLogin.Cancel();
        HaSignIn.Changed();
        var session = _home.Session;
        _home.Session = null;
        config.UpdateOrThrow(c => c.HomeAssistantRefreshToken = "");
        // A session Home Assistant already refused needs no revoke (and gets no further request).
        var revoked = session is null || session.Dead || await HaSignIn.RevokeQuietlyAsync(session);
        Console.WriteLine($"[ha] signed out (revoked: {revoked})");
        return new { ok = true, revoked };
    }

    /// <summary>
    /// The Home Assistant address setting changed: a token belongs to the server it was made for, so another server drops the long-lived
    /// token and the sign-in (revoked at the old address, in the background) and ends a running sign-in.
    /// </summary>
    private void SetHomeAssistantUrl(string url)
    {
        var sameServer = HomeAssistantPolicy.SameServer(config.Current.HomeAssistantUrl, url);
        var old = sameServer ? null : _home.Session;
        config.UpdateOrThrow(c =>
        {
            c.HomeAssistantUrl = url;
            if (!sameServer)
            {
                c.HomeAssistantToken = "";
                c.HomeAssistantRefreshToken = "";
            }
        });
        _home.BaseUrl = url;
        if (sameServer) return;
        HaSignIn.Changed();
        _home.Token = "";
        _home.Session = null;
        _haLogin.Cancel();
        RevokeHaInBackground(old);
    }

    /// <summary>The long-lived token (expert way) replaces a sign-in: one credential at a time, the sign-in is revoked.</summary>
    private void SetHomeAssistantToken(string token)
    {
        var old = _home.Session;
        config.UpdateOrThrow(c =>
        {
            c.HomeAssistantToken = Secrets.Protect(token);
            if (token.Length > 0) c.HomeAssistantRefreshToken = "";
        });
        _home.Token = token;
        if (token.Length == 0) return;
        // A sign-in still running would replace the token just set: it is not kept any more.
        HaSignIn.Changed();
        if (old is null) return;
        _home.Session = null;
        RevokeHaInBackground(old);
    }

    /// <summary>
    /// The couch browser caught the redirect address (never loaded). Taken only for the running sign-in (state), from its Home
    /// Assistant's page, on an unlocked couch; then the code is exchanged at that address. Every answer of the sign-in (code, refusal,
    /// wrong page, too late) closes the browser with the sign-in page, so the page address with the state leaves the screen and the
    /// browser's back list. Nothing of the address or the code goes to the log, only the outcome.
    /// </summary>
    private void OnHaSignInRedirect(BrowserPane pane, string url, string pageUrl)
    {
        var result = _haLogin.Complete(url, pageUrl);
        Console.WriteLine($"[ha] sign-in redirect: {result.Outcome}");
        // Not this sign-in (a page making up a callback, an old one): nothing to say, the page stays.
        if (result.Outcome is HaCallbackOutcome.NoFlow or HaCallbackOutcome.WrongState or HaCallbackOutcome.Malformed) return;
        if (_browser == pane) CloseBrowser(true);
        string? error = result.Outcome switch
        {
            HaCallbackOutcome.Expired => Strings.Get("ha.signInExpired"),
            HaCallbackOutcome.Denied => Strings.Get("ha.signInDenied"),
            HaCallbackOutcome.WrongPage => Strings.Get("ha.signInWrongPage"),
            _ when Locked => Strings.Get("ha.signInLocked"),
            _ => null
        };
        if (error is not null)
        {
            Emit("homeAssistantSignIn", new { ok = false, error });
            return;
        }
        _ = FinishHaSignInAsync(result);
    }

    private async Task FinishHaSignInAsync(HaCallback callback)
    {
        HaFinish finish;
        try
        {
            // Back on the UI thread after the exchange: locked meanwhile, the new sign-in is not kept.
            finish = await HaSignIn.FinishAsync(callback, () => Locked ? Strings.Get("ha.signInLocked") : null, CommitHaSession);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[ha] sign-in failed: {ex.GetType().Name}");
            finish = new HaFinish(HaFinishOutcome.Failed, Strings.Get("ha.signInFailed", ex.GetType().Name));
        }
        Console.WriteLine($"[ha] sign-in: {finish.Outcome}");
        Emit("homeAssistantSignIn", finish.Ok ? new { ok = true, error = (string?)null } : new { ok = false, error = finish.Error });
    }

    /// <summary>
    /// Saves a finished sign-in in one config write (address, refresh token encrypted, long-lived token dropped) and only then puts it
    /// in use; the session it replaces is revoked at its own address. Throws when saving fails: nothing changed then.
    /// </summary>
    private void CommitHaSession(HomeAssistantSession session)
    {
        var old = _home.Session;
        var moved = !HomeAssistantPolicy.SameServer(config.Current.HomeAssistantUrl, session.BaseUrl);
        config.UpdateOrThrow(c =>
        {
            if (moved)
            {
                c.HomeAssistantUrl = session.BaseUrl;
                // The old server's entity ids mean nothing on the new one: the choice and the guide pins start over.
                c.HomeAssistantEntities = [];
                c.HomeAssistantPinned = [];
            }
            c.HomeAssistantRefreshToken = Secrets.Protect(session.RefreshToken);
            c.HomeAssistantToken = "";
        });
        if (moved) _home.BaseUrl = session.BaseUrl;
        _home.Token = "";
        _home.Session = session;
        if (old is not null && old.RefreshToken != session.RefreshToken) RevokeHaInBackground(old);
    }

    /// <summary>Revokes a replaced session at its own Home Assistant in the background; one Home Assistant already refused is left alone.</summary>
    private static void RevokeHaInBackground(HomeAssistantSession? old)
    {
        if (old is null || old.Dead) return;
        _ = Task.Run(async () =>
        {
            var revoked = await HaSignIn.RevokeQuietlyAsync(old);
            if (!revoked) Console.Error.WriteLine("[ha] revoke failed");
        });
    }
}
