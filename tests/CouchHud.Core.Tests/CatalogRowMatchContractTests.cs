namespace CouchHud.Core.Tests;

/// <summary>
/// The page matches an installed plugin to a catalog row by the row's folder and tells a row of another source by its flag
/// (ui/core/plugin-rules.js isEntryOf / entryActions). CatalogManager.Describe builds the row; it needs the whole host to run, so this pins the source.
/// </summary>
public class CatalogRowMatchContractTests
{
    private static string Manager()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "CouchHud.slnx"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!, "src", "CouchHud.Shell", "Services", "CatalogManager.cs"));
    }

    [Fact]
    public void RowCarriesTheEntrysFolderAndTheOtherSourceFlag()
    {
        var source = Manager();
        Assert.Contains("public string? Folder { get; init; }", source);
        Assert.Contains("public bool OtherSource { get; init; }", source);
        var describe = source[source.IndexOf("private CatalogState Describe(", StringComparison.Ordinal)..];
        Assert.Contains("Folder = e.Folder,", describe);
        Assert.Contains("OtherSource = originProblem is not null,", describe);
    }
}
