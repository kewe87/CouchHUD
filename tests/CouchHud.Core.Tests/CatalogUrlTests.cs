using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

public class CatalogUrlTests
{
    private const string Official = CatalogDefaults.IndexUrl;

    [Theory]
    [InlineData("HTTPS://RAW.GITHUBUSERCONTENT.COM/KEWE87/COUCHHUD-CATALOG/main/index.json")]
    [InlineData("https://raw.githubusercontent.com/kewe87/couchhud-catalog/main/index.json")]
    public void Scheme_host_and_owner_repo_ignore_case(string url)
    {
        Assert.True(CatalogUrl.Same(url, Official));
        Assert.True(CatalogService.IsDefaultSource(url));
        Assert.True(CatalogSourceRules.IsOfficial(url));
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/MAIN/index.json")]            // branch case: another path
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/INDEX.JSON")]            // file case
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json?x=1")]        // query
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json#a")]          // fragment
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json/")]           // a file name with a slash is no file
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json//")]
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalogue/main/index.json")]          // look-alike repository
    [InlineData("https://raw.githubusercontent.com/kewe87x/CouchHUD-Catalog/main/index.json")]          // look-alike owner
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog%2F/main/index.json")]         // escapes in the repository
    [InlineData("https://raw.githubusercontent.com/%6Bewe87/CouchHUD-Catalog/main/index.json")]          // escaped owner
    [InlineData("https://raw.githubusercontent.com./kewe87/CouchHUD-Catalog/main/index.json")]           // trailing dot in the host
    [InlineData("https://raw.githubusercontent.com.evil.example/kewe87/CouchHUD-Catalog/main/index.json")]
    [InlineData("https://raw.githubusercontent.com@evil.example/kewe87/CouchHUD-Catalog/main/index.json")]
    [InlineData("https://user@raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json")]
    [InlineData("https://raw.githubusercontent.com:8443/kewe87/CouchHUD-Catalog/main/index.json")]
    [InlineData("http://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json")]
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog")]
    [InlineData("https://raw.githubusercontent.com/kewe87")]
    [InlineData("")]
    public void Look_alikes_are_not_the_official_address(string url)
    {
        Assert.False(CatalogUrl.Same(url, Official));
        Assert.False(CatalogService.IsDefaultSource(url));
        Assert.False(CatalogSourceRules.IsOfficial(url));
    }

    [Fact]
    public void Owner_and_repo_are_only_folded_on_the_GitHub_hosts_and_the_path_after_them_stays_exact()
    {
        Assert.False(CatalogUrl.Same("https://example.org/Owner/Repo/a.json", "https://example.org/owner/repo/a.json"));
        Assert.True(CatalogUrl.Same("https://EXAMPLE.org/Owner/Repo/a.json", "https://example.org/Owner/Repo/a.json"));
        Assert.False(CatalogUrl.Same("https://github.com/kewe87/CouchHUD-Catalog/releases/download/Wand-1.0.0/a.zip", "https://github.com/kewe87/couchhud-catalog/releases/download/wand-1.0.0/a.zip"));
        Assert.True(CatalogUrl.Same("https://github.com/kewe87/CouchHUD-Catalog/releases/download/wand-1.0.0/a.zip", "https://github.com/kewe87/couchhud-catalog/releases/download/wand-1.0.0/a.zip"));
    }

    [Fact]
    public void A_trailing_slash_is_ignored_after_a_folder_only()
    {
        Assert.True(CatalogUrl.Same("https://example.org/a/b/", "https://example.org/a/b"));
        Assert.False(CatalogUrl.Same("https://example.org/a/index.json/", "https://example.org/a/index.json"));
    }

    [Fact]
    public void A_developer_list_entry_that_only_looks_like_the_official_one_is_not_a_second_official_or_an_unsigned_developer_source()
    {
        // Same address in another spelling: the one official source, not twice. Different path: its own address, never "default".
        Assert.True(CatalogUrl.Same("https://RAW.githubusercontent.com/KEWE87/couchhud-catalog/main/index.json", Official));
        Assert.False(CatalogService.IsDefaultSource("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/index.json/"));
    }

    [Fact]
    public void Developer_addresses_are_the_same_only_when_written_alike()
    {
        Assert.True(CatalogUrl.Same("http://127.0.0.1:8080/index.json", "http://127.0.0.1:8080/index.json/"));
        Assert.False(CatalogUrl.Same("http://127.0.0.1:8080/index.json", "http://127.0.0.1:8080/Index.json"));
        Assert.False(CatalogUrl.Same("http://127.0.0.1:8080/index.json", Official));
    }

    [Fact]
    public void A_mixed_case_official_address_cannot_be_added_as_a_foreign_source()
    {
        var problem = CatalogSourceRules.CheckNew("https://RAW.githubusercontent.com/KEWE87/couchhud-catalog/main/index.json", null, null, []);
        Assert.Equal("catalog.source.isOfficial", problem);
        // A different branch of the same repository is another address: it is a foreign (unsigned, never first-party) source.
        Assert.False(CatalogService.IsDefaultSource("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/dev/index.json"));
    }

    [Theory]
    [InlineData("https://raw.githubusercontent.com/kewe87/CouchHUD-Catalog/main/icons/a.png", true)]
    [InlineData("https://github.com/kewe87/CouchHUD-Catalog/releases/download/a-1.0.0/a-1.0.0.zip", true)]
    [InlineData("https://github.com/kewe87/CouchHUD/tree/master/plugins/X", true)]
    [InlineData("https://raw.githubusercontent.com/kewe87/couchhud-catalog/main/icons/a.png", false)]
    [InlineData("https://github.com/kewe87/couchhud/tree/master/plugins/X", false)]
    [InlineData("https://GitHub.com/kewe87/CouchHUD-Catalog/releases/download/a-1.0.0/a.zip", false)]
    [InlineData("HTTPS://github.com/kewe87/CouchHUD-Catalog/releases/download/a-1.0.0/a.zip", false)]
    [InlineData("https://example.org/Anything/Goes", true)]
    public void New_catalog_entries_must_use_the_canonical_spelling(string url, bool canonical) =>
        Assert.Equal(canonical, CatalogUrl.NonCanonicalReason(url) is null);
}
