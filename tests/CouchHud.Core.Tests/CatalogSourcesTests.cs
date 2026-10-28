using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CouchHud.Core.Config;
using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>
/// Catalogs besides CouchHUD's own: the official one is built in and cannot be removed, every other one is added only from the HUD itself,
/// unlocked, outside a child's profile, in expert mode, with the parents' code and the "I trust this developer" confirmation, over https,
/// bounded, and everything from it is third-party and cannot take the name of one of CouchHUD's own plugins. Throwaway keys only.
/// </summary>
[Collection("Language")]
public sealed class CatalogSourcesTests : IDisposable
{
    private const string Extra = "https://plugins.example.org/index.json";
    /// <summary>The same address with the host in upper case; the path after it stays exact (see CatalogUrl).</summary>
    private const string ExtraHostUpper = "HTTPS://PLUGINS.EXAMPLE.ORG/index.json";
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-sources-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _official = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _publisher = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public CatalogSourcesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _official.Dispose();
        _publisher.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static PluginPolicy.GateContext Gate(PluginCaller caller = PluginCaller.Hud, bool locked = false, bool child = false, bool expert = true, bool parental = true) =>
        new(caller, locked, child, expert, false, parental);

    private static IReadOnlyList<CatalogSourceEntry> None => [];

    private static PluginPolicy.GateDecision Add(PluginPolicy.GateContext ctx, string? url = Extra, bool ack = true, string? key = null, IReadOnlyList<CatalogSourceEntry>? current = null) =>
        CatalogSourceRules.Decide(ctx, CatalogSourceChange.Add, url, key, null, ack, current ?? None);

    private static string Pem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();

    // ---- the official source stays

    [Theory]
    [InlineData(CatalogDefaults.IndexUrl)]
    [InlineData("HTTPS://RAW.GITHUBUSERCONTENT.COM/KEWE87/COUCHHUD-CATALOG/main/index.json")]
    public void The_official_catalog_cannot_be_removed_whatever_else_is_true(string url)
    {
        var decision = CatalogSourceRules.Decide(Gate(), CatalogSourceChange.Remove, url, null, null, true, [new CatalogSourceEntry { Url = Extra }]);
        Assert.Equal("catalog.source.officialStays", decision.Refused);
        // Not even a stray entry for it in the list changes that, nor adding it again.
        Assert.Equal("catalog.source.isOfficial", Add(Gate(), url).Refused);
    }

    [Fact]
    public void The_official_catalog_is_always_the_first_source_whatever_config_json_lists()
    {
        Assert.Equal([CatalogDefaults.IndexUrl], CatalogSourceRules.OfficialAnd([], false));
        Assert.Equal([CatalogDefaults.IndexUrl], CatalogSourceRules.OfficialAnd(["https://x.example/i.json"], false));
        // The developer switch brings the old list back, still behind the official one, which cannot be listed twice.
        Assert.Equal([CatalogDefaults.IndexUrl, "https://x.example/i.json"], CatalogSourceRules.OfficialAnd([CatalogDefaults.IndexUrl, "https://x.example/i.json"], true));
        Assert.Equal(CatalogDefaults.IndexUrl, CatalogSourceRules.OfficialAnd(["https://x.example/i.json"], true)[0]);
    }

    // ---- adding: every wall

    [Fact]
    public void Adding_goes_ahead_only_when_every_condition_holds_and_then_asks_for_the_code()
    {
        var ok = Add(Gate());
        Assert.Null(ok.Refused);
        Assert.True(ok.ParentCode);
        // Without parental controls the host asks for the couch code instead (the ticket); the decision still wants a code.
        Assert.True(Add(Gate(parental: false)).ParentCode);
    }

    [Fact]
    public void Adding_while_locked_is_refused()
    {
        Assert.Equal("lock.unlockFirst", Add(Gate(locked: true)).Refused);
        Assert.Equal("lock.unlockFirst", CatalogSourceRules.Decide(Gate(locked: true), CatalogSourceChange.Remove, Extra, null, null, true, [new CatalogSourceEntry { Url = Extra }]).Refused);
    }

    [Fact]
    public void Adding_in_a_child_profile_is_refused()
    {
        Assert.Equal("plugin.gate.child", Add(Gate(child: true)).Refused);
        Assert.Equal("plugin.gate.child", CatalogSourceRules.Decide(Gate(child: true), CatalogSourceChange.Remove, Extra, null, null, true, [new CatalogSourceEntry { Url = Extra }]).Refused);
    }

    [Fact]
    public void Adding_without_the_trust_confirmation_is_refused()
    {
        Assert.Equal("catalog.source.ackNeeded", Add(Gate(), ack: false).Refused);
    }

    [Theory]
    [InlineData("http://plugins.example.org/index.json")]
    [InlineData("file:///C:/catalog/index.json")]
    [InlineData("ftp://plugins.example.org/index.json")]
    [InlineData("https://user:pass@plugins.example.org/index.json")]
    [InlineData("plugins.example.org/index.json")]
    [InlineData("")]
    [InlineData(null)]
    public void Adding_an_address_that_is_not_plain_https_is_refused(string? url)
    {
        Assert.Equal("catalog.source.httpsOnly", Add(Gate(), url).Refused);
    }

    [Fact]
    public void Adding_without_expert_mode_is_refused()
    {
        Assert.Equal("catalog.source.expert", Add(Gate(expert: false)).Refused);
    }

    [Theory]
    [InlineData(PluginCaller.Remote)]
    [InlineData(PluginCaller.Mcp)]
    [InlineData(PluginCaller.Assistant)]
    [InlineData(PluginCaller.Plugin)]
    public void Only_the_HUD_itself_changes_the_catalogs(PluginCaller caller)
    {
        Assert.Equal("plugin.gate.hudOnly", Add(Gate(caller)).Refused);
        Assert.Equal("plugin.gate.hudOnly", CatalogSourceRules.Decide(Gate(caller), CatalogSourceChange.Remove, Extra, null, null, true, [new CatalogSourceEntry { Url = Extra }]).Refused);
    }

    [Fact]
    public void Adding_is_bounded_and_refuses_doubles_and_bad_keys_and_removing_an_unknown_one()
    {
        var full = Enumerable.Range(0, CatalogSourceRules.MaxSources).Select(i => new CatalogSourceEntry { Url = $"https://s{i}.example.org/i.json" }).ToList();
        Assert.Equal("catalog.source.tooMany", Add(Gate(), current: full).Refused);
        Assert.Equal("catalog.source.twice", Add(Gate(), ExtraHostUpper, current: [new CatalogSourceEntry { Url = Extra }]).Refused);
        Assert.Equal("catalog.source.badKey", Add(Gate(), key: "not a key").Refused);
        Assert.Equal("catalog.source.badKey", Add(Gate(), key: CatalogSignature.PublicKeyPem).Refused);
        Assert.Equal("catalog.source.badKey", Add(Gate(), key: new string('a', CatalogSourceRules.MaxKeyLength + 1)).Refused);
        Assert.Equal("catalog.source.badName", CatalogSourceRules.Decide(Gate(), CatalogSourceChange.Add, Extra, null, new string('n', 61), true, None).Refused);
        Assert.Null(Add(Gate(), key: Pem(_publisher)).Refused);
        Assert.Equal("catalog.source.unknown", CatalogSourceRules.Decide(Gate(), CatalogSourceChange.Remove, "https://nobody.example.org/i.json", null, null, true, None).Refused);
    }

    [Fact]
    public void A_list_edited_by_hand_cannot_smuggle_past_the_rules()
    {
        var edited = new List<CatalogSourceEntry>
        {
            new() { Url = "http://plain.example.org/i.json" },
            new() { Url = CatalogDefaults.IndexUrl },
            new() { Url = Extra },
            new() { Url = ExtraHostUpper },
            new() { Url = "https://k.example.org/i.json", PublisherKey = "garbage" },
            new() { Url = "https://ok.example.org/i.json", PublisherKey = Pem(_publisher) }
        };
        edited.AddRange(Enumerable.Range(0, 20).Select(i => new CatalogSourceEntry { Url = $"https://more{i}.example.org/i.json" }));
        var effective = CatalogSourceRules.Effective(edited);
        Assert.Equal(CatalogSourceRules.MaxSources, effective.Count);
        Assert.Equal([Extra, "https://ok.example.org/i.json"], effective.Take(2).Select(s => s.Url));
        Assert.DoesNotContain(effective, s => s.Url.StartsWith("http:", StringComparison.Ordinal) || CatalogSourceRules.IsOfficial(s.Url) || s.PublisherKey == "garbage");
        Assert.Empty(CatalogSourceRules.Effective(null));
    }

    [Fact]
    public void The_list_is_stored_in_the_config_and_read_back_from_it()
    {
        var list = CatalogSourceRules.Apply(None, CatalogSourceChange.Add, Extra, Pem(_publisher), " Friend ", DateTimeOffset.UnixEpoch);
        var config = new AppConfig { ExtraCatalogSources = list };
        var json = System.Text.Json.JsonSerializer.Serialize(config, ConfigStore.JsonOptions);
        var back = ConfigStore.Parse(json)!;
        var source = Assert.Single(back.ExtraCatalogSources);
        Assert.Equal(Extra, source.Url);
        Assert.Equal("Friend", source.Name);
        Assert.NotNull(CatalogSourceRules.KeyCode(source));
        Assert.Empty(new AppConfig().ExtraCatalogSources);
        Assert.Empty(CatalogSourceRules.Apply(list, CatalogSourceChange.Remove, ExtraHostUpper, null, null, DateTimeOffset.UtcNow));
    }

    // ---- what comes from an extra source

    private static string Entry(string id, string folder, string statement = "") =>
        $$"""{ "id": "{{id}}", "folder": "{{folder}}", "name": "{{id}}", "type": "plugin", "version": "1.0.0", "download": "https://example.com/{{id}}.zip", "sha256": "{{Hash}}"{{statement}} }""";

    private static string Index(params string[] entries) => "{ \"entries\": [" + string.Join(",", entries) + "] }";

    private static string Sign(ECDsa key, string text) =>
        Convert.ToBase64String(key.SignData(CatalogSignature.WithContext(CatalogSignature.CatalogContext, Encoding.UTF8.GetBytes(text)), HashAlgorithmName.SHA256));

    private sealed class FakeHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));
    }

    private static HttpResponseMessage Ok(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };

    /// <summary>The official index (signed with the throwaway official key) and one extra index, served by a fake network.</summary>
    private CatalogService Service(string official, string extra, string? extraSignature = null, string? pinnedKey = null, IReadOnlyList<CatalogSourceEntry>? sources = null)
    {
        HttpResponseMessage Respond(Uri u) => u.ToString() switch
        {
            CatalogDefaults.IndexUrl => Ok(official),
            CatalogDefaults.IndexUrl + ".sig" => Ok(Sign(_official, official)),
            Extra => Ok(extra),
            Extra + ".sig" => extraSignature is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : Ok(extraSignature),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        };
        var list = sources ?? [new CatalogSourceEntry { Url = Extra, PublisherKey = pinnedKey }];
        return new CatalogService(new HttpClient(new FakeHandler(Respond)), _dir, () => [CatalogDefaults.IndexUrl], () => false, "de", Pem(_official), null, () => list);
    }

    [Fact]
    public async Task Entries_of_an_extra_catalog_are_extra_unsigned_without_a_pinned_key_and_signed_with_it()
    {
        var official = Index(Entry("tool", "tool"));
        var extra = Index(Entry("other", "other"));

        var without = await Service(official, extra).LoadAsync();
        Assert.False(without.Entries.Single(e => e.Id == "tool").Extra);
        Assert.False(without.Entries.Single(e => e.Id == "tool").Unsigned);
        var other = without.Entries.Single(e => e.Id == "other");
        Assert.True(other.Extra);
        Assert.True(other.Unsigned);

        var pinned = await Service(official, extra, Sign(_publisher, extra), Pem(_publisher)).LoadAsync();
        Assert.False(pinned.Entries.Single(e => e.Id == "other").Unsigned);
        // Signed with another key than the pinned one: an error, the catalog is not used (see CatalogSourcesHardeningTests).
        File.Delete(Path.Combine(_dir, "catalog-cache.json"));
        var wrong = await Service(official, extra, Sign(_official, extra), Pem(_publisher)).LoadAsync();
        Assert.DoesNotContain(wrong.Entries, e => e.Id == "other");
        // Never trusted because CouchHUD's key signed it: a source is verified against the key pinned for it only.
        var borrowed = await Service(official, extra, Sign(_official, extra)).LoadAsync();
        Assert.True(borrowed.Entries.Single(e => e.Id == "other").Unsigned);
    }

    [Fact]
    public async Task A_spelling_of_the_official_address_in_the_lists_is_the_official_source_not_a_second_unsigned_one()
    {
        const string Variant = "https://raw.githubusercontent.com/KEWE87/couchhud-catalog/main/index.json";
        var official = Index(Entry("tool", "tool"));
        var requests = new List<string>();
        HttpResponseMessage Respond(Uri u)
        {
            requests.Add(u.ToString());
            var plain = u.ToString().EndsWith(".sig") ? u.ToString()[..^4] : u.ToString();
            if (!CatalogUrl.Same(plain, CatalogDefaults.IndexUrl)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return Ok(u.ToString().EndsWith(".sig") ? Sign(_official, official) : official);
        }
        var service = new CatalogService(new HttpClient(new FakeHandler(Respond)), _dir, () => [CatalogDefaults.IndexUrl, Variant], () => false, "de", Pem(_official), null,
            () => [new CatalogSourceEntry { Url = Variant }]);
        var result = await service.LoadAsync();
        // One fetch of the index, signed and checked as the official one; the spelling neither doubled it nor became an unsigned developer source.
        Assert.Single(requests, r => !r.EndsWith(".sig"));
        Assert.All(result.Entries, e => Assert.False(e.Extra || e.Unsigned));
        Assert.Single(result.Entries);
    }

    [Fact]
    public async Task An_id_of_the_official_catalog_cannot_be_taken_over_by_an_extra_one()
    {
        var official = Index(Entry("tool", "tool"));
        var extra = Index(Entry("tool", "elsewhere"), Entry("fresh", "tool"), Entry("same", "same"));
        var result = await Service(official, extra).LoadAsync();

        // The same id with another folder: listed with a problem and no download, the official entry stays the installable one.
        var tools = result.Entries.Where(e => e.Id == "tool").ToList();
        Assert.Equal(2, tools.Count);
        Assert.Single(tools, e => !e.Extra && e.Problem is null && e.DownloadUrl is not null);
        Assert.Single(tools, e => e.Extra && e.Problem is not null && e.DownloadUrl is null);
        // A new id on the folder of an official plugin is not listed at all (the first source wins).
        Assert.DoesNotContain(result.Entries, e => e.Id == "fresh");
        Assert.Contains(result.Entries, e => e.Id == "same" && e.Extra);
    }

    [Theory]
    [InlineData("casehud")]
    [InlineData("CaseHUD-pro")]
    [InlineData("c4seHud")]
    [InlineData("wand")]
    [InlineData("couchhud-extras")]
    public async Task An_extra_catalog_cannot_use_the_name_of_one_of_CouchHUDs_own_plugins_even_when_the_official_catalog_is_offline(string id)
    {
        var service = Service("{}", Index(Entry(id, id)));
        var result = await service.LoadAsync();
        var entry = result.Entries.Single(e => e.Id == id);
        Assert.NotNull(entry.Problem);
        Assert.Null(entry.DownloadUrl);
    }

    [Fact]
    public async Task An_extra_index_is_bounded_in_size_and_in_entries()
    {
        var fat = Index(Entry("big", "big", ",\"longDescription\":\"" + new string('x', 600_000) + "\""));
        var tooBig = await Service(Index(Entry("tool", "tool")), fat).LoadAsync();
        Assert.DoesNotContain(tooBig.Entries, e => e.Id == "big");
        Assert.NotNull(tooBig.Error);
        Assert.Contains(tooBig.Entries, e => e.Id == "tool");

        var many = Index(Enumerable.Range(0, CatalogSourceRules.MaxEntries + 50).Select(i => Entry($"p{i}", $"p{i}")).ToArray());
        var result = await Service(Index(Entry("tool", "tool")), many).LoadAsync();
        Assert.Equal(CatalogSourceRules.MaxEntries, result.Entries.Count(e => e.Extra));
    }

    [Fact]
    public async Task An_extra_source_that_is_not_https_or_is_the_official_one_is_never_fetched()
    {
        var fetched = new List<string>();
        var handler = new FakeHandler(u =>
        {
            fetched.Add(u.ToString());
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = new CatalogService(new HttpClient(handler), _dir, () => [], () => true, "de", Pem(_official), null,
            () => [new CatalogSourceEntry { Url = "http://plain.example.org/i.json" }, new CatalogSourceEntry { Url = CatalogDefaults.IndexUrl }]);
        await service.LoadAsync();
        Assert.Empty(fetched);
    }

    // ---- install: always third-party

    private string StatementJson(ECDsa signer, string id) => new JsonObject
    {
        ["id"] = id, ["folder"] = id, ["version"] = "1.0.0", ["sha256"] = Hash, ["sdk"] = 3,
        ["capabilities"] = new JsonArray(), ["publisherKey"] = PackageStatements.Fingerprint(Pem(signer)), ["license"] = "MIT"
    }.ToJsonString();

    private SignedStatement Signed(ECDsa signer, string id)
    {
        var bytes = Encoding.UTF8.GetBytes(StatementJson(signer, id));
        var sig = Convert.ToBase64String(signer.SignData(CatalogSignature.WithContext(CatalogSignature.PluginContext, bytes), HashAlgorithmName.SHA256));
        return new SignedStatement(bytes, sig, Pem(signer));
    }

    [Fact]
    public void A_package_with_CouchHUDs_signature_is_not_accepted_from_an_extra_catalog()
    {
        var statement = Signed(_official, "tool");
        Assert.Equal(PluginOrigin.FirstParty, PackageStatements.Verify(statement, Pem(_official)).Origin);
        Assert.Throws<CatalogException>(() => PackageStatements.Verify(statement, Pem(_official), null, allowFirstParty: false));
        // The developer's own key: third-party, from either.
        Assert.Equal(PluginOrigin.ThirdParty, PackageStatements.Verify(Signed(_publisher, "tool"), Pem(_official), null, allowFirstParty: false).Origin);
    }

    [Fact]
    public async Task A_plugin_of_an_extra_catalog_without_a_statement_of_its_developer_cannot_be_installed()
    {
        var service = Service(Index(Entry("tool", "tool")), Index(Entry("other", "other")));
        var result = await service.LoadAsync();
        var entry = result.Entries.Single(e => e.Id == "other");
        Assert.True(entry.Extra);
        // No statement: refused (the official catalog's entries without one are the old, local kind and unaffected).
        Assert.Throws<CatalogException>(() => service.VerifyEntry(entry));
        Assert.Null(service.VerifyEntry(result.Entries.Single(e => e.Id == "tool")));
    }
}
