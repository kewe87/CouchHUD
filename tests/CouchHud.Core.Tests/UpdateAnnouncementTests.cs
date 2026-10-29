using CouchHud.Core.Notifications;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;

namespace CouchHud.Core.Tests;

public class UpdateAnnouncementTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("announce").FullName;
    private string LedgerFile => Path.Combine(_dir, "announced.json");

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Self_new_version_notifies_same_does_not_newer_again_does()
    {
        Assert.True(UpdateAnnouncements.SelfIsNew(null, "0.7.0"));
        Assert.False(UpdateAnnouncements.SelfIsNew("0.7.0", "0.7.0"));
        Assert.False(UpdateAnnouncements.SelfIsNew("0.7.0", "0.6.9"));
        Assert.True(UpdateAnnouncements.SelfIsNew("0.7.0", "0.7.1"));
        Assert.False(UpdateAnnouncements.SelfIsNew("0.7.0", "garbage"));
    }

    [Fact]
    public void Catalog_mixed_new_and_known_entries_only_new_ones_notify()
    {
        var ledger = new UpdateAnnouncements(LedgerFile);
        Assert.Single(ledger.TakeFresh([UpdateAnnouncements.Key("plugin", "a", "1.1.0")]));
        var second = ledger.TakeFresh([UpdateAnnouncements.Key("plugin", "a", "1.1.0"), UpdateAnnouncements.Key("skin", "b", "2.0.0")]);
        Assert.Equal([UpdateAnnouncements.Key("skin", "b", "2.0.0")], second);
        Assert.Empty(ledger.TakeFresh([UpdateAnnouncements.Key("skin", "b", "2.0.0")]));
        Assert.Single(ledger.TakeFresh([UpdateAnnouncements.Key("skin", "b", "2.1.0")]));
    }

    [Fact]
    public void Nothing_is_announced_again_after_a_restart()
    {
        var key = UpdateAnnouncements.Key("plugin", "a", "1.1.0");
        Assert.Single(new UpdateAnnouncements(LedgerFile).TakeFresh([key]));
        var reloaded = new UpdateAnnouncements(LedgerFile);
        Assert.Empty(reloaded.TakeFresh([key]));
        Assert.Single(reloaded.TakeFresh([UpdateAnnouncements.Key("plugin", "a", "1.2.0")]));
    }

    [Fact]
    public void Ledger_stays_bounded_and_survives_damage()
    {
        var ledger = new UpdateAnnouncements(LedgerFile);
        ledger.TakeFresh(Enumerable.Range(0, 500).Select(i => UpdateAnnouncements.Key("plugin", "p" + i, "1.0.0")));
        Assert.True(File.ReadAllText(LedgerFile).Split("plugin:").Length - 1 <= UpdateAnnouncements.MaxKeys);
        File.WriteAllText(LedgerFile, "{ not json");
        Assert.Single(new UpdateAnnouncements(LedgerFile).TakeFresh(["plugin:x@1.0.0"]));
        File.WriteAllText(LedgerFile, "[null, \"\", \"" + new string('x', 5000) + "\"]");
        Assert.Single(new UpdateAnnouncements(LedgerFile).TakeFresh(["plugin:x@1.0.0"]));
    }

    [Fact]
    public void Keys_with_json_characters_round_trip()
    {
        var key = UpdateAnnouncements.Key("plugin", "a\"b\\c]\n{x}", "1.0.0-ä\"");
        Assert.Single(new UpdateAnnouncements(LedgerFile).TakeFresh([key]));
        Assert.Empty(new UpdateAnnouncements(LedgerFile).TakeFresh([key]));
    }

    [Fact]
    public void A_write_failure_keeps_the_key_in_memory()
    {
        // The ledger's folder is a file, so nothing can be saved.
        var blocker = Path.Combine(_dir, "blocker");
        File.WriteAllText(blocker, "x");
        var ledger = new UpdateAnnouncements(Path.Combine(blocker, "announced.json"));
        var key = UpdateAnnouncements.Key("plugin", "a", "1.0.0");
        Assert.Single(ledger.TakeFresh([key]));
        Assert.Empty(ledger.TakeFresh([key]));
    }

    [Fact]
    public void Names_are_cleaned_capped_and_at_most_four()
    {
        var names = UpdateAnnouncements.Names(["Plug\u202Eout \uD83D\uDE00", new string('n', 300), "c", "d", "e", ""]);
        var parts = names.Split(", ");
        Assert.Equal(4, parts.Length);
        Assert.False(names.Contains('\u202E'));
        Assert.False(names.Contains("\uD83D\uDE00", StringComparison.Ordinal));
        Assert.All(parts, p => Assert.True(p.Length <= 41));
    }

    [Fact]
    public void Only_whitelisted_targets_survive()
    {
        Assert.Equal("downloads", NotificationTargets.Normalize("downloads"));
        Assert.Null(NotificationTargets.Normalize("Downloads"));
        Assert.Null(NotificationTargets.Normalize("https://evil.example"));
        Assert.Null(NotificationTargets.Normalize("C:/Windows/calc.exe"));
        Assert.Null(NotificationTargets.Normalize("settings:updates"));
        Assert.Null(NotificationTargets.Normalize(null));
    }

    [Fact]
    public void Plugins_cannot_set_a_target()
    {
        // The plugin path hands over app, title and body only; there is no way to pass a target through it.
        var type = typeof(PluginManager).GetProperty(nameof(PluginManager.Notifier))!.PropertyType;
        Assert.Equal(typeof(Action<string, string, string>), type);
        Assert.Null(new Notification("1", "couchhud", "plugin", "t", "b", 0).Target);
    }
}
