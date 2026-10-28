using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Plugins;
using CouchHud.Core.Localization;

namespace CouchHud.Shell;

/// <summary>
/// The web sign-in of a plugin with the capability "browser.signin" (first-party only, see <see cref="CouchHud.Sdk.IPluginSignIn"/>): the
/// sign-in page opens in the couch browser, the browser cancels the redirect to the host the plugin named before any request goes out and
/// the HUD hands the address to the plugin once. The rules: only on the unlocked couch
/// (checked again when the redirect arrives), opening the page is not a person's use of the browser, every answer of the sign-in closes
/// the browser (the address with the state leaves the screen and the back list), the redirect host never resolves (".invalid"), and
/// nothing of the address goes to a log. One sign-in at a time.
/// <para>
/// The redirect host stays cancelled in the browser until the browser closes, also after the sign-in ended or ran out: a late redirect
/// must never turn into a normal navigation, an error page or a history entry. Only a redirect that carries the state of the page the
/// sign-in opened is handed to the plugin; a stray one (no state, another state, malformed) is ignored and the page stays open.
/// </para>
/// </summary>
public sealed partial class Hud
{
    [GeneratedRegex(@"^([a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?\.){1,4}invalid\z", RegexOptions.CultureInvariant)]
    private static partial Regex PluginRedirectHostPattern();

    private sealed record PluginSignInState(string PluginId, string Host, Action<string, string> Receiver, string State, string Root);

    /// <summary>The root of the page a plugin's sign-in opened in the browser (until the browser closes); null when none.</summary>
    private volatile string? _signInPage;

    private readonly object _pluginSignInGate = new();
    private volatile PluginSignInState? _pluginSignIn;

    private void StartPluginSignIn()
    {
        plugins.SignInOpener = OpenPluginSignIn;
        plugins.SignInCanceller = id =>
        {
            if (_pluginSignIn?.PluginId.Equals(id, StringComparison.OrdinalIgnoreCase) == true) EndPluginSignIn();
        };
    }

    /// <summary>The state parameter of a sign-in address (exactly one, URL-safe characters, at most 256); null for anything else.</summary>
    internal static string? SignInStateOf(string? url) => CouchHud.Core.Plugins.PluginSignInRules.StateOf(url);

    /// <summary>The checks first, on the calling thread, so the plugin learns about a refusal; the browser opens on the UI thread.</summary>
    private bool OpenPluginSignIn(string pluginId, string pluginName, string pageUrl, string redirectHost, Action<string, string> receiver)
    {
        if (!IsUnlocked || !Areas.On(FeatureAreas.Browser)) return false;
        if (!PluginRedirectHostPattern().IsMatch(redirectHost ?? "")) return false;
        // The page's address always has a query (client, redirect, state): the root is its origin, the rest is checked as a whole.
        // Without a state nothing could tell the sign-in's redirect from a stray one.
        if (PluginSignInRules.ParsePage(pageUrl) is not { } checkedPage || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var page)) return false;
        var root = checkedPage.Root;
        var pageState = checkedPage.State;
        PluginSignInState state;
        lock (_pluginSignInGate)
        {
            if (_pluginSignIn is { } running && !running.PluginId.Equals(pluginId, StringComparison.OrdinalIgnoreCase)) return false;
            state = new PluginSignInState(pluginId, redirectHost!, receiver, pageState, root);
            _pluginSignIn = state;
        }
        Ui(async () =>
        {
            try
            {
                var pane = Browser();
                pane.PluginRedirectHost = redirectHost;
                _signInPage = root;
                await OpenBrowserAsync(pageUrl);
                // The person sees which plugin opened the browser.
                Emit("toast", Strings.Get("plugin.signInOpened", pluginName));
                Console.WriteLine($"[plugins] {pluginId}: sign-in page opened ({page.Host})");
            }
            catch (Exception ex) when (ex is ArgumentException or LockedException or InvalidOperationException)
            {
                Console.Error.WriteLine($"[plugins] {pluginId}: sign-in page not opened: {ex.GetType().Name}");
                EndPluginSignIn(state);
                ResetPluginRedirectHost();
            }
        });
        return true;
    }

    /// <summary>The sign-in is over (answered, cancelled, ran out). The browser keeps cancelling the redirect host until it closes.</summary>
    private void EndPluginSignIn(PluginSignInState? only = null)
    {
        lock (_pluginSignInGate)
        {
            if (only is null || _pluginSignIn == only) _pluginSignIn = null;
        }
    }

    /// <summary>The browser is closed: now the redirect host is an ordinary address again (the next sign-in sets it anew).</summary>
    private void ResetPluginRedirectHost()
    {
        if (_browser is { } pane) pane.PluginRedirectHost = null;
    }

    /// <summary>
    /// The couch browser caught a plugin's redirect address (never loaded, cancelled in the pane whatever happens here). Without a running
    /// sign-in, or with a state that is not the sign-in's, nothing happens and the page stays. The right state is used once: the browser
    /// closes, and the address goes to the plugin on a worker thread, only from the sign-in's own page and on the unlocked couch.
    /// </summary>
    private void OnPluginSignInRedirect(BrowserPane pane, string url, string pageUrl)
    {
        var state = _pluginSignIn;
        if (state is null) return;
        if (SignInStateOf(url) is not { } got || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(got), Encoding.UTF8.GetBytes(state.State))) return;
        lock (_pluginSignInGate)
        {
            // One use: whatever happens next, a second redirect is not taken.
            if (_pluginSignIn != state) return;
            _pluginSignIn = null;
        }
        if (_browser == pane) CloseBrowser(true);
        Console.WriteLine($"[plugins] {state.PluginId}: sign-in redirect");
        if (!PluginSignInRules.SameOrigin(pageUrl, state.Root))
        {
            Emit("toast", Strings.Get("plugin.signInWrongPage"));
            return;
        }
        if (Locked)
        {
            Emit("toast", Strings.Get("plugin.signInLocked"));
            return;
        }
        _ = Task.Run(() =>
        {
            try
            {
                state.Receiver(url, pageUrl);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[plugins] {state.PluginId}: sign-in receiver failed: {ex.GetType().Name}");
            }
        });
    }
}
