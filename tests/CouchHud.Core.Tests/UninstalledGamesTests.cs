using System.Text;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>Not installed games: the store files (parsers with limits), the install links and the paged list.</summary>
public class UninstalledGamesTests
{
    // ------------------------------------------------------------------ install links

    [Theory]
    [InlineData("620", "steam://install/620")]
    [InlineData("4294967295", "steam://install/4294967295")]
    public void Steam_install_link_is_built_from_digits_only(string id, string url) => Assert.Equal(url, InstallLinks.Steam(id));

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("12a")]
    [InlineData("-5")]
    [InlineData("4294967296")]
    [InlineData("12345678901")]
    [InlineData("1 2")]
    [InlineData("1/../2")]
    [InlineData("１２３")] // full-width digits
    [InlineData("١٢٣")] // Arabic-Indic digits
    public void Steam_install_link_refuses_anything_else(string id) => Assert.Null(InstallLinks.Steam(id));

    [Fact]
    public void Epic_install_link_uses_the_launchers_scheme()
    {
        Assert.Equal("com.epicgames.launcher://apps/ns1%3Acat2%3AGameApp?action=install", InstallLinks.Epic("ns1", "cat2", "GameApp"));
        Assert.Equal("com.epicgames.launcher://apps/a.b%3Ac_d%3Ae-f?action=install", InstallLinks.Epic("a.b", "c_d", "e-f"));
    }

    [Theory]
    [InlineData(null, "c", "a")]
    [InlineData("n", "", "a")]
    [InlineData("n", "c", "a b")]
    [InlineData("n", "c", "a/b")]
    [InlineData("n", "c", "a?action=launch")]
    [InlineData("n", "c", "a&x=1")]
    [InlineData("n", "c", "a%3A")]
    [InlineData("n", "c", "..")]
    [InlineData("n", "c", ".hidden")]
    [InlineData("n", "c", "a\"b")]
    [InlineData("n", "c", "ä")]
    public void Epic_install_link_refuses_odd_ids(string? ns, string? cat, string? app) => Assert.Null(InstallLinks.Epic(ns, cat, app));

    [Fact]
    public void Epic_ids_are_limited_in_length()
    {
        Assert.NotNull(InstallLinks.Epic("n", "c", new string('a', 128)));
        Assert.Null(InstallLinks.Epic("n", "c", new string('a', 129)));
    }

    [Theory]
    [InlineData("steam://install/620", true)]
    [InlineData("com.epicgames.launcher://apps/n%3Ac%3Aa?action=install", true)]
    [InlineData("steam://install/0", false)]
    [InlineData("steam://install/620 ", false)]
    [InlineData("steam://install/620\n", false)]
    [InlineData("steam://install/620/../../run", false)]
    [InlineData("steam://rungameid/620", false)]
    [InlineData("steam://install/99999999999", false)]
    [InlineData("STEAM://install/620", false)]
    [InlineData("com.epicgames.launcher://apps/n%3Ac%3Aa?action=launch", false)]
    [InlineData("com.epicgames.launcher://apps/n%3Ac%3Aa?action=install&silent=true", false)]
    [InlineData("com.epicgames.launcher://apps/n%3Ac?action=install", false)]
    [InlineData("https://example.org/steam://install/620", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(null, false)]
    public void Only_the_two_install_forms_pass_the_last_check(string? url, bool ok) => Assert.Equal(ok, InstallLinks.IsInstallUrl(url));

    // ------------------------------------------------------------------ text VDF limits

    [Fact]
    public void Vdf_refuses_a_nesting_bomb_without_overflowing_the_stack()
    {
        var bomb = new StringBuilder();
        for (var i = 0; i < 100_000; i++) bomb.Append("\"k\" { ");
        Assert.Throws<FormatException>(() => Vdf.Parse(bomb.ToString()));
        var deep = new StringBuilder();
        for (var i = 0; i < Vdf.MaxDepth; i++) deep.Append("\"k\" { ");
        for (var i = 0; i < Vdf.MaxDepth; i++) deep.Append("} ");
        Assert.NotNull(Vdf.Parse(deep.ToString()).GetObject("k"));
    }

    // ------------------------------------------------------------------ localconfig.vdf

    private const string LocalConfig = """
        "UserLocalConfigStore"
        {
          "Software" { "Valve" { "Steam" {
            "apps"
            {
              "100" { "LastPlayed" "1700000000" "Playtime" "42" }
              "200" { "Playtime" "5" }
              "300" { "cloud" { "last_sync_state" "synchronized" } }
              "400" { "LastPlayed" "0" "Playtime" "0" }
              "abc" { "LastPlayed" "1700000000" }
              "0"   { "LastPlayed" "1700000000" }
              "500" "not an object"
            }
          } } }
        }
        """;

    [Fact]
    public void Localconfig_gives_only_apps_with_a_play_record()
    {
        Assert.Equal(new uint[] { 100, 200 }, SteamOwnedGames.PlayedAppIdsFromText(LocalConfig).Order());
    }

    [Fact]
    public void Localconfig_without_the_section_or_broken_gives_nothing()
    {
        Assert.Empty(SteamOwnedGames.PlayedAppIdsFromText("\"UserLocalConfigStore\" { }"));
        var dir = Directory.CreateTempSubdirectory("couchhud-lc-").FullName;
        var file = Path.Combine(dir, "localconfig.vdf");
        File.WriteAllText(file, "\"UserLocalConfigStore\" { \"Software\" { ");
        Assert.Empty(SteamOwnedGames.PlayedAppIds(file));
        Assert.Empty(SteamOwnedGames.PlayedAppIds(Path.Combine(dir, "missing.vdf")));
    }

    [Fact]
    public void Localconfig_beyond_the_size_limit_is_not_read()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-lc-").FullName;
        var file = Path.Combine(dir, "localconfig.vdf");
        using (var f = new FileStream(file, FileMode.Create)) f.SetLength(SteamOwnedGames.MaxLocalConfigBytes + 1);
        Assert.Empty(SteamOwnedGames.PlayedAppIds(file));
    }

    [Fact]
    public void Localconfig_of_the_active_account_else_the_newest()
    {
        var root = Directory.CreateTempSubdirectory("couchhud-steam-").FullName;
        string Make(string account, DateTime written)
        {
            var dir = Path.Combine(root, "userdata", account, "config");
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "localconfig.vdf");
            File.WriteAllText(f, LocalConfig);
            File.SetLastWriteTimeUtc(f, written);
            return f;
        }
        var older = Make("111", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = Make("222", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.CreateDirectory(Path.Combine(root, "userdata", "..x"));
        Directory.CreateDirectory(Path.Combine(root, "userdata", "anonymous"));

        Assert.Equal(newer, SteamOwnedGames.LocalConfigFile(root, new FakeRegistry()));
        var reg = new FakeRegistry();
        reg.WriteDword(@"HKCU\Software\Valve\Steam\ActiveProcess", "ActiveUser", 111);
        Assert.Equal(older, SteamOwnedGames.LocalConfigFile(root, reg));
        // An active account without a file of its own: the newest one.
        reg.WriteDword(@"HKCU\Software\Valve\Steam\ActiveProcess", "ActiveUser", 999);
        Assert.Equal(newer, SteamOwnedGames.LocalConfigFile(root, reg));
    }

    // ------------------------------------------------------------------ appinfo.vdf (binary)

    /// <summary>Builds an appinfo.vdf like Steam's (version 40: keys inline; 41: keys in a string table).</summary>
    internal sealed class AppInfoBuilder(int version)
    {
        private readonly List<(uint Id, byte[] Kv)> _apps = [];
        private readonly List<string> _strings = [];

        private int Index(string s)
        {
            var i = _strings.IndexOf(s);
            if (i >= 0) return i;
            _strings.Add(s);
            return _strings.Count - 1;
        }

        private void Key(BinaryWriter w, string key)
        {
            if (version >= 41) w.Write(Index(key));
            else
            {
                w.Write(Encoding.UTF8.GetBytes(key));
                w.Write((byte)0);
            }
        }

        public AppInfoBuilder App(uint id, string? name, string? type, Action<BinaryWriter, Action<BinaryWriter, string>>? extra = null)
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            void Str(string k, string v)
            {
                w.Write((byte)1);
                Key(w, k);
                w.Write(Encoding.UTF8.GetBytes(v));
                w.Write((byte)0);
            }
            w.Write((byte)0);
            Key(w, "appinfo");
            w.Write((byte)2);
            Key(w, "appid");
            w.Write((int)id);
            w.Write((byte)0);
            Key(w, "common");
            if (name is not null) Str("name", name);
            if (type is not null) Str("type", type);
            w.Write((byte)7);
            Key(w, "gameid");
            w.Write((ulong)id);
            w.Write((byte)8);
            extra?.Invoke(w, Key);
            w.Write((byte)8);
            w.Write((byte)8);
            _apps.Add((id, ms.ToArray()));
            return this;
        }

        public AppInfoBuilder Raw(uint id, byte[] kv)
        {
            _apps.Add((id, kv));
            return this;
        }

        public byte[] Build()
        {
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write(0x07564400u | (uint)version);
            w.Write(1u);
            long tableAtPos = 0;
            if (version >= 41)
            {
                tableAtPos = ms.Position;
                w.Write(0L);
            }
            foreach (var (id, kv) in _apps)
            {
                w.Write(id);
                var header = 4 + 4 + 8 + 20 + 4 + (version >= 40 ? 20 : 0);
                w.Write((uint)(header + kv.Length));
                w.Write(2u);
                w.Write(1700000000u);
                w.Write(0UL);
                w.Write(new byte[20]);
                w.Write(1u);
                if (version >= 40) w.Write(new byte[20]);
                w.Write(kv);
            }
            w.Write(0u);
            if (version >= 41)
            {
                var at = ms.Position;
                w.Write((uint)_strings.Count);
                foreach (var s in _strings)
                {
                    w.Write(Encoding.UTF8.GetBytes(s));
                    w.Write((byte)0);
                }
                ms.Position = tableAtPos;
                w.Write(at);
            }
            return ms.ToArray();
        }
    }

    private static Dictionary<uint, SteamAppName> ReadAppInfo(byte[] data, params uint[] wanted) =>
        SteamAppInfo.Read(new MemoryStream(data), wanted.ToHashSet(), TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(39)]
    [InlineData(40)]
    [InlineData(41)]
    public void Appinfo_gives_name_and_type_of_the_wanted_apps(int version)
    {
        var data = new AppInfoBuilder(version)
            .App(100, "Testspiel Eins", "Game")
            .App(150, "Not asked for", "Game")
            .App(200, "Testspiel Zwei", "game", (w, key) =>
            {
                // Other node types the reader has to step over: float, int64, wide string, nested object.
                w.Write((byte)3); key(w, "rating"); w.Write(4.5f);
                w.Write((byte)10); key(w, "big"); w.Write(-5L);
                w.Write((byte)5); key(w, "wide"); w.Write(Encoding.Unicode.GetBytes("ab\0"));
                w.Write((byte)0); key(w, "extended"); w.Write((byte)1); key(w, "developer"); w.Write(Encoding.UTF8.GetBytes("Studio\0")); w.Write((byte)8);
            })
            .App(300, "Ein Werkzeug", "Tool")
            .Build();
        var result = ReadAppInfo(data, 100, 200, 300, 999);
        Assert.Equal(new SteamAppName("Testspiel Eins", "Game"), result[100]);
        Assert.Equal(new SteamAppName("Testspiel Zwei", "game"), result[200]);
        Assert.Equal("Tool", result[300].Type);
        Assert.False(result.ContainsKey(150));
        Assert.False(result.ContainsKey(999));
    }

    [Fact]
    public void Appinfo_names_lose_control_characters_and_endless_length()
    {
        var data = new AppInfoBuilder(41).App(1, "Bad\u0007Name\u202E" + new string('x', 500), "game").Build();
        var name = ReadAppInfo(data, 1)[1].Name;
        Assert.DoesNotContain('\u0007', name);
        Assert.True(name.Length <= 200);
    }

    [Fact]
    public void Appinfo_entry_without_name_or_type_is_left_out()
    {
        var data = new AppInfoBuilder(41).App(1, null, "game").App(2, "Named", null).App(3, "Ok", "game").Build();
        Assert.Equal(new uint[] { 3 }, ReadAppInfo(data, 1, 2, 3).Keys);
    }

    [Theory]
    [InlineData(0x07564426u)] // version 38: unknown here
    [InlineData(0x0756442Au)] // version 42
    [InlineData(0x12345629u)]
    public void Appinfo_with_an_unknown_magic_gives_nothing(uint magic)
    {
        var data = new AppInfoBuilder(41).App(1, "A", "game").Build();
        BitConverter.GetBytes(magic).CopyTo(data, 0);
        Assert.Empty(ReadAppInfo(data, 1));
    }

    [Fact]
    public void Appinfo_garbage_truncation_and_bad_sizes_never_throw()
    {
        var good = new AppInfoBuilder(40).App(1, "A", "game").App(2, "B", "game").Build();
        // Cut at every length: no exception, and never an invented entry.
        for (var len = 0; len < good.Length; len++)
        {
            var result = ReadAppInfo(good[..len], 1, 2);
            Assert.All(result.Values, v => Assert.Contains(v.Name, new[] { "A", "B" }));
        }
        // An entry size beyond the file.
        var huge = (byte[])good.Clone();
        BitConverter.GetBytes(uint.MaxValue).CopyTo(huge, 12);
        Assert.Empty(ReadAppInfo(huge, 1, 2));
        // Random bytes after a valid header.
        var random = new byte[4096];
        new Random(7).NextBytes(random);
        BitConverter.GetBytes(0x07564429u).CopyTo(random, 0);
        Assert.Empty(ReadAppInfo(random, 1, 2));
    }

    [Fact]
    public void Appinfo_string_table_outside_the_file_or_too_large_gives_nothing()
    {
        var data = new AppInfoBuilder(41).App(1, "A", "game").Build();
        var outside = (byte[])data.Clone();
        BitConverter.GetBytes((long)data.Length + 100).CopyTo(outside, 8);
        Assert.Empty(ReadAppInfo(outside, 1));
        var negative = (byte[])data.Clone();
        BitConverter.GetBytes(-1L).CopyTo(negative, 8);
        Assert.Empty(ReadAppInfo(negative, 1));
        // A count far beyond the limit.
        var tableAt = BitConverter.ToInt64(data, 8);
        var many = (byte[])data.Clone();
        BitConverter.GetBytes(uint.MaxValue).CopyTo(many, (int)tableAt);
        Assert.Empty(ReadAppInfo(many, 1));
    }

    [Fact]
    public void Appinfo_nesting_bomb_and_bad_key_index_skip_only_that_entry()
    {
        var bomb = new MemoryStream();
        for (var i = 0; i < 10_000; i++)
        {
            bomb.WriteByte(0);
            bomb.Write(Encoding.UTF8.GetBytes("k\0"));
        }
        var badIndex = new byte[] { 1, 0xFF, 0xFF, 0xFF, 0x7F, (byte)'x', 0, 8 };
        var data40 = new AppInfoBuilder(40).Raw(1, bomb.ToArray()).App(2, "After", "game").Build();
        Assert.Equal(new uint[] { 2 }, ReadAppInfo(data40, 1, 2).Keys);
        var data41 = new AppInfoBuilder(41).Raw(1, badIndex).App(2, "After", "game").Build();
        Assert.Equal(new uint[] { 2 }, ReadAppInfo(data41, 1, 2).Keys);
    }

    [Fact]
    public void Appinfo_unknown_node_type_and_overlong_string_skip_the_entry()
    {
        var unknown = new byte[] { 0x0C, (byte)'k', 0, 8 };
        var longString = new MemoryStream();
        longString.WriteByte(1);
        longString.Write(Encoding.UTF8.GetBytes("k\0"));
        longString.Write(Encoding.UTF8.GetBytes(new string('x', SteamAppInfo.MaxStringBytes + 10)));
        longString.WriteByte(0);
        longString.WriteByte(8);
        var data = new AppInfoBuilder(40).Raw(1, unknown).Raw(2, longString.ToArray()).App(3, "Fine", "game").Build();
        Assert.Equal(new uint[] { 3 }, ReadAppInfo(data, 1, 2, 3).Keys);
    }

    [Fact]
    public void Appinfo_stops_when_its_time_is_up()
    {
        var builder = new AppInfoBuilder(41);
        for (uint i = 1; i <= 50; i++) builder.App(i, $"Spiel {i}", "game");
        var result = SteamAppInfo.Read(new MemoryStream(builder.Build()), Enumerable.Range(1, 50).Select(i => (uint)i).ToHashSet(), TimeSpan.Zero);
        Assert.True(result.Count < 50);
    }

    // ------------------------------------------------------------------ Steam end to end

    private static string SteamFolder(byte[] appinfo, string localConfig = LocalConfig)
    {
        var root = Directory.CreateTempSubdirectory("couchhud-steam-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "steamapps"));
        Directory.CreateDirectory(Path.Combine(root, "appcache"));
        File.WriteAllBytes(Path.Combine(root, "appcache", "appinfo.vdf"), appinfo);
        var config = Path.Combine(root, "userdata", "4242", "config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, "localconfig.vdf"), localConfig);
        return root;
    }

    [Fact]
    public void Steam_lists_played_games_with_known_names_only()
    {
        var root = SteamFolder(new AppInfoBuilder(41)
            .App(100, "Testspiel Eins", "Game")
            .App(200, "Zusatzinhalt", "DLC")
            .App(300, "Never played", "Game")
            .Build());
        var games = new SteamOwnedGames(new FakeRegistry(), root).Scan(default);
        var game = Assert.Single(games);
        Assert.Equal("steam:100", game.Id);
        Assert.Equal("steam", game.Source);
        Assert.Equal("Testspiel Eins", game.Name);
        Assert.Equal("steam://install/100", game.InstallUrl);
        Assert.EndsWith("/100/library_600x900.jpg", game.Art.Cover[^1]);
    }

    [Fact]
    public void Steam_without_appinfo_or_userdata_lists_nothing()
    {
        var root = SteamFolder([1, 2, 3]);
        Assert.Empty(new SteamOwnedGames(new FakeRegistry(), root).Scan(default));
        Directory.Delete(Path.Combine(root, "userdata"), recursive: true);
        Assert.Empty(new SteamOwnedGames(new FakeRegistry(), root).Scan(default));
    }

    // ------------------------------------------------------------------ Epic

    private const string EpicCatalog = """
        [
          { "id": "cat1", "namespace": "ns1", "title": "Testspiel Epic", "categories": [ { "path": "games" }, { "path": "applications" } ],
            "releaseInfo": [ { "appId": "MacOnly", "platform": [ "Mac" ] }, { "appId": "GameOne", "platform": [ "Windows", "Mac" ] } ],
            "keyImages": [ { "type": "DieselGameBoxTall", "url": "https://cdn.example.org/tall.jpg" }, { "type": "DieselGameBoxTall", "url": "http://insecure/x.jpg" } ] },
          { "id": "cat2", "namespace": "ns1", "title": "Ein DLC", "categories": [ { "path": "games" } ], "mainGameItem": { "id": "cat1" },
            "releaseInfo": [ { "appId": "Dlc", "platform": [ "Windows" ] } ] },
          { "id": "cat3", "namespace": "ns3", "title": "Add-on", "categories": [ { "path": "addons" }, { "path": "games" } ],
            "releaseInfo": [ { "appId": "Addon", "platform": [ "Windows" ] } ] },
          { "id": "cat4", "namespace": "ns4", "title": "Nur Mac", "categories": [ { "path": "games" } ],
            "releaseInfo": [ { "appId": "Mac", "platform": [ "Mac" ] } ] },
          { "id": "cat5", "namespace": "ns5", "title": "Bad id", "categories": [ { "path": "games" } ],
            "releaseInfo": [ { "appId": "x?action=launch", "platform": [ "Windows" ] } ] },
          { "id": "cat6", "namespace": "ns6", "title": "Engine", "categories": [ { "path": "engines" } ],
            "releaseInfo": [ { "appId": "Engine", "platform": [ "Windows" ] } ] },
          "not an object",
          { "namespace": "no id" }
        ]
        """;

    private static string EpicFile(string text, bool base64)
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-epic-").FullName;
        var file = Path.Combine(dir, "catcache.bin");
        File.WriteAllText(file, base64 ? Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) : text);
        return file;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Epic_lists_games_with_a_windows_release_not_dlc_or_addons(bool base64)
    {
        var games = new EpicOwnedGames(EpicFile(EpicCatalog, base64)).Scan(default);
        var game = Assert.Single(games);
        Assert.Equal("epic:GameOne", game.Id);
        Assert.Equal("Testspiel Epic", game.Name);
        Assert.Equal("com.epicgames.launcher://apps/ns1%3Acat1%3AGameOne?action=install", game.InstallUrl);
        Assert.Equal(["https://cdn.example.org/tall.jpg"], game.Art.Cover);
    }

    [Fact]
    public void Epic_broken_or_deep_catalog_gives_nothing()
    {
        Assert.Empty(new EpicOwnedGames(EpicFile("[{\"id\": ", base64: false)).Scan(default));
        Assert.Empty(new EpicOwnedGames(EpicFile("!!!not base64!!!", base64: false)).Scan(default));
        Assert.Empty(new EpicOwnedGames(EpicFile(new string('[', 10_000), base64: false)).Scan(default));
        Assert.Empty(new EpicOwnedGames(Path.Combine(Path.GetTempPath(), "couchhud-missing", "catcache.bin")).Scan(default));
    }

    [Fact]
    public void Epic_manifests_tell_installed_and_incomplete()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-epicm-").FullName;
        File.WriteAllText(Path.Combine(dir, "a.item"), """{ "AppName": "Done", "bIsIncompleteInstall": false }""");
        File.WriteAllText(Path.Combine(dir, "b.item"), """{ "AppName": "Busy", "bIsIncompleteInstall": true }""");
        File.WriteAllText(Path.Combine(dir, "c.item"), "{ broken");
        File.WriteAllText(Path.Combine(dir, "d.item"), "[1, 2]");
        var (installed, incomplete) = EpicScanner.InstallState(dir);
        Assert.Equal(["epic:Done"], installed);
        Assert.Equal(["epic:Busy"], incomplete);
        var none = EpicScanner.InstallState(Path.Combine(dir, "missing"));
        Assert.Empty(none.Installed);
    }

    // ------------------------------------------------------------------ the paged list

    private sealed class FakeSource(string name, Func<CancellationToken, IReadOnlyList<UninstalledGame>> scan) : IOwnedGamesSource
    {
        public string Name => name;

        public IReadOnlyList<UninstalledGame> Scan(CancellationToken ct) => scan(ct);
    }

    private static UninstalledGame G(string id, string name) =>
        new(id, id.Split(':')[0], name, id.StartsWith("steam", StringComparison.Ordinal) ? $"steam://install/{id[6..]}" : "x", Artwork.Empty);

    [Fact]
    public async Task List_leaves_out_installed_games_sorts_and_pages()
    {
        var steam = new FakeSource("Steam", _ => [G("steam:2", "Beta"), G("steam:1", "alpha"), G("steam:3", "Gamma")]);
        var epic = new FakeSource("Epic", _ => [G("epic:X", "Delta"), G("steam:1", "Duplicate")]);
        var lib = new UninstalledLibrary([steam, epic]);
        await lib.RefreshAsync(["steam:3"]);

        Assert.Equal(["alpha", "Beta", "Delta"], lib.All.Select(g => g.Name));
        var page = lib.Page(1, 1, null, null);
        Assert.Equal(3, page.Total);
        Assert.Equal("Beta", Assert.Single(page.Items).Name);
        Assert.Equal(2, page.Counts["steam"]);
        Assert.Equal(1, page.Counts["epic"]);
        Assert.Equal(["Delta"], lib.Page(0, 50, "epic", null).Items.Select(g => g.Name));
        Assert.Equal(["Beta"], lib.Page(0, 50, null, "ET").Items.Select(g => g.Name));
        // Page size is capped, offsets are clamped.
        Assert.Equal(3, lib.Page(-5, 10_000, null, null).Items.Count);
        Assert.Equal("Delta", Assert.Single(lib.Page(99, 10, null, null).Items).Name);

        lib.Forget(["steam:2"]);
        Assert.Null(lib.Find("steam:2"));
        Assert.Equal(2, lib.All.Count);
    }

    [Fact]
    public async Task Page_size_never_exceeds_the_cap()
    {
        var many = Enumerable.Range(1, 5000).Select(i => G($"steam:{i}", $"Spiel {i:D5}")).ToList();
        var lib = new UninstalledLibrary([new FakeSource("Steam", _ => many)]);
        await lib.RefreshAsync([]);
        var page = lib.Page(4990, 500, null, null);
        Assert.Equal(5000, page.Total);
        Assert.Equal(10, page.Items.Count);
        Assert.Equal(UninstalledLibrary.MaxPageSize, lib.Page(0, 500, null, null).Items.Count);
    }

    [Fact]
    public async Task A_slow_or_broken_source_is_skipped_the_others_show()
    {
        var slow = new FakeSource("Slow", ct =>
        {
            Task.Delay(TimeSpan.FromSeconds(30), ct).Wait(ct);
            return [G("steam:9", "Slow")];
        });
        var broken = new FakeSource("Broken", _ => throw new InvalidDataException("boom"));
        var fine = new FakeSource("Fine", _ => [G("epic:A", "Fine")]);
        var lib = new UninstalledLibrary([slow, broken, fine]) { SourceTimeout = TimeSpan.FromMilliseconds(200) };
        await lib.RefreshAsync([]);
        Assert.Equal(["Fine"], lib.All.Select(g => g.Name));
    }

    [Fact]
    public async Task A_new_scan_comes_at_most_every_interval()
    {
        var scans = 0;
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var lib = new UninstalledLibrary([new FakeSource("Steam", _ =>
        {
            Interlocked.Increment(ref scans);
            return [G("steam:1", "A")];
        })], () => now);
        Assert.Null(lib.ScannedAt);
        await lib.RefreshAsync([]);
        Assert.NotNull(lib.ScannedAt);
        await lib.RefreshAsync([]);
        now += TimeSpan.FromSeconds(10);
        await lib.RefreshAsync([]);
        Assert.Equal(1, scans);
        now += lib.MinRefreshInterval;
        await lib.RefreshAsync([]);
        Assert.Equal(2, scans);
    }

    [Fact]
    public async Task No_second_scan_while_a_timed_out_worker_still_reads()
    {
        var scans = 0;
        using var release = new ManualResetEventSlim();
        var stubborn = new FakeSource("Stubborn", _ =>
        {
            // Ignores its token, as a reader stuck in one big read would.
            Interlocked.Increment(ref scans);
            release.Wait(TimeSpan.FromSeconds(10));
            return [];
        });
        var lib = new UninstalledLibrary([stubborn]) { SourceTimeout = TimeSpan.FromMilliseconds(100), MinRefreshInterval = TimeSpan.Zero };
        await lib.RefreshAsync([]);
        Assert.False(lib.Scanning);
        await lib.RefreshAsync([]);
        Assert.Equal(1, scans);
        release.Set();
        // Once the worker has ended, the next scan may start.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (scans < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            await lib.RefreshAsync([]);
        }
        Assert.Equal(2, scans);
    }

    [Fact]
    public async Task The_list_is_capped()
    {
        var many = Enumerable.Range(1, UninstalledLibrary.MaxGames + 50).Select(i => G($"steam:{i}", $"Spiel {i:D6}")).ToList();
        var lib = new UninstalledLibrary([new FakeSource("Steam", _ => many)]);
        await lib.RefreshAsync([]);
        Assert.Equal(UninstalledLibrary.MaxGames, lib.All.Count);
    }

    [Fact]
    public void Appinfo_string_table_honours_time_and_cancel()
    {
        var builder = new AppInfoBuilder(41);
        for (uint i = 1; i <= 5; i++) builder.App(i, $"Spiel {i}", "game");
        var data = builder.Build();
        Assert.Empty(SteamAppInfo.Read(new MemoryStream(data), new HashSet<uint> { 1 }, TimeSpan.Zero));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SteamAppInfo.Read(new MemoryStream(data), new HashSet<uint> { 1 }, TimeSpan.FromSeconds(10), cancelled.Token));
        Assert.Single(SteamAppInfo.Read(new MemoryStream(data), new HashSet<uint> { 1 }, TimeSpan.FromSeconds(10)));
    }

    // ------------------------------------------------------------------ Steam manifests while polling

    private static string Acf(string appId, string name, int flags = 2) =>
        $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"name\"\t\t\"{name}\"\n\t\"StateFlags\"\t\t\"{flags}\"\n\t\"BytesToDownload\"\t\t\"100\"\n\t\"BytesDownloaded\"\t\t\"10\"\n}}\n";

    [Fact]
    public void Polled_manifests_are_bounded_and_names_cleaned()
    {
        var root = Directory.CreateTempSubdirectory("couchhud-steam-poll-").FullName;
        try
        {
            var apps = Path.Combine(root, "steamapps");
            Directory.CreateDirectory(apps);
            File.WriteAllText(Path.Combine(apps, "appmanifest_10.acf"), Acf("10", "Spiel\u0001{\"K\":\"x\"}"));
            File.WriteAllText(Path.Combine(apps, "appmanifest_11.acf"), Acf("11; calc", "Bad id"));
            File.WriteAllText(Path.Combine(apps, "appmanifest_12.acf"), Acf("12", "Installed", 4));
            // Far beyond a real manifest: not read at all.
            File.WriteAllText(Path.Combine(apps, "appmanifest_13.acf"), Acf("13", "Huge") + new string(' ', (int)SteamScanner.MaxManifestBytes));
            var (downloads, installed) = new SteamScanner(new FakeRegistry(), root).DownloadsAndInstalled();
            var d = Assert.Single(downloads);
            Assert.Equal("10", d.AppId);
            Assert.DoesNotContain('\u0001', d.Name);
            Assert.Equal(new[] { "12" }, installed.ToArray());
            Assert.DoesNotContain('\u0001', DownloadCenter.FromSteam(d with { Name = "A\u0001B" }, 0).Name);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task A_worker_stuck_for_long_is_logged_once()
    {
        using var release = new ManualResetEventSlim();
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var stuck = new FakeSource("Stuck", _ =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return [];
        });
        var lib = new UninstalledLibrary([stuck])
        {
            SourceTimeout = TimeSpan.FromMilliseconds(50),
            StuckAfter = TimeSpan.FromMilliseconds(200),
            Log = lines.Enqueue
        };
        await lib.RefreshAsync([]);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (lines.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
        release.Set();
        await Task.Delay(100);
        var line = Assert.Single(lines);
        Assert.Contains("Stuck", line);
        Assert.Contains("still running", line);
    }

    // ------------------------------------------------------------------ reads bounded by what is really read

    [Fact]
    public void Bounded_read_gives_the_text_up_to_the_limit_and_nothing_beyond()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-bounded-").FullName;
        try
        {
            var exact = Path.Combine(dir, "exact.txt");
            File.WriteAllBytes(exact, [0xEF, 0xBB, 0xBF, (byte)'a', (byte)'b', (byte)'c', (byte)'d', (byte)'e']);
            Assert.Equal("abcde", CouchHud.Core.Platform.BoundedFile.ReadAllText(exact, 8));
            Assert.Null(CouchHud.Core.Platform.BoundedFile.ReadAllText(exact, 7));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>A stream that claims a small length but keeps giving bytes, as a file growing between check and read.</summary>
    private sealed class GrowingStream : Stream
    {
        public long Served { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 10;
        public override long Position { get => Served; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            Array.Fill(buffer, (byte)'x', offset, count);
            Served += count;
            return count;
        }
    }

    [Fact]
    public void Bounded_read_stops_one_byte_past_the_limit_of_a_growing_file()
    {
        var stream = new GrowingStream();
        Assert.Null(CouchHud.Core.Platform.BoundedFile.ReadAtMost(stream, 1000));
        Assert.Equal(1001, stream.Served);
    }

    [Fact]
    public void An_oversized_epic_manifest_is_not_read()
    {
        var dir = Directory.CreateTempSubdirectory("couchhud-epic-big-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.item"), "{\"AppName\":\"Small\"}");
            File.WriteAllText(Path.Combine(dir, "b.item"), "{\"AppName\":\"Big\",\"pad\":\"" + new string('x', (int)EpicScanner.MaxManifestBytes) + "\"}");
            Assert.Equal(new[] { "epic:Small" }, EpicScanner.InstallState(dir).Installed.ToArray());
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Theory]
    [InlineData(@"\\server\share\SteamLibrary", true)]
    [InlineData("//server/share/SteamLibrary", true)]
    public void Network_paths_are_left_out_of_the_poll(string path, bool network) => Assert.Equal(network, SteamScanner.OnNetwork(path));

    [Fact]
    public void A_local_folder_is_polled()
    {
        Assert.False(SteamScanner.OnNetwork(Path.GetTempPath()));
        Assert.False(SteamScanner.OnNetwork("relative"));
    }

    // ------------------------------------------------------------------ lock

    [Fact]
    public void A_locked_couch_may_look_but_not_install()
    {
        Assert.Contains("getUninstalled", LockPolicy.OpenWhileLocked);
        Assert.Contains("getDownloadCenter", LockPolicy.OpenWhileLocked);
        Assert.DoesNotContain("installGame", LockPolicy.OpenWhileLocked);
        Assert.DoesNotContain("cancelDownload", LockPolicy.OpenWhileLocked);
        Assert.DoesNotContain("openClientDownloads", LockPolicy.OpenWhileLocked);
        Assert.DoesNotContain("startUpdate", LockPolicy.OpenWhileLocked);
    }

    [Fact]
    public void Only_steam_has_a_fixed_link_to_its_download_page()
    {
        Assert.Equal("steam://open/downloads", InstallLinks.ClientDownloads("steam"));
        foreach (var other in new[] { "epic", "Steam", "steam ", "gog", "", null, "steam://open/downloads" })
            Assert.Null(InstallLinks.ClientDownloads(other));
    }
}
