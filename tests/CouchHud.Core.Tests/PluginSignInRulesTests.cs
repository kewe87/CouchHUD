using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>The state parameter of a plugin's sign-in address: behaviour, not text matching.</summary>
public sealed class PluginSignInRulesTests
{
    [Theory]
    [InlineData("https://ha.example/auth?state=abc123", "abc123")]
    [InlineData("https://ha.example/auth?client=x&state=A-b.c_d~9&z=1", "A-b.c_d~9")]
    [InlineData("https://ha.example/auth?state=ab%2Dcd", "ab-cd")]
    [InlineData("https://ha.example/auth?state=%61bc", "abc")]
    public void A_valid_state_is_returned(string url, string expected) => Assert.Equal(expected, PluginSignInRules.StateOf(url));

    [Theory]
    [InlineData("https://ha.example/auth?state=a&state=b")]
    [InlineData("https://ha.example/auth?state=a&x=1&state=a")]
    [InlineData("https://ha.example/auth")]
    [InlineData("https://ha.example/auth?")]
    [InlineData("https://ha.example/auth#state=abc")]
    [InlineData("https://ha.example/auth?x=1#state=abc")]
    [InlineData("https://ha.example/auth?state=")]
    [InlineData("https://ha.example/auth?state")]
    [InlineData("https://ha.example/auth?state=a%2Fb")]
    [InlineData("https://ha.example/auth?state=a%20b")]
    [InlineData("https://ha.example/auth?state=a%00b")]
    [InlineData("https://ha.example/auth?state=a+b")]
    [InlineData("https://ha.example/auth?state=%C3%A4")]
    [InlineData("https://ha.example/auth?state=a<b")]
    [InlineData("https://ha.example/auth?state=a'b")]
    [InlineData("https://ha.example/auth?State=abc")]
    [InlineData("/auth?state=abc")]
    [InlineData("")]
    [InlineData(null)]
    public void A_missing_duplicate_hidden_or_wrong_state_is_refused(string? url) => Assert.Null(PluginSignInRules.StateOf(url));

    [Fact]
    public void A_state_that_is_too_long_is_refused_and_the_longest_is_fine()
    {
        Assert.Equal(new string('a', 256), PluginSignInRules.StateOf("https://ha.example/a?state=" + new string('a', 256)));
        Assert.Null(PluginSignInRules.StateOf("https://ha.example/a?state=" + new string('a', 257)));
        // A percent-encoded state counts after decoding.
        Assert.Null(PluginSignInRules.StateOf("https://ha.example/a?state=" + string.Concat(Enumerable.Repeat("%61", 257))));
        // A query over 4096 characters is refused whatever it holds.
        Assert.Null(PluginSignInRules.StateOf("https://ha.example/a?state=ok&pad=" + new string('x', 4100)));
    }

    [Fact]
    public void An_escaped_name_counts_as_a_state_so_the_duplicate_is_refused()
    {
        Assert.Null(PluginSignInRules.StateOf("https://ha.example/a?%73tate=a&state=b"));
        Assert.Null(PluginSignInRules.StateOf("https://ha.example/a?state=a&st%61te=b"));
    }

    [Fact]
    public void A_semicolon_is_no_separator_and_the_state_with_it_is_refused()
    {
        Assert.Null(PluginSignInRules.StateOf("https://ha.example/a?state=a;state=b"));
        Assert.Null(PluginSignInRules.StateOf("https://ha.example/a?x=1;state=a"));
    }

    [Fact]
    public void A_state_with_another_case_of_the_name_is_another_parameter()
    {
        Assert.Equal("a", PluginSignInRules.StateOf("https://ha.example/a?state=a&State=b"));
    }

    [Fact]
    public void An_international_host_matches_its_punycode_twin()
    {
        var page = PluginSignInRules.ParsePage("https://b\u00fccher.example:8123/auth/authorize?state=abc");
        Assert.NotNull(page);
        Assert.True(PluginSignInRules.SameOrigin("https://xn--bcher-kva.example:8123/x", page!.Root));
        Assert.False(PluginSignInRules.SameOrigin("https://xn--bcher-kva.example:8124/x", page.Root));
    }

    [Fact]
    public void An_ipv6_literal_is_a_valid_origin()
    {
        var page = PluginSignInRules.ParsePage("http://[2001:db8::1]:8123/auth/authorize?state=abc");
        Assert.NotNull(page);
        Assert.Equal("http://[2001:db8::1]:8123", page!.Root);
        Assert.True(PluginSignInRules.SameOrigin("http://[2001:db8::1]:8123/x", page.Root));
        Assert.False(PluginSignInRules.SameOrigin("http://[2001:db8::2]:8123/x", page.Root));
    }

    [Theory]
    [InlineData("https://ha.example\\@evil.example/auth/authorize?state=abc")]
    [InlineData("https://ha.example\\evil.example/auth/authorize?state=abc")]
    public void A_backslash_trick_is_refused_or_the_root_is_exactly_the_host_the_parser_sees(string url)
    {
        var page = PluginSignInRules.ParsePage(url);
        if (page is not null) Assert.Equal(new Uri(url).GetLeftPart(UriPartial.Authority), page.Root);
        if (page is not null) Assert.Equal(0, new Uri(url).UserInfo.Length);
    }

    [Theory]
    [InlineData(" https://ha.example/auth/authorize?state=abc")]
    [InlineData("https://ha.example/auth/authorize?state=abc ")]
    [InlineData("\thttps://ha.example/auth/authorize?state=abc")]
    public void Surrounding_whitespace_is_refused(string url) => Assert.Null(PluginSignInRules.ParsePage(url));

    // The shape the Home Assistant plugin passes: base + /auth/authorize with its fixed parameters.
    private const string HaPage = "https://192.0.2.10:8123/auth/authorize?response_type=code&client_id=https%3A%2F%2Fhomeassistant.couchhud.invalid%2F"
        + "&redirect_uri=https%3A%2F%2Fhomeassistant.couchhud.invalid%2F%3Fauth_callback%3D1&state=Ab-9_x.Y~0123456789abcdef"
        + "&code_challenge=E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM&code_challenge_method=S256";

    [Fact]
    public void The_real_Home_Assistant_authorize_address_is_accepted_and_the_root_is_its_origin()
    {
        var page = PluginSignInRules.ParsePage(HaPage);
        Assert.NotNull(page);
        Assert.Equal("https://192.0.2.10:8123", page!.Root);
        Assert.Equal("Ab-9_x.Y~0123456789abcdef", page.State);
        Assert.True(PluginSignInRules.SameOrigin("https://192.0.2.10:8123/lovelace/0", page.Root));
        Assert.False(PluginSignInRules.SameOrigin("https://192.0.2.10:8124/", page.Root));
        Assert.False(PluginSignInRules.SameOrigin("http://192.0.2.10:8123/", page.Root));
        Assert.False(PluginSignInRules.SameOrigin("https://192.0.2.11:8123/", page.Root));
    }

    [Fact]
    public void A_base_path_is_kept_out_of_the_root_and_a_default_port_is_dropped()
    {
        Assert.Equal("http://ha.local", PluginSignInRules.ParsePage("http://ha.local:80/ha/auth/authorize?state=abc")!.Root);
    }

    [Theory]
    [InlineData("https://user:pw@ha.example/auth/authorize?state=abc")]
    [InlineData("https://user@ha.example/auth/authorize?state=abc")]
    [InlineData("https://ha.example/auth/authorize?state=abc#frag")]
    [InlineData("https://ha.example/auth/authorize?state=abc#")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://ha.example/auth/authorize?state=abc")]
    [InlineData("file:///c:/x?state=abc")]
    [InlineData("https://ha.example/auth/authorize?response_type=code")]
    [InlineData("https://ha.example/auth/authorize?state=a&state=b")]
    [InlineData("https://ha.example/auth/authorize?state=abc\u0001")]
    [InlineData("https://ha.example/auth/authorize?state=abc\n")]
    [InlineData("/auth/authorize?state=abc")]
    [InlineData("")]
    [InlineData(null)]
    public void A_page_that_is_not_a_clean_http_address_with_one_state_is_refused(string? url) => Assert.Null(PluginSignInRules.ParsePage(url));

    [Fact]
    public void A_page_address_over_2048_characters_is_refused()
    {
        var url = "https://ha.example/a?state=abc&pad=";
        Assert.NotNull(PluginSignInRules.ParsePage(url + new string('x', 2048 - url.Length)));
        Assert.Null(PluginSignInRules.ParsePage(url + new string('x', 2049 - url.Length)));
    }
}
