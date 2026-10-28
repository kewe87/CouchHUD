using CouchHud.Core.Plugins;

namespace CouchHud.Core.Tests;

/// <summary>Checks for an entry of the repository's catalog that carries CouchHUD's own signed package statement.</summary>
internal static class SignedCatalogEntry
{
    /// <summary>
    /// The statement verifies first-party against the key compiled into CouchHUD, names exactly this package (id, folder, version, hash)
    /// and an icon, so the catalog can install the plugin and show its icon.
    /// </summary>
    public static PackageStatement AssertOwnSigned(CatalogEntry entry)
    {
        Assert.NotNull(entry.Statement);
        Assert.NotNull(entry.Signature);
        Assert.Matches("^[0-9a-f]{64}$", entry.Sha256!);
        Assert.Null(entry.Problem);
        var verified = PackageStatements.Verify(new SignedStatement(Convert.FromBase64String(entry.Statement!), entry.Signature, null));
        Assert.Equal(PluginOrigin.FirstParty, verified.Origin);
        PackageStatements.CheckMatches(verified, entry.Sha256!, entry);
        Assert.Equal(entry.Id, verified.Statement.Id);
        Assert.NotNull(verified.Statement.Icon);
        return verified.Statement;
    }
}
