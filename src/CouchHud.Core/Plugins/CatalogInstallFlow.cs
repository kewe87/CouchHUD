namespace CouchHud.Core.Plugins;

/// <summary>
/// The order of the parents' code in a catalog install: the gate is checked and its grant used up exactly once, in the catalog service's approval
/// callback (the last gate, after the statement checked out and before the download). A skin of a catalog the user added may be looked at earlier
/// (<c>early</c>, so a missing code fails before any download is tracked), but that early look must only check, never consume: the callback asks
/// the same gate again, and a grant used up twice would leave the install in an endless ask loop.
/// </summary>
public static class CatalogInstallFlow
{
    /// <param name="extraSkin">The entry is a skin of an added catalog (third-party): it has an early look.</param>
    /// <param name="early">Looks at the gate without using the grant up (throws when refused).</param>
    /// <param name="gate">Checks the gate for the request and uses the grant up (throws when refused).</param>
    /// <param name="install">Runs the install with the approval callback the catalog service asks.</param>
    public static Task<T> RunAsync<T>(bool extraSkin, Action early, Action<PluginInstallRequest> gate, Func<Func<PluginInstallRequest, bool>, Task<T>> install)
    {
        if (extraSkin) early();
        return install(request =>
        {
            gate(request);
            return true;
        });
    }
}
