using System.Text.Json;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// Offline mode fences a plugin that may reach the internet: the host lists nothing of it, calls nothing of it, delivers no events to it and
/// refuses its calls into the host, and everything is back when Offline goes. A plugin that declares network:lan only is never touched.
/// </summary>
public class PluginOfflinePauseTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("couchhud-pause-").FullName;
    private bool _offline;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private sealed class Provider : ILibraryProvider
    {
        public int Asked;
        public string Name => "p";

        public Task<IReadOnlyList<LibraryItem>> GetItemsAsync(CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult<IReadOnlyList<LibraryItem>>([new LibraryItem { Id = "x", Source = "plugin", Name = "X", Type = ItemType.Game, Launch = new LaunchTarget(LaunchKind.Exe, "x.exe") }]);
        }
    }

    private (PluginManager Manager, IPluginContext Context, Provider Provider, int[] Ran) Setup(string capability, string id)
    {
        var m = new PluginManager([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true) { OfflineNow = () => _offline };
        var caps = new HashSet<string> { PluginCapabilities.Library, PluginCapabilities.Pages, PluginCapabilities.QuickActions, PluginCapabilities.Assistant, PluginCapabilities.Notify, capability };
        var ctx = m.ContextFor(id, new PluginManager.PluginGrant(id, PluginOrigin.FirstParty, caps), markLoaded: true);
        var provider = new Provider();
        var ran = new[] { 0 };
        ctx.RegisterLibraryProvider(provider);
        ctx.RegisterPage(new PluginPage { Id = "pg", Title = "Page", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });
        ctx.RegisterQuickAction(new QuickAction { Id = "q", Label = "Q", Run = _ => Task.FromResult<string?>(null) });
        ctx.RegisterTool(new PluginTool { Name = "act", Description = "x", Run = (_, _) => { ran[0]++; return Task.FromResult("ran"); } });
        return (m, ctx, provider, ran);
    }

    [Fact]
    public async Task A_running_plugin_is_listed_and_called_as_usual()
    {
        var (m, _, provider, ran) = Setup(PluginCapabilities.NetworkInternet, "net");
        Assert.Single(m.Pages);
        Assert.Single(m.QuickActions);
        Assert.Single(m.ToolSpecs);
        Assert.NotNull(m.FindPage("net", "pg"));
        Assert.NotNull(m.FindAction("net", "q"));
        Assert.Single(await m.LibraryProviders[0].GetItemsAsync(default));
        Assert.Equal("ran", await m.CallToolAsync(m.ToolSpecs[0].Name, JsonDocument.Parse("{}").RootElement));
        Assert.Equal(1, provider.Asked);
    }

    [Fact]
    public async Task Offline_stops_the_host_from_listing_or_calling_a_plugin_that_may_reach_the_internet()
    {
        var (m, ctx, provider, ran) = Setup(PluginCapabilities.NetworkInternet, "net");
        var name = m.ToolSpecs[0].Name;
        _offline = true;

        Assert.True(m.IsPaused("net"));
        Assert.Empty(m.Pages);
        Assert.Empty(m.QuickActions);
        Assert.Empty(m.Settings);
        Assert.Empty(m.ToolSpecs);
        Assert.Null(m.FindPage("net", "pg"));
        Assert.Null(m.FindAction("net", "q"));
        Assert.False(m.HasTool(name));
        Assert.Null(m.FindTool(name));
        await Assert.ThrowsAsync<ArgumentException>(() => m.CallToolAsync(name, JsonDocument.Parse("{}").RootElement));
        Assert.Equal(0, ran[0]);
        // The provider is not asked and lists nothing.
        Assert.Empty(await m.LibraryProviders[0].GetItemsAsync(default));
        Assert.Equal(0, provider.Asked);
        // The plugin's own calls into the host are refused.
        // ... but never with an exception: a plugin's own background thread would not expect one. Harmless members work, actions do nothing.
        ctx.Log("still logging");
        Assert.False(string.IsNullOrEmpty(ctx.Language));
        _ = ctx.Locked;
        _ = ctx.DataDirectory;
        ctx.Invalidate("pg");
        ctx.Notify("t", "b");
        ctx.RegisterPage(new PluginPage { Id = "late", Title = "L", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });
        Assert.Empty(m.Pages);

        // Offline off: everything is back.
        _offline = false;
        Assert.False(m.IsPaused("net"));
        Assert.Single(m.Pages);
        Assert.Equal("ran", await m.CallToolAsync(name, JsonDocument.Parse("{}").RootElement));
        Assert.Single(await m.LibraryProviders[0].GetItemsAsync(default));
    }

    [Fact]
    public async Task A_plugin_that_declares_both_is_paused_and_one_that_declares_the_home_network_only_is_not()
    {
        var (m, _, _, _) = Setup(PluginCapabilities.NetworkInternet, "both");
        var lan = m.ContextFor("lan", new PluginManager.PluginGrant("lan", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Pages, PluginCapabilities.NetworkLan }), markLoaded: true);
        lan.RegisterPage(new PluginPage { Id = "lp", Title = "Lan", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });
        var both = m.ContextFor("both2", new PluginManager.PluginGrant("both2", PluginOrigin.FirstParty, new HashSet<string> { PluginCapabilities.Pages, PluginCapabilities.NetworkLan, PluginCapabilities.NetworkInternet }), markLoaded: true);
        both.RegisterPage(new PluginPage { Id = "bp", Title = "Both", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });

        _offline = true;
        Assert.False(m.IsPaused("lan"));
        Assert.True(m.IsPaused("both2"));
        Assert.Equal(["lan"], m.Pages.Select(p => p.Plugin));
        await Task.CompletedTask;
    }

    [Fact]
    public void A_paused_plugin_gets_do_nothing_stubs_and_no_null_references()
    {
        var m = new PluginManager([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true) { OfflineNow = () => _offline };
        var caps = new HashSet<string> { PluginCapabilities.Downloads, PluginCapabilities.NetworkState, PluginCapabilities.NetworkInternet, PluginCapabilities.BrowserSignIn };
        var ctx = m.ContextFor("net", new PluginManager.PluginGrant("net", PluginOrigin.FirstParty, caps), markLoaded: true);
        _offline = true;

        var downloads = ctx.Downloads;
        Assert.NotNull(downloads);
        downloads.Begin("d", "D");
        downloads.Report("d", 0.5, "x", 1, 2);
        downloads.Complete("d", true);
        var network = ctx.Network;
        Assert.NotNull(network);
        Assert.Empty(network.Adapters());
        Assert.Null(network.DefaultRoute());

        // A sign-in reference cached before the pause opens nothing afterwards.
        m.SignInOpener = (_, _, _, _, _) => true;
        _offline = false;
        var signIn = ctx.SignIn;
        Assert.NotNull(signIn);
        Assert.True(signIn.Open("https://example.com/in", "example.com", (_, _) => { }));
        _offline = true;
        Assert.False(signIn.Open("https://example.com/in", "example.com", (_, _) => { }));
    }

    [Fact]
    public async Task Subscribing_twice_and_unsubscribing_twice_leaves_no_handler_behind()
    {
        var (m, ctx, _, _) = Setup(PluginCapabilities.NetworkInternet, "net");
        var calls = 0;
        var signal = new SemaphoreSlim(0);
        Action<string> handler = _ => { Interlocked.Increment(ref calls); signal.Release(); };
        ctx.Events.LanguageChanged += handler;
        ctx.Events.LanguageChanged += handler;
        ctx.Events.LanguageChanged -= handler;

        m.Events.RaiseLanguageChanged("x");
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(300)));
        Assert.Equal(1, calls);

        ctx.Events.LanguageChanged -= handler;
        m.Events.RaiseLanguageChanged("y");
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(400)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Events_reach_a_plugin_only_while_it_is_not_paused()
    {
        var (m, ctx, _, _) = Setup(PluginCapabilities.NetworkInternet, "net");
        var got = new List<string>();
        var signal = new SemaphoreSlim(0);
        Action<string> handler = l => { lock (got) got.Add(l); signal.Release(); };
        ctx.Events.LanguageChanged += handler;

        m.Events.RaiseLanguageChanged("de");
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)));

        _offline = true;
        m.Events.RaiseLanguageChanged("paused");
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(400)));

        _offline = false;
        m.Events.RaiseLanguageChanged("en");
        Assert.True(await signal.WaitAsync(TimeSpan.FromSeconds(5)));

        ctx.Events.LanguageChanged -= handler;
        m.Events.RaiseLanguageChanged("gone");
        Assert.False(await signal.WaitAsync(TimeSpan.FromMilliseconds(400)));
        lock (got) Assert.Equal(["de", "en"], got);
    }
}
