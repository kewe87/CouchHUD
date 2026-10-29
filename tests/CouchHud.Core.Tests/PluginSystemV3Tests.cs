using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Sdk;

namespace CouchHud.Core.Tests;

/// <summary>
/// The plugin system of 0.6 (SDK 3): signed package statements, the trust store v2, the lint, the capability gate on every context
/// member, the install gates, revocation, suggestions, host services and the media cache. Mostly negative cases: everything fails closed.
/// All keys are throwaway keys made here.
/// </summary>
public sealed class PluginSystemV3Tests : IDisposable
{
    private const string ZipHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "couchhud-v3-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _own = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _dev = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _otherDev = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private static readonly byte[] TrustKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    public PluginSystemV3Tests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _own.Dispose();
        _dev.Dispose();
        _otherDev.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string OwnPem => _own.ExportSubjectPublicKeyInfoPem();
    private static string Pem(ECDsa key) => key.ExportSubjectPublicKeyInfoPem();
    private static string Fp(ECDsa key) => PackageStatements.Fingerprint(Pem(key));

    private static JsonObject StatementJson(ECDsa publisher, string id = "tool", string version = "1.0.0", string? license = "MIT", params string[] caps)
    {
        var o = new JsonObject
        {
            ["id"] = id,
            ["folder"] = id,
            ["version"] = version,
            ["sha256"] = ZipHash,
            ["sdk"] = 3,
            ["capabilities"] = new JsonArray(caps.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
            ["publisherKey"] = Fp(publisher),
        };
        if (license is not null) o["license"] = license;
        return o;
    }

    private static SignedStatement Sign(JsonObject statement, ECDsa signer, string? publisherPem, string context = CatalogSignature.PluginContext)
    {
        var bytes = Encoding.UTF8.GetBytes(statement.ToJsonString());
        var sig = Convert.ToBase64String(signer.SignData(CatalogSignature.WithContext(context, bytes), HashAlgorithmName.SHA256));
        return new SignedStatement(bytes, sig, publisherPem);
    }

    private VerifiedStatement Verify(SignedStatement s) => PackageStatements.Verify(s, OwnPem);

    private void Refused(string key, Action act) => Assert.Equal(Strings.Get(key), Assert.Throws<CatalogException>(act).Message);

    private string PluginDir(string folder, string content = "v1")
    {
        var dir = Path.Combine(_dir, "plugins", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, folder + ".dll"), content);
        return dir;
    }

    // =====================================================================
    // Package statements

    [Fact]
    public void A_statement_signed_with_CouchHUDs_key_is_first_party()
    {
        var v = Verify(Sign(StatementJson(_own, "casehud"), _own, null));
        Assert.Equal(PluginOrigin.FirstParty, v.Origin);
        Assert.Equal("casehud", v.Statement.Id);
    }

    [Fact]
    public void A_statement_signed_with_the_developers_own_key_is_third_party()
    {
        var v = Verify(Sign(StatementJson(_dev), _dev, Pem(_dev)));
        Assert.Equal(PluginOrigin.ThirdParty, v.Origin);
        Assert.Equal(Fp(_dev), v.KeyFingerprint);
    }

    [Fact]
    public void A_signature_in_another_context_is_refused()
    {
        // The same key, but signed as a catalog index or a release: never a plugin statement.
        Refused("plugin.statement.badSignature", () => Verify(Sign(StatementJson(_own, "casehud"), _own, null, CatalogSignature.CatalogContext)));
        Refused("plugin.statement.badSignature", () => Verify(Sign(StatementJson(_dev), _dev, Pem(_dev), CatalogSignature.ReleaseContext)));
    }

    [Fact]
    public void A_changed_statement_or_a_missing_signature_is_refused()
    {
        var signed = Sign(StatementJson(_dev), _dev, Pem(_dev));
        var bytes = (byte[])signed.Statement.Clone();
        bytes[^2] ^= 1;
        Refused("plugin.statement.badSignature", () => Verify(signed with { Statement = bytes }));
        Refused("plugin.statement.badSignature", () => Verify(signed with { Signature = null }));
        Refused("plugin.statement.badSignature", () => Verify(signed with { PublisherKeyPem = null }));
        Refused("plugin.statement.badSignature", () => Verify(signed with { PublisherKeyPem = "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----" }));
    }

    [Fact]
    public void A_statement_must_name_the_key_that_signed_it()
    {
        // Signed by one developer, names another: not this publisher's package.
        Refused("plugin.statement.keyMismatch", () => Verify(Sign(StatementJson(_otherDev), _dev, Pem(_dev))));
    }

    [Fact]
    public void A_third_party_cannot_borrow_CouchHUDs_public_key()
    {
        // Brings CouchHUD's public key as its "own", signs with another: refused, never first-party and never third-party.
        Refused("plugin.statement.badSignature", () => Verify(Sign(StatementJson(_own), _dev, OwnPem)));
    }

    [Theory]
    [InlineData("wand")]
    [InlineData("casehud")]
    [InlineData("casehud-pro")]
    [InlineData("c0uchhud-extras")]
    [InlineData("nordvpn")]
    public void A_third_party_may_not_use_a_reserved_name(string id)
    {
        Refused("plugin.statement.reserved", () => Verify(Sign(StatementJson(_dev, id), _dev, Pem(_dev))));
    }

    [Fact]
    public void A_third_party_may_not_use_the_id_of_a_first_party_plugin_from_the_index()
    {
        var s = Sign(StatementJson(_dev, "lgtv"), _dev, Pem(_dev));
        Assert.Throws<CatalogException>(() => PackageStatements.Verify(s, OwnPem, ["lgtv"]));
        Assert.Equal(PluginOrigin.ThirdParty, PackageStatements.Verify(s, OwnPem).Origin);
    }

    [Theory]
    [InlineData("Proprietary")]
    [InlineData("LicenseRef-Commercial")]
    [InlineData("MIT AND Proprietary")]
    [InlineData(null)]
    public void A_package_without_a_free_licence_is_refused(string? license)
    {
        Refused("plugin.statement.notFree", () => Verify(Sign(StatementJson(_dev, license: license), _dev, Pem(_dev))));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("inAppPurchase")]
    [InlineData("license_key")]
    [InlineData("subscription")]
    public void A_paid_marker_in_the_statement_is_refused(string field)
    {
        var s = StatementJson(_dev);
        s[field] = "4.99";
        Refused("plugin.statement.notFree", () => Verify(Sign(s, _dev, Pem(_dev))));
    }

    [Fact]
    public void An_unknown_field_is_refused()
    {
        var s = StatementJson(_dev);
        s["autoUpdateFrom"] = "https://example.invalid/";
        Refused("plugin.statement.bad", () => Verify(Sign(s, _dev, Pem(_dev))));
    }

    [Theory]
    [InlineData(PluginCapabilities.SetupRun, "plugin.cap.firstPartyOnly")]
    [InlineData(PluginCapabilities.LockRestrict, "plugin.cap.firstPartyOnly")]
    [InlineData(PluginCapabilities.StartupArgs, "plugin.cap.firstPartyOnly")]
    [InlineData(PluginCapabilities.DynamicCode, "plugin.cap.dynamicCode")]
    [InlineData("filesystem.everything", "plugin.cap.unknown")]
    public void A_third_party_statement_cannot_ask_for_what_it_may_not_have(string cap, string reason)
    {
        // The refusal names the capability that is at fault.
        Assert.Equal(Strings.Get(reason) + ": " + cap, Assert.Throws<CatalogException>(() => Verify(Sign(StatementJson(_dev, caps: cap), _dev, Pem(_dev)))).Message);
    }

    [Fact]
    public void First_party_may_declare_first_party_capabilities()
    {
        var v = Verify(Sign(StatementJson(_own, "casehud", "1.0.0", "MIT", PluginCapabilities.SetupRun, PluginCapabilities.LockRestrict, PluginCapabilities.StartupArgs), _own, null));
        Assert.Contains(PluginCapabilities.SetupRun, v.Statement.Capabilities);
    }

    [Fact]
    public void Media_beyond_the_limits_is_refused()
    {
        var s = StatementJson(_dev);
        s["icon"] = new JsonObject { ["sha256"] = ZipHash, ["bytes"] = PackageMedia.MaxIconBytes + 1 };
        Refused("plugin.statement.bad", () => Verify(Sign(s, _dev, Pem(_dev))));

        s = StatementJson(_dev);
        s["screenshots"] = new JsonArray(Enumerable.Range(0, PackageMedia.MaxScreenshots + 1)
            .Select(_ => (JsonNode)new JsonObject { ["sha256"] = ZipHash, ["bytes"] = 10 }).ToArray());
        Refused("plugin.statement.bad", () => Verify(Sign(s, _dev, Pem(_dev))));

        s = StatementJson(_dev);
        s["icon"] = new JsonObject { ["sha256"] = "not-a-hash", ["bytes"] = 10 };
        Refused("plugin.statement.bad", () => Verify(Sign(s, _dev, Pem(_dev))));
    }

    [Fact]
    public void Media_within_the_limits_is_read()
    {
        var s = StatementJson(_dev);
        s["icon"] = new JsonObject { ["sha256"] = ZipHash, ["bytes"] = 1000 };
        s["name"] = "Tool";
        s["summary"] = "Does a thing";
        s["changelog"] = "https://example.org/changes";
        var v = Verify(Sign(s, _dev, Pem(_dev)));
        Assert.Equal(1000, v.Statement.Icon!.Bytes);
        Assert.Equal("Tool", v.Statement.Name);
    }

    [Fact]
    public void The_statement_must_belong_to_the_package()
    {
        var v = Verify(Sign(StatementJson(_dev), _dev, Pem(_dev)));
        Refused("plugin.statement.mismatch", () => PackageStatements.CheckMatches(v, new string('b', 64)));
        PackageStatements.CheckMatches(v, ZipHash);
        var entry = CatalogParser.Parse($$"""[{"id":"tool","name":"Tool","type":"plugin","version":"1.0.1","download":"https://x.example/t.zip","sha256":"{{ZipHash}}"}]""", "s")[0];
        Refused("plugin.statement.mismatch", () => PackageStatements.CheckMatches(v, ZipHash, entry));
    }

    [Fact]
    public void An_envelope_that_is_not_one_is_refused()
    {
        Refused("plugin.statement.bad", () => PackageStatements.ParseEnvelope("[]"));
        Refused("plugin.statement.bad", () => PackageStatements.ParseEnvelope("{\"statement\":\"%%%\"}"));
        Refused("plugin.statement.bad", () => PackageStatements.ParseEnvelope("{"));
        Refused("plugin.statement.bad", () => PackageStatements.ParseEnvelope(new string(' ', PackageStatements.MaxEnvelopeBytes + 1)));
    }

    // ---- against what is installed: key pin (TOFU), downgrade, folder takeover

    private PluginTrust InstalledTool(ECDsa key, string version)
    {
        var trust = new PluginTrust(_dir, TrustKey);
        var v = Verify(Sign(StatementJson(key, version: version), key, Pem(key)));
        trust.Trust("tool", PluginDir("tool"), v.ToTrustEntry());
        return trust;
    }

    [Fact]
    public void An_update_signed_with_another_key_is_refused_and_never_re_pinned()
    {
        var trust = InstalledTool(_dev, "1.0.0");
        var other = Verify(Sign(StatementJson(_otherDev, version: "2.0.0"), _otherDev, Pem(_otherDev)));
        Refused("plugin.statement.keyChanged", () => PackageStatements.CheckAgainstInstalled(other, trust));
        Assert.Equal(Fp(_dev), trust.PinnedKey("tool"));
    }

    [Fact]
    public void A_downgrade_is_refused_also_below_a_version_installed_earlier()
    {
        var trust = InstalledTool(_dev, "2.0.0");
        var older = Verify(Sign(StatementJson(_dev, version: "1.9.0"), _dev, Pem(_dev)));
        Refused("plugin.statement.downgrade", () => PackageStatements.CheckAgainstInstalled(older, trust));
        var newer = Verify(Sign(StatementJson(_dev, version: "2.1.0"), _dev, Pem(_dev)));
        PackageStatements.CheckAgainstInstalled(newer, trust);
    }

    [Fact]
    public void A_signed_third_party_package_cannot_take_over_a_folder_installed_by_hand()
    {
        var trust = new PluginTrust(_dir, TrustKey);
        trust.Trust("tool", PluginDir("tool"));
        var v = Verify(Sign(StatementJson(_dev), _dev, Pem(_dev)));
        Refused("plugin.statement.keyChanged", () => PackageStatements.CheckAgainstInstalled(v, trust));
        // CouchHUD's own key may (its update of its own plugin).
        var own = Verify(Sign(StatementJson(_own), _own, null));
        PackageStatements.CheckAgainstInstalled(own, trust);
    }

    // =====================================================================
    // Trust store v2

    private static string MacV1(string entries) =>
        Convert.ToBase64String(HMACSHA256.HashData(TrustKey, Encoding.UTF8.GetBytes("plugin-trust\n" + entries)));

    [Fact]
    public void A_format_1_list_is_migrated_and_grants_nothing_new()
    {
        var dir = PluginDir("Hallo");
        var hash = PluginTrust.HashFolder(dir);
        File.WriteAllText(Path.Combine(_dir, "plugin-trust.json"),
            JsonSerializer.Serialize(new { Entries = new Dictionary<string, string> { ["Hallo"] = hash }, Mac = MacV1("hallo=" + hash) }));

        var trust = new PluginTrust(_dir, TrustKey);
        Assert.Equal(PluginTrustState.Trusted, trust.Check("Hallo", dir));
        var entry = trust.Entry("Hallo")!;
        Assert.Equal(PluginOrigin.Local, entry.Origin);
        Assert.Null(entry.Capabilities);
        Assert.Null(entry.KeyFingerprint);
        Assert.Empty(entry.ConfirmedExes);
        Assert.Contains("\"Format\":2", File.ReadAllText(Path.Combine(_dir, "plugin-trust.json")));

        // Whatever its plugin.json declares now: no more than the SDK 2 surface, no host services.
        var granted = PluginPolicy.Granted(entry.Origin, [PluginCapabilities.Process, PluginCapabilities.NetworkState, PluginCapabilities.SetupRun, PluginCapabilities.Pages], 3, entry.Capabilities);
        Assert.Equal([PluginCapabilities.Pages], granted.ToList());
    }

    [Fact]
    public void A_format_1_list_with_a_wrong_signature_is_not_migrated()
    {
        var dir = PluginDir("Hallo");
        var hash = PluginTrust.HashFolder(dir);
        File.WriteAllText(Path.Combine(_dir, "plugin-trust.json"),
            JsonSerializer.Serialize(new { Entries = new Dictionary<string, string> { ["Hallo"] = hash }, Mac = MacV1("other=" + hash) }));
        Assert.Equal(PluginTrustState.Fault, new PluginTrust(_dir, TrustKey).Check("Hallo", dir));
        Assert.DoesNotContain("\"Format\"", File.ReadAllText(Path.Combine(_dir, "plugin-trust.json")));
    }

    [Theory]
    [InlineData("\"Origin\":\"thirdParty\"", "\"Origin\":\"firstParty\"")]
    [InlineData("\"Capabilities\":[\"pages\"]", "\"Capabilities\":[\"pages\",\"setup.run\"]")]
    [InlineData("\"ConfirmedExes\":[]", "\"ConfirmedExes\":[\"C:\\\\Windows\\\\System32\\\\cmd.exe\"]")]
    public void A_changed_entry_makes_the_whole_list_a_fault(string from, string to)
    {
        var dir = PluginDir("tool");
        var trust = new PluginTrust(_dir, TrustKey);
        trust.Trust("tool", dir, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.ThirdParty, PluginId = "tool", Capabilities = [PluginCapabilities.Pages] });
        var file = Path.Combine(_dir, "plugin-trust.json");
        var text = File.ReadAllText(file);
        File.WriteAllText(file, text.Replace(from, to));
        Assert.NotEqual(text, File.ReadAllText(file));
        Assert.Equal(PluginTrustState.Fault, new PluginTrust(_dir, TrustKey).Check("tool", dir));
        // And nothing is written over a faulty list.
        Assert.Throws<InvalidOperationException>(() => new PluginTrust(_dir, TrustKey).ConfirmExe("tool", @"C:\x.exe"));
    }

    [Fact]
    public void Pending_updates_confirmed_programs_and_accepted_revocations_are_kept_and_bound()
    {
        var dir = PluginDir("tool");
        var trust = new PluginTrust(_dir, TrustKey);
        trust.Trust("tool", dir, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.ThirdParty, PluginId = "tool", Version = "1.0.0" });

        trust.StagePending("tool", new PluginTrustEntry { Hash = ZipHash, PluginId = "tool", Version = "1.1.0" });
        Assert.Equal("1.1.0", trust.TakePending("tool")!.Version);
        Assert.Null(trust.TakePending("tool"));

        trust.ConfirmExe("tool", @"C:\Program Files\Tool\tool.exe");
        Assert.Contains(@"C:\Program Files\Tool\tool.exe", trust.Entry("tool")!.ConfirmedExes);
        Assert.Throws<ArgumentException>(() => trust.ConfirmExe("nothere", @"C:\Program Files\Tool\tool.exe"));

        // A confirmed program stays for an update of the same plugin, never for another plugin in that folder.
        trust.Trust("tool", dir, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.ThirdParty, PluginId = "tool", Version = "1.1.0" });
        Assert.NotEmpty(trust.Entry("tool")!.ConfirmedExes);
        trust.Trust("tool", dir, new PluginTrustEntry { Hash = "", Origin = PluginOrigin.ThirdParty, PluginId = "other", Version = "1.0.0" });
        Assert.Empty(trust.Entry("tool")!.ConfirmedExes);

        // An accepted revocation is for exactly these files.
        trust.AcceptRevocation("tool", dir);
        Assert.Equal(trust.Entry("tool")!.Hash, trust.Entry("tool")!.RevocationAccepted);
        File.WriteAllText(Path.Combine(dir, "tool.dll"), "changed");
        Assert.Throws<ArgumentException>(() => trust.AcceptRevocation("tool", dir));
    }

    [Fact]
    public void The_highest_version_ever_installed_is_remembered()
    {
        var dir = PluginDir("tool");
        var trust = new PluginTrust(_dir, TrustKey);
        trust.Trust("tool", dir, new PluginTrustEntry { Hash = "", PluginId = "tool", Version = "2.0.0" });
        trust.Trust("tool", dir, new PluginTrustEntry { Hash = "", PluginId = "tool", Version = "1.0.0" });
        Assert.Equal("2.0.0", trust.Entry("tool")!.HighestVersion);
    }

    // =====================================================================
    // Capabilities and the gate on every context member

    [Fact]
    public void Every_member_of_the_plugin_context_has_an_entry_in_the_gate_table()
    {
        var members = typeof(IPluginContext).GetMethods().Where(m => !m.IsSpecialName).Select(m => m.Name)
            .Concat(typeof(IPluginContext).GetProperties().Select(p => p.Name))
            .Distinct().ToList();
        Assert.NotEmpty(members);
        foreach (var m in members) Assert.True(PluginPolicy.ContextMembers.ContainsKey(m), $"{m} has no gate");
        // And nothing in the table that is not a member (a typo would leave a member ungated).
        foreach (var m in PluginPolicy.ContextMembers.Keys) Assert.Contains(m, members);
    }

    [Fact]
    public void Every_capability_named_in_the_gate_table_is_known()
    {
        foreach (var cap in PluginPolicy.ContextMembers.Values.Where(c => c is not null)) Assert.Contains(cap!, PluginCapabilities.All);
        foreach (var cap in PluginPolicy.FirstPartyOnly) Assert.Contains(cap, PluginCapabilities.All);
    }

    private IPluginContext Context(PluginOrigin origin, params string[] caps)
    {
        var manager = new PluginManager([(Path.Combine(_dir, "plugins"), true)], _dir, _ => true) { Trust = new PluginTrust(_dir, TrustKey) };
        return manager.ContextFor("tool", new PluginManager.PluginGrant("tool", origin, new HashSet<string>(caps)));
    }

    [Fact]
    public async Task A_context_without_capabilities_refuses_every_gated_member()
    {
        var ctx = Context(PluginOrigin.FirstParty);
        Assert.Throws<PluginCapabilityException>(() => ctx.Downloads);
        Assert.Throws<PluginCapabilityException>(() => ctx.Network);
        Assert.Throws<PluginCapabilityException>(() => ctx.RestrictWhileLocked(@"C:\Program Files\X\x.exe"));
        Assert.Throws<PluginCapabilityException>(() => ctx.AdaptStartupArgs(@"C:\Program Files\X\x.exe", a => a));
        await Assert.ThrowsAsync<PluginCapabilityException>(() => ctx.RunSetupAsync(@"C:\x.exe", ZipHash, CancellationToken.None));
        await Assert.ThrowsAsync<PluginCapabilityException>(() => ctx.RunTrustedCliAsync(@"C:\Program Files\X\x.exe", [], TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Throws<PluginCapabilityException>(() => ctx.RegisterPage(new PluginPage { Id = "p", Title = "P", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) }));
        Assert.Throws<PluginCapabilityException>(() => ctx.Invalidate("p"));
        Assert.Throws<PluginCapabilityException>(() => ctx.RegisterTab(new PluginTab("t", "p", "Titel", "Title")));
        Assert.Throws<PluginCapabilityException>(() => ctx.RegisterQuickAction(new QuickAction { Id = "a", Label = "A", Run = _ => Task.FromResult<string?>(null) }));
        Assert.Throws<PluginCapabilityException>(() => ctx.RegisterTool(null!));
        Assert.Throws<PluginCapabilityException>(() => ctx.RegisterLibraryProvider(null!));
        Assert.Throws<PluginCapabilityException>(() => ctx.Notify("a", "b"));
        Assert.Throws<PluginCapabilityException>(() => ctx.OpenUrl("https://example.org"));
        // The ungated basics stay.
        ctx.Log("hello");
        Assert.NotNull(ctx.DataDirectory);
        Assert.NotNull(ctx.Language);
    }

    [Fact]
    public void A_granted_capability_opens_exactly_its_member()
    {
        var ctx = Context(PluginOrigin.FirstParty, PluginCapabilities.NetworkState, PluginCapabilities.Pages);
        Assert.Null(Record.Exception(() => ctx.Network));
        ctx.RegisterPage(new PluginPage { Id = "p", Title = "P", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) });
        Assert.Throws<PluginCapabilityException>(() => ctx.Downloads);
    }

    [Fact]
    public async Task A_cli_call_with_the_capability_still_needs_a_confirmed_trusted_program()
    {
        var ctx = Context(PluginOrigin.ThirdParty, PluginCapabilities.Process);
        var result = await ctx.RunTrustedCliAsync(@"C:\Windows\System32\cmd.exe", ["/c", "echo"], TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal("notTrusted", result.Refused);
        Assert.Null(result.ExitCode);
    }

    [Theory]
    [InlineData(PluginOrigin.ThirdParty)]
    [InlineData(PluginOrigin.Local)]
    public void First_party_capabilities_are_never_granted_to_others(PluginOrigin origin)
    {
        Assert.Equal("plugin.cap.firstPartyOnly", PluginPolicy.CheckDeclared(origin, [PluginCapabilities.SetupRun]));
        var all = PluginCapabilities.All.ToList();
        var granted = PluginPolicy.Granted(origin, all, 3, all);
        foreach (var cap in PluginPolicy.FirstPartyOnly) Assert.DoesNotContain(cap, granted);
        if (origin == PluginOrigin.ThirdParty) Assert.DoesNotContain(PluginCapabilities.DynamicCode, granted);
    }

    [Fact]
    public void A_plugin_never_gets_more_than_it_was_installed_with()
    {
        var granted = PluginPolicy.Granted(PluginOrigin.FirstParty, [PluginCapabilities.Pages, PluginCapabilities.Process], 3, [PluginCapabilities.Pages]);
        Assert.Equal([PluginCapabilities.Pages], granted.ToList());
        Assert.Empty(PluginPolicy.Granted(PluginOrigin.FirstParty, ["made.up"], 3, ["made.up"]));
        // SDK 3 without a declaration: nothing at all.
        Assert.Empty(PluginPolicy.Granted(PluginOrigin.Local, null, 3, null));
        // SDK 2 without a declaration: its old surface.
        Assert.Equal(PluginPolicy.Legacy.OrderBy(x => x), PluginPolicy.Granted(PluginOrigin.Local, null, 2, null).OrderBy(x => x));
    }

    // =====================================================================
    // Gates: installing, updating, enabling …

    private static readonly PluginPolicy.GateContext Open = new(PluginCaller.Hud, Locked: false, ChildProfile: false, ExpertMode: true, DeveloperSwitch: true, ParentalEnabled: false);

    [Theory]
    [InlineData(PluginCaller.Remote)]
    [InlineData(PluginCaller.Mcp)]
    [InlineData(PluginCaller.Assistant)]
    [InlineData(PluginCaller.Plugin)]
    public void Only_the_HUD_itself_may_change_plugins(PluginCaller caller)
    {
        foreach (var change in Enum.GetValues<PluginChange>())
        {
            foreach (var origin in Enum.GetValues<PluginOrigin>())
                Assert.Equal("plugin.gate.hudOnly", PluginPolicy.Decide(Open with { Caller = caller }, change, origin, []).Refused);
        }
    }

    [Fact]
    public void The_locked_couch_and_a_child_profile_change_nothing()
    {
        foreach (var change in Enum.GetValues<PluginChange>())
        {
            foreach (var origin in Enum.GetValues<PluginOrigin>())
            {
                Assert.Equal("lock.unlockFirst", PluginPolicy.Decide(Open with { Locked = true }, change, origin, []).Refused);
                Assert.Equal("plugin.gate.child", PluginPolicy.Decide(Open with { ChildProfile = true }, change, origin, []).Refused);
            }
        }
    }

    [Fact]
    public void A_third_party_install_needs_expert_mode_and_always_the_code()
    {
        Assert.Equal("plugin.gate.expert", PluginPolicy.Decide(Open with { ExpertMode = false }, PluginChange.Install, PluginOrigin.ThirdParty, []).Refused);
        var d = PluginPolicy.Decide(Open, PluginChange.Install, PluginOrigin.ThirdParty, []);
        Assert.True(d.Allowed);
        Assert.True(d.ParentCode);
        Assert.Equal("pluginInstall", d.ParentAction);
    }

    [Fact]
    public void A_third_party_update_that_adds_capabilities_counts_as_an_install()
    {
        Assert.Equal("plugin.gate.expert",
            PluginPolicy.Decide(Open with { ExpertMode = false }, PluginChange.Update, PluginOrigin.ThirdParty, [PluginCapabilities.Process], addsCapabilities: true).Refused);
        var plain = PluginPolicy.Decide(Open with { ExpertMode = false }, PluginChange.Update, PluginOrigin.ThirdParty, []);
        Assert.True(plain.Allowed);
        Assert.False(plain.ParentCode);
        Assert.True(PluginPolicy.Decide(Open with { ExpertMode = false, ParentalEnabled = true }, PluginChange.Update, PluginOrigin.ThirdParty, []).ParentCode);
    }

    [Fact]
    public void An_unsigned_package_needs_the_developer_switch_expert_mode_and_the_code()
    {
        Assert.Equal("plugin.gate.unsigned", PluginPolicy.Decide(Open with { DeveloperSwitch = false }, PluginChange.Install, PluginOrigin.Local, []).Refused);
        Assert.Equal("plugin.gate.unsigned", PluginPolicy.Decide(Open with { ExpertMode = false }, PluginChange.Install, PluginOrigin.Local, []).Refused);
        Assert.True(PluginPolicy.Decide(Open, PluginChange.Install, PluginOrigin.Local, []).ParentCode);
    }

    [Theory]
    [InlineData(PluginChange.AcceptRevoked)]
    [InlineData(PluginChange.ConfirmExe)]
    public void Accepting_a_revoked_plugin_or_confirming_a_program_always_needs_the_code(PluginChange change)
    {
        foreach (var origin in Enum.GetValues<PluginOrigin>()) Assert.True(PluginPolicy.Decide(Open, change, origin, []).ParentCode);
    }

    [Fact]
    public void A_first_party_install_needs_the_code_under_parental_controls_only_for_powerful_capabilities()
    {
        Assert.False(PluginPolicy.Decide(Open, PluginChange.Install, PluginOrigin.FirstParty, [PluginCapabilities.Process]).ParentCode);
        Assert.False(PluginPolicy.Decide(Open with { ParentalEnabled = true }, PluginChange.Install, PluginOrigin.FirstParty, [PluginCapabilities.Pages]).ParentCode);
        Assert.True(PluginPolicy.Decide(Open with { ParentalEnabled = true }, PluginChange.Install, PluginOrigin.FirstParty, [PluginCapabilities.Process]).ParentCode);
        Assert.True(PluginPolicy.Decide(Open with { ParentalEnabled = true }, PluginChange.Update, PluginOrigin.FirstParty, [PluginCapabilities.SetupRun]).ParentCode);
    }

    // ---- rows and quick actions

    private static PluginRow Row(PluginRowKind? kind = null, bool whileLocked = false, bool parent = false) =>
        new("r", "Row") { Kind = kind, AllowedWhileLocked = whileLocked, RequiresParentGrant = parent };

    [Fact]
    public void On_the_locked_couch_only_first_party_rows_marked_for_it_may_be_pressed()
    {
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.ThirdParty, Row(whileLocked: true), false, "tool").Refused);
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.Local, Row(whileLocked: true), false, "tool").Refused);
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayPress(true, PluginOrigin.FirstParty, Row(), false, "nordvpn").Refused);
        Assert.True(PluginPolicy.MayPress(true, PluginOrigin.FirstParty, Row(PluginRowKind.Toggle, whileLocked: true), false, "nordvpn").Allowed);
        Assert.Equal("plugin.pageNotFound", PluginPolicy.MayPress(false, PluginOrigin.FirstParty, null, false, "nordvpn").Refused);
    }

    [Fact]
    public void A_row_that_asks_for_the_parents_grant_gets_it_under_parental_controls()
    {
        Assert.True(PluginPolicy.MayPress(false, PluginOrigin.FirstParty, Row(parent: true), true, "nordvpn").ParentCode);
        Assert.True(PluginPolicy.MayPress(true, PluginOrigin.FirstParty, Row(whileLocked: true, parent: true), true, "nordvpn").ParentCode);
        Assert.False(PluginPolicy.MayPress(false, PluginOrigin.FirstParty, Row(), true, "nordvpn").ParentCode);
        // A third party's row that changes something always needs it under parental controls.
        Assert.True(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, Row(), true, "tool").ParentCode);
    }

    [Fact]
    public void A_link_row_changes_nothing_but_still_stays_hidden_on_the_locked_couch()
    {
        var link = Row(new PluginRowKind.Link("other"));
        Assert.True(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, link, true, "tool").Allowed);
        Assert.False(PluginPolicy.MayPress(false, PluginOrigin.ThirdParty, link, true, "tool").ParentCode);
        Assert.False(PluginPolicy.MayPress(true, PluginOrigin.ThirdParty, link, false, "tool").Allowed);
    }

    [Fact]
    public void Locked_pages_show_only_what_was_marked_for_it()
    {
        var page = new PluginPage { Id = "p", Title = "P", Rows = _ => Task.FromResult<IReadOnlyList<PluginRow>>([]) };
        Assert.False(PluginPolicy.MayView(true, page));
        Assert.True(PluginPolicy.MayView(false, page));
        Assert.True(PluginPolicy.MayView(true, new PluginPage { Id = "p", Title = "P", Rows = page.Rows, AllowedWhileLocked = true }));
        var rows = new[] { Row(whileLocked: true), new PluginRow("s", "Secret") };
        Assert.Single(PluginPolicy.Visible(true, rows));
        Assert.Equal(2, PluginPolicy.Visible(false, rows).Count);
    }

    [Fact]
    public void Quick_actions_never_run_locked_and_third_party_tools_always_ask()
    {
        var action = new QuickAction { Id = "a", Label = "A", Run = _ => Task.FromResult<string?>(null) };
        Assert.Equal("lock.unlockFirst", PluginPolicy.MayRun(true, PluginOrigin.FirstParty, action, false).Refused);
        Assert.True(PluginPolicy.MayRun(false, PluginOrigin.ThirdParty, action, true).ParentCode);
        Assert.False(PluginPolicy.MayRun(false, PluginOrigin.FirstParty, action, true).ParentCode);
        Assert.True(PluginPolicy.MayRun(false, PluginOrigin.FirstParty, new QuickAction { Id = "a", Label = "A", Run = action.Run, RequiresParentGrant = true }, true).ParentCode);
        Assert.True(PluginPolicy.ToolWrites(PluginOrigin.ThirdParty, false));
        Assert.True(PluginPolicy.ToolWrites(PluginOrigin.Local, false));
        Assert.False(PluginPolicy.ToolWrites(PluginOrigin.FirstParty, false));
    }

    [Fact]
    public void Plugin_rows_pass_the_bridge_while_locked_only_to_be_decided_by_the_row()
    {
        Assert.Contains("activatePluginRow", LockPolicy.OpenWhileLocked);
        Assert.Contains("changePluginRow", LockPolicy.OpenWhileLocked);
        // A guide menu entry passes the bridge too; the host decides by the entry (PluginPolicy.MayRun): CouchHUD's own plugins, marked for it.
        Assert.Contains("runQuickAction", LockPolicy.OpenWhileLocked);
        foreach (var m in new[] { "installCatalogEntry", "updateCatalogEntry", "uninstallPlugin", "setPluginEnabled", "approvePlugin", "acceptRevokedPlugin", "confirmPluginExe", "installPackage", "dismissPluginSuggestion", "getCatalogSources", "addCatalogSource", "removeCatalogSource" })
            Assert.DoesNotContain(m, LockPolicy.OpenWhileLocked);
    }

    // =====================================================================
    // Lint (not a sandbox: it finds what an honest plugin forgot and turns away the obvious)

    private string Zip(params (string Name, byte[] Content)[] files)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in files)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(content);
        }
        return path;
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("sub/run.bat")]
    [InlineData("install.ps1")]
    [InlineData("driver.sys")]
    [InlineData("x.lnk")]
    [InlineData("SCRIPT.VBS")]
    public void Programs_and_scripts_are_refused_in_any_package(string name)
    {
        foreach (var origin in Enum.GetValues<PluginOrigin>())
            Assert.Throws<CatalogException>(() => PluginLint.CheckZip(Zip((name, [1, 2, 3])), origin, [], legacy: true));
    }

    [Fact]
    public void A_third_party_package_holds_only_known_file_types()
    {
        Assert.Throws<CatalogException>(() => PluginLint.CheckZip(Zip(("data.bin", [1])), PluginOrigin.ThirdParty, []));
        PluginLint.CheckZip(Zip(("plugin.json", "{}"u8.ToArray()), ("icon.png", [1])), PluginOrigin.ThirdParty, []);
    }

    [Fact]
    public void Code_that_uses_more_than_it_declared_is_found()
    {
        // This test assembly (see LintBait below) starts a process, reads the registry, talks to the network and loads an assembly.
        var image = File.ReadAllBytes(typeof(PluginSystemV3Tests).Assembly.Location);
        var findings = PluginLint.Scan(image, "Tests.dll");
        var undeclared = PluginLint.Evaluate(findings, PluginOrigin.ThirdParty, []).Select(f => f.Capability).ToHashSet();
        Assert.Contains(PluginCapabilities.Process, undeclared);
        Assert.Contains(PluginCapabilities.Registry, undeclared);
        Assert.Contains("network", undeclared);
        Assert.Contains(PluginCapabilities.DynamicCode, undeclared);

        // Declared, they are covered, except run-time code for a third party.
        var declared = new[] { PluginCapabilities.Process, PluginCapabilities.Registry, PluginCapabilities.NetworkInternet, PluginCapabilities.DynamicCode, PluginCapabilities.Native };
        var left = PluginLint.Evaluate(findings, PluginOrigin.ThirdParty, declared).Select(f => f.Capability).Distinct().ToList();
        Assert.Equal([PluginCapabilities.DynamicCode], left);
        Assert.Empty(PluginLint.Evaluate(findings, PluginOrigin.FirstParty, declared));

        var zip = Zip(("Core.dll", image));
        Assert.Throws<CatalogException>(() => PluginLint.CheckZip(zip, PluginOrigin.ThirdParty, [PluginCapabilities.Pages]));
    }

    [Fact]
    public void A_dll_that_is_no_dotnet_assembly_counts_as_native_code()
    {
        var findings = PluginLint.Scan(Encoding.ASCII.GetBytes("MZ this is no assembly at all"), "evil.dll");
        Assert.Contains(findings, f => f.Capability == PluginCapabilities.Native);
        Assert.Throws<CatalogException>(() => PluginLint.CheckZip(Zip(("evil.dll", Encoding.ASCII.GetBytes("garbage"))), PluginOrigin.ThirdParty, []));
    }

    // =====================================================================
    // Revocation

    [Fact]
    public void Revocations_are_read_from_the_index_and_match_by_id_version_or_key()
    {
        var fp = Fp(_dev);
        var index = CatalogParser.ParseIndex($$"""
            {"entries":[],"revoked":[
              {"id":"tool","versions":["1.0.0"],"reason":"bad"},
              {"keyFp":"{{fp}}","reason":"stolen key"},
              {"id":"all-of-it"},
              {"id":"NOT VALID"},
              {"keyFp":"short"}
            ]}
            """, "s");
        Assert.Equal(3, index.Revoked.Count);
        Assert.True(index.Revoked[0].Matches("tool", "1.0.0", null));
        Assert.False(index.Revoked[0].Matches("tool", "1.0.1", null));
        Assert.False(index.Revoked[0].Matches("tool", null, null));
        Assert.True(index.Revoked[1].Matches("anything", "9.9.9", fp.ToUpperInvariant()));
        Assert.False(index.Revoked[1].Matches("anything", "9.9.9", null));
        Assert.True(index.Revoked[2].Matches("ALL-OF-IT", "3.0.0", null));
    }

    // =====================================================================
    // Catalog entries (the list and the detail page)

    [Fact]
    public void The_catalog_reads_the_detail_fields_and_drops_what_is_not_allowed()
    {
        var e = CatalogParser.Parse($$"""
            [{"id":"tool","name":"Tool","type":"plugin","version":"1.0.0","download":"https://x.example/t.zip","sha256":"{{ZipHash}}",
              "license":"MIT","longDescription":{"de":"Lang","en":"Long"},"iconSha256":"{{ZipHash.ToUpperInvariant()}}",
              "screenshots":["https://x.example/1.png","http://x.example/2.png","file:///C:/3.png"],
              "changelog":"Fixed things","size":1234,"capabilities":["pages","made.up","pages"],
              "suggestWhen":{"uninstallEntry":"NordVPN"},"statement":"e30=","signature":"AAAA","publisherKey":"pem"}]
            """, "s", "en")[0];
        Assert.Null(e.Problem);
        Assert.Equal("Long", e.LongDescription);
        Assert.Equal(ZipHash, e.IconSha256);
        Assert.Equal(["https://x.example/1.png"], e.Screenshots);
        Assert.Equal("Fixed things", e.Changelog);
        Assert.Equal(1234, e.Size);
        Assert.Equal([PluginCapabilities.Pages], e.Capabilities);
        Assert.Equal("NordVPN", e.SuggestWhenUninstallEntry);
        Assert.Equal("e30=", e.Statement);
    }

    [Theory]
    [InlineData("\"license\":\"Proprietary\"")]
    [InlineData("\"price\":\"4.99\"")]
    [InlineData("\"premium\":true")]
    public void A_catalog_entry_that_is_not_free_cannot_be_installed(string field)
    {
        var e = CatalogParser.Parse($$"""
            [{"id":"tool","name":"Tool","type":"plugin","version":"1.0.0","download":"https://x.example/t.zip","sha256":"{{ZipHash}}",{{field}}}]
            """, "s")[0];
        Assert.Equal(Strings.Get("plugin.statement.notFree"), e.Problem);
        Assert.Null(e.DownloadUrl);
    }

    [Fact]
    public void A_statement_without_a_signature_makes_the_entry_unavailable()
    {
        var e = CatalogParser.Parse($$"""
            [{"id":"tool","name":"Tool","type":"plugin","version":"1.0.0","download":"https://x.example/t.zip","sha256":"{{ZipHash}}","statement":"e30="}]
            """, "s")[0];
        Assert.Equal(Strings.Get("plugin.statement.bad"), e.Problem);
    }

    // =====================================================================
    // Media cache: never hot-linked, only verified PNGs

    private static byte[] Png(int size = 100, uint width = 64, uint height = 64)
    {
        var b = new byte[size];
        new Random(size).NextBytes(b);
        byte[] magic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        magic.CopyTo(b, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(8), 13);
        "IHDR"u8.CopyTo(b.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(20), height);
        return b;
    }

    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    [Fact]
    public async Task A_verified_icon_comes_back_as_a_data_uri_and_is_read_from_the_cache()
    {
        var png = Png();
        var fetched = 0;
        var cache = new CatalogMediaCache(_dir, (_, _, _) =>
        {
            fetched++;
            return Task.FromResult<byte[]?>(png);
        });
        var uri = await cache.GetAsync("https://x.example/i.png", Sha(png), PackageMedia.MaxIconBytes);
        Assert.Equal("data:image/png;base64," + Convert.ToBase64String(png), uri);
        Assert.Equal(uri, await cache.GetAsync("https://x.example/i.png", Sha(png), PackageMedia.MaxIconBytes));
        Assert.Equal(1, fetched);
    }

    [Fact]
    public async Task Wrong_media_is_never_served()
    {
        var png = Png();
        var notPng = Encoding.ASCII.GetBytes("<svg onload=alert(1)>");
        byte[] answer = png;
        var fetched = 0;
        var cache = new CatalogMediaCache(_dir, (_, _, _) =>
        {
            fetched++;
            return Task.FromResult<byte[]?>(answer);
        });
        // Wrong hash, no hash, too large.
        Assert.Null(await cache.GetAsync("https://x.example/i.png", new string('c', 64), PackageMedia.MaxIconBytes));
        Assert.Null(await cache.GetAsync("https://x.example/i.png", null, PackageMedia.MaxIconBytes));
        Assert.Null(await cache.GetAsync("https://x.example/i.png", Sha(png), png.Length - 1));
        // Not a PNG, even with the right hash.
        answer = notPng;
        Assert.Null(await cache.GetAsync("https://x.example/i.svg", Sha(notPng), PackageMedia.MaxIconBytes));
        // No plain http, no file: without the developer switch: not even fetched.
        var before = fetched;
        Assert.Null(await cache.GetAsync("http://x.example/i.png", Sha(png), PackageMedia.MaxIconBytes));
        Assert.Null(await cache.GetAsync("file:///C:/i.png", Sha(png), PackageMedia.MaxIconBytes));
        Assert.Equal(before, fetched);
    }

    [Fact]
    public async Task A_changed_file_in_the_cache_is_not_served()
    {
        var png = Png();
        var cache = new CatalogMediaCache(_dir, (_, _, _) => Task.FromResult<byte[]?>(png));
        Assert.NotNull(await cache.GetAsync("https://x.example/i.png", Sha(png), PackageMedia.MaxIconBytes));
        var file = Path.Combine(_dir, "catalog-media", Sha(png) + ".png");
        Assert.True(File.Exists(file));
        File.WriteAllBytes(file, Png(120));
        var offline = new CatalogMediaCache(_dir, (_, _, _) => Task.FromResult<byte[]?>(null));
        Assert.Null(await offline.GetAsync("https://x.example/i.png", Sha(png), PackageMedia.MaxIconBytes));
    }

    [Fact]
    public async Task Screenshots_only_come_with_a_signed_statement()
    {
        var png = Png();
        var cache = new CatalogMediaCache(_dir, (_, _, _) => Task.FromResult<byte[]?>(png));
        var entry = CatalogParser.Parse($$"""
            [{"id":"tool","name":"Tool","type":"plugin","version":"1.0.0","download":"https://x.example/t.zip","sha256":"{{ZipHash}}",
              "screenshots":["https://x.example/1.png"]}]
            """, "s")[0];
        Assert.Empty(await cache.ScreenshotsAsync(entry, null));
        var statement = Verify(Sign(StatementJson(_dev), _dev, Pem(_dev))).Statement with { Screenshots = [new PackageMedia(Sha(png), png.Length)] };
        Assert.Single(await cache.ScreenshotsAsync(entry, statement));
    }

    // =====================================================================
    // Suggestions and setting seeds

    private static IReadOnlyList<CatalogEntry> Entries() => CatalogParser.Parse($$$"""
        [{"id":"nordvpn","name":"NordVPN","type":"plugin","version":"1.0.0","download":"https://x.example/n.zip","sha256":"{{{ZipHash}}}","suggestWhen":{"uninstallEntry":"NordVPN"}},
         {"id":"wand","name":"Wand","type":"plugin","version":"1.0.0","download":"https://x.example/w.zip","sha256":"{{{ZipHash}}}"},
         {"id":"skinny","name":"Skin","type":"skin","version":"1.0.0","download":"https://x.example/s.zip","sha256":"{{{ZipHash}}}","suggestWhen":{"uninstallEntry":"NordVPN"}}]
        """, "s");

    private static FakeRegistry WithNord()
    {
        var reg = new FakeRegistry();
        reg.WriteString(PluginSuggestions.UninstallRoot + @"\{1234}", "DisplayName", "NordVPN");
        return reg;
    }

    [Fact]
    public void A_matching_program_suggests_its_plugin_once_per_user_and_never_while_locked()
    {
        var s = new PluginSuggestions(_dir, WithNord(), "Anna");
        Assert.Empty(s.Due(Entries(), [], locked: true));
        var due = s.Due(Entries(), [], locked: false);
        var only = Assert.Single(due);
        Assert.Equal(("nordvpn", "program", "NordVPN"), (only.EntryId, only.Reason, only.Program));
        s.MarkOffered("nordvpn");
        Assert.Empty(new PluginSuggestions(_dir, WithNord(), "ANNA").Due(Entries(), [], false));
        Assert.Single(new PluginSuggestions(_dir, WithNord(), "Ben").Due(Entries(), [], false));
        Assert.Empty(new PluginSuggestions(_dir, WithNord(), "Ben").Due(Entries(), ["NordVPN"], false));
    }

    [Fact]
    public void Wand_is_suggested_to_those_who_used_the_bundled_one()
    {
        Assert.Empty(new PluginSuggestions(_dir, new FakeRegistry(), "Anna").Due(Entries(), [], false));
        Directory.CreateDirectory(Path.Combine(_dir, "plugin-data", "wand"));
        var due = new PluginSuggestions(_dir, new FakeRegistry(), "Anna").Due(Entries(), [], false);
        Assert.Equal("wand", Assert.Single(due).Reason);
    }

    [Fact]
    public void Only_CaseHUD_gets_the_settings_CouchHUD_kept_for_it()
    {
        var config = new AppConfig { CaseHudGamingLease = false };
        var seeds = PluginSettingSeeds.For(config, "casehud")!;
        // Only the game report is a setting of the plugin; the setup options are the CaseHUD setup's own questions.
        Assert.Equal("false", seeds["gamingLease"]);
        Assert.False(seeds.ContainsKey("installFps"));
        Assert.Null(PluginSettingSeeds.For(config, "tool"));
        Assert.Null(PluginSettingSeeds.For(config, "casehud-pro"));
    }

    // =====================================================================
    // Host services: programs, startup arguments, network state

    private const string NordExe = @"C:\Program Files\NordVPN\NordVPN.exe";

    private static PluginHostServices Host() => new() { CanonicalTrustedExe = exe => exe.StartsWith(@"C:\Program Files\", StringComparison.OrdinalIgnoreCase) ? exe : null };

    [Fact]
    public void A_command_line_runs_only_for_a_confirmed_trusted_program_with_clean_arguments()
    {
        var host = Host();
        var ok = TimeSpan.FromSeconds(10);
        Assert.Equal("notTrusted", new PluginHostServices().CheckCli(NordExe, [], ok, PluginOrigin.FirstParty, false, [NordExe], out _));
        Assert.Equal("notTrusted", host.CheckCli(@"C:\Windows\System32\cmd.exe", [], ok, PluginOrigin.FirstParty, false, [@"C:\Windows\System32\cmd.exe"], out _));
        Assert.Equal("notTrusted", host.CheckCli(@"nordvpn.exe", [], ok, PluginOrigin.FirstParty, false, [], out _));
        Assert.Equal("notConfirmed", host.CheckCli(NordExe, [], ok, PluginOrigin.FirstParty, false, [], out _));
        Assert.Equal("locked", host.CheckCli(NordExe, [], ok, PluginOrigin.ThirdParty, true, [NordExe], out _));
        Assert.Equal("badArguments", host.CheckCli(NordExe, ["connect\nrm"], ok, PluginOrigin.FirstParty, false, [NordExe], out _));
        Assert.Equal("badArguments", host.CheckCli(NordExe, ["a\u2028b"], ok, PluginOrigin.FirstParty, false, [NordExe], out _));
        Assert.Equal("badArguments", host.CheckCli(NordExe, Enumerable.Repeat("x", PluginHostServices.MaxArguments + 1).ToList(), ok, PluginOrigin.FirstParty, false, [NordExe], out _));
        Assert.Equal("badTimeout", host.CheckCli(NordExe, [], TimeSpan.Zero, PluginOrigin.FirstParty, false, [NordExe], out _));
        Assert.Equal("badTimeout", host.CheckCli(NordExe, [], TimeSpan.FromMinutes(5), PluginOrigin.FirstParty, false, [NordExe], out _));
        Assert.Null(host.CheckCli(NordExe, ["status"], ok, PluginOrigin.FirstParty, true, [NordExe.ToUpperInvariant()], out var canonical));
        Assert.Equal(NordExe, canonical);
    }

    [Fact]
    public void A_startup_adapter_changes_only_the_arguments_of_its_own_program()
    {
        var host = Host();
        Assert.False(host.AddStartupAdapter("casehud", @"C:\Windows\x.exe", a => a));
        Assert.True(host.AddStartupAdapter("casehud", NordExe, a => a + " --hidden"));
        var compose = host.ComposeStartup(null);
        Assert.Equal($"\"{NordExe}\" -a --hidden", compose($"\"{NordExe}\" -a"));
        Assert.Equal("\"C:\\Program Files\\Other\\o.exe\" -a", compose("\"C:\\Program Files\\Other\\o.exe\" -a"));
    }

    [Theory]
    [InlineData("bad\r\n\"C:\\evil.exe\"")]
    [InlineData("throw")]
    [InlineData("slow")]
    [InlineData("null")]
    public void A_startup_adapter_that_misbehaves_changes_nothing(string mode)
    {
        var host = Host();
        host.AddStartupAdapter("casehud", NordExe, a =>
        {
            switch (mode)
            {
                case "throw":
                    throw new InvalidOperationException("boom");
                case "slow":
                    Thread.Sleep(PluginHostServices.AdaptTimeout + TimeSpan.FromMilliseconds(500));
                    return a + " --late";
                case "null":
                    return null;
                default:
                    return mode;
            }
        });
        var line = $"\"{NordExe}\" -a";
        Assert.Equal(line, host.ComposeStartup(null)(line));
    }

    [Fact]
    public void A_restricted_program_is_told_by_its_image_not_its_name()
    {
        var host = Host();
        Assert.False(host.IsRestrictedProcess(42));
        Assert.False(host.Restrict("casehud", @"D:\Games\NordVPN.exe"));
        Assert.True(host.Restrict("casehud", NordExe));
        host.ProcessImage = pid => pid == 42 ? NordExe : pid == 7 ? @"D:\NordVPN.exe" : throw new InvalidOperationException("gone");
        Assert.True(host.IsRestrictedProcess(42));
        Assert.False(host.IsRestrictedProcess(7));
        Assert.False(host.IsRestrictedProcess(99));
    }

    [Fact]
    public void A_restricted_plugin_program_is_minimized_on_the_locked_couch_only()
    {
        var locked = new ForegroundContext(true, GameRunning: false, MouseMode: false, PcMode: false, Browsing: false, LockPrompt: false, Locked: true);
        var w = new LockedWindow("renamed", "", false, false, () => false, () => false, () => false) { PluginRestricted = () => true };
        Assert.Equal(ForegroundAction.Minimize, LockedForeground.Decide(locked, w).Action);
        Assert.False(LockedForeground.Decide(locked with { Locked = false }, w).Suppress);
    }

    [Fact]
    public void The_tunnel_with_a_gateway_carries_the_default_route()
    {
        var picked = PluginNetworkState.Pick([
            new("1", "Ethernet", "Intel", Up: true, Tunnel: false, HasGateway: true),
            new("2", "NordLynx", "NordLynx Tunnel", Up: true, Tunnel: true, HasGateway: true),
            new("3", "Down", "TAP-Windows", Up: false, Tunnel: true, HasGateway: true)
        ]);
        Assert.Equal("2", Assert.Single(picked, a => a.HasDefaultRoute).Id);
        var plain = PluginNetworkState.Pick([new("1", "Ethernet", "Intel", true, false, true), new("3", "Down", "TAP-Windows", false, true, true)]);
        Assert.Equal("1", Assert.Single(plain, a => a.HasDefaultRoute).Id);
        Assert.True(PluginNetworkState.IsTunnel(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "WireGuard Tunnel"));
        Assert.False(PluginNetworkState.IsTunnel(System.Net.NetworkInformation.NetworkInterfaceType.Ethernet, "Intel I225"));
    }

    // =====================================================================
    // Uninstall

    [Fact]
    public void An_uninstalled_plugin_is_forgotten_so_its_folder_never_loads_again()
    {
        var dir = PluginDir("tool");
        new PluginTrust(_dir).Trust("tool", dir);
        using var http = new HttpClient();
        Assert.True(new CatalogService(http, _dir, () => [], () => false).UninstallPlugin("tool"));
        Assert.False(Directory.Exists(dir));
        Assert.Null(new PluginTrust(_dir).Entry("tool"));
        Assert.Throws<ArgumentException>(() => new CatalogService(http, _dir, () => [], () => false).UninstallPlugin(".."));
        Assert.Throws<ArgumentException>(() => new CatalogService(http, _dir, () => [], () => false).UninstallPlugin(@"..\..\Windows"));
    }
}

/// <summary>Never called: gives the lint test above something to find in this assembly's metadata.</summary>
internal static class LintBait
{
#pragma warning disable CA1416
    internal static object?[] Uses() =>
    [
        System.Diagnostics.Process.GetCurrentProcess(),
        Microsoft.Win32.Registry.CurrentUser,
        new HttpClient(),
        System.Reflection.Assembly.Load("nothing")
    ];
#pragma warning restore CA1416
}
