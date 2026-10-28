using System.Runtime.CompilerServices;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// The list of approved plugins is never read or written without its secret. The shell hands it over at start; the tests use this
/// throwaway one (the same bytes the plugin tests pass explicitly), so code that creates its own <see cref="PluginTrust"/> signs alike.
/// </summary>
internal static class TestTrustKey
{
    public static readonly byte[] Bytes = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [ModuleInitializer]
    internal static void Init()
    {
        PluginTrust.UseKey(() => Bytes);
        // Whatever proxy the machine running the tests has: the catalog tests do not go through it (tests that want one hand it in).
        CatalogHosts.SystemProxy = null;
    }
}
