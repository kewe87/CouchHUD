namespace CouchHud.Core.Guard;

/// <summary>
/// The action needs a fresh grant of the parents' code (or, without a parents' code, the unlock) for exactly this action and item. The bridge
/// tells the page which grant to ask for ("parent": action and item); the page asks, then repeats the call, and the host checks the grant again
/// and uses it up. A normal refusal for everything that still derives from <see cref="InvalidOperationException"/>.
/// </summary>
public sealed class ParentCodeNeededException(string message, string action, string? item) : InvalidOperationException(message)
{
    public string Action { get; } = action;

    public string? Item { get; } = item;

    /// <summary>Why, in the person's language, for the code prompt (the plugin and the function); null: the page uses its own text.</summary>
    public string? Reason { get; init; }
}
