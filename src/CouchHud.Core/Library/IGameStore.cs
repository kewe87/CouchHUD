namespace CouchHud.Core.Library;

/// <summary>
/// A built-in game store or game folder scanner. With Gaming switched off it is not read (see Config.FeatureAreas.ReadsStore), unless
/// parental controls need it to see games started outside CouchHud or one of its entries was marked as an app by the user.
/// </summary>
public interface IGameStore
{
    /// <summary>The source of its entries (LibraryItem.Source); their ids start with it and a colon.</summary>
    string Source { get; }
}
