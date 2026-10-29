using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>
/// Which plugins Offline mode (Settings › Privacy) pauses. The plugins run inside CouchHUD, so their own sockets cannot be fenced in from
/// outside; what the host can do is not load a plugin that says it needs the internet. The SDK says it with the capabilities a plugin declares
/// (the code lint demands them): <c>network:internet</c> is "the internet or the home network", <c>network:lan</c> "the home network only",
/// <c>network:loopback</c> "this PC only". A plugin that talks to the home network only (a TV, Home Assistant) declares <c>network:lan</c>
/// and keeps running; one that declares <c>network:internet</c> is paused, whether it really needs the internet or only did not say otherwise.
/// </summary>
public static class PluginOfflinePolicy
{
    /// <summary>True when Offline is on and the plugin (by its granted capabilities) may reach the internet.</summary>
    public static bool Pauses(bool offline, IEnumerable<string>? capabilities) =>
        offline && capabilities is not null && capabilities.Contains(PluginCapabilities.NetworkInternet, StringComparer.Ordinal);
}
