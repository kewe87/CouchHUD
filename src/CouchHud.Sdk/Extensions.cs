using System.Text.Json;

namespace CouchHud.Sdk;

// SDK v1: plugins describe their UI as data. CouchHud renders it in the current
// skin, so every plugin screen looks right and works with the controller.

/// <summary>One row on a plugin page.</summary>
public sealed record PluginRow(string Id, string Label)
{
    /// <summary>Second line under the label.</summary>
    public string? Detail { get; init; }

    /// <summary>A short symbol or emoji on the left.</summary>
    public string? Icon { get; init; }

    /// <summary>Value shown on the right, e.g. "An", "42 %", "Leise".</summary>
    public string? Value { get; init; }

    /// <summary>Shows <see cref="Value"/> as active (accent color).</summary>
    public bool On { get; init; }

    // ---- SDK 3 ----

    /// <summary>The <see cref="PluginSection.Id"/> this row belongs to; null: the first section (or the plain list).</summary>
    public string? Section { get; init; }

    /// <summary>A PNG (at most <see cref="MaxImageBytes"/>), shown as an image, never as markup. Anything else is dropped by the host.</summary>
    public byte[]? Image { get; init; }

    public const int MaxImageBytes = 256 * 1024;

    /// <summary>What a press does; null is <see cref="PluginRowKind.Action"/>.</summary>
    public PluginRowKind? Kind { get; init; }

    /// <summary>A short mark on the right ("Neu", "3").</summary>
    public string? Badge { get; init; }

    /// <summary>A press changes something, so it needs the unlocked couch. Ignored for <see cref="PluginRowKind.Link"/>. The host may only make it stricter.</summary>
    public bool RequiresUnlock { get; init; } = true;

    /// <summary>
    /// A press needs the parents' code under parental controls (on top of the unlock). Enforced by the host, never by the plugin; for a
    /// plugin that is not CouchHUD's own it is on for every row that changes something, whatever the plugin says.
    /// </summary>
    public bool RequiresParentGrant { get; init; }

    /// <summary>
    /// Shown, and for CouchHUD's own plugins also usable, on the locked couch. For any other plugin the row is shown read-only on the
    /// locked couch (a press is refused).
    /// </summary>
    public bool AllowedWhileLocked { get; init; }

    /// <summary>
    /// The id of the <see cref="PluginFunction"/> this belongs to (declared with <see cref="IPluginContext.RegisterFunctions"/>). The host
    /// enforces the level the owner chose for that function (admin only, restricted too, nobody). Null: untagged, the flags above apply as before.
    /// A row without one belongs to its page's function.
    /// </summary>
    public string? Function { get; init; }
}

/// <summary>A full-screen list page, reachable from the plugin's entry in the settings.</summary>
public sealed class PluginPage
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public string Icon { get; init; } = "◆";

    /// <summary>Called every time the page opens and after each activation.</summary>
    public required Func<CancellationToken, Task<IReadOnlyList<PluginRow>>> Rows { get; init; }

    /// <summary>A pressed on a row. Return a short message for a toast, or null.</summary>
    public Func<string, CancellationToken, Task<string?>>? Activate { get; init; }

    // ---- SDK 3 ----

    /// <summary>Sections of the page in order; empty: one plain list.</summary>
    public IReadOnlyList<PluginSection> Sections { get; init; } = [];

    /// <summary>A stepper or choice row got a new value (row id, value). Return a short message for a toast, or null.</summary>
    public Func<string, string, CancellationToken, Task<string?>>? Change { get; init; }

    /// <summary>A short mark on the plugin's entry in the settings ("Update"), asked when the settings open.</summary>
    public Func<string?>? SettingsBadge { get; init; }

    /// <summary>
    /// The page may be opened on the locked couch; then only its rows with <see cref="PluginRow.AllowedWhileLocked"/> are shown (read-only
    /// for plugins that are not CouchHUD's own).
    /// </summary>
    public bool AllowedWhileLocked { get; init; }

    /// <summary>
    /// The id of the <see cref="PluginFunction"/> this belongs to (declared with <see cref="IPluginContext.RegisterFunctions"/>). The host
    /// enforces the level the owner chose for that function (admin only, restricted too, nobody). Null: untagged, the flags above apply as before.
    /// The page and all of its rows without an own function.
    /// </summary>
    public string? Function { get; init; }
}

/// <summary>An entry in the guide menu, e.g. "Licht aus" or "Lüfter leise".</summary>
public sealed class QuickAction
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public string Icon { get; init; } = "◆";

    /// <summary>Second line, asked for each time the guide menu opens.</summary>
    public Func<string?>? Detail { get; init; }

    /// <summary>Return a short message for a toast, or null.</summary>
    public required Func<CancellationToken, Task<string?>> Run { get; init; }

    /// <summary>SDK 3: needs the parents' code under parental controls. The host enforces it (and forces it for plugins that are not CouchHUD's own).</summary>
    public bool RequiresParentGrant { get; init; }

    /// <summary>
    /// SDK 3: may run on the locked couch (and is shown there). Honoured only for CouchHUD's own plugins; for any other plugin the entry is
    /// hidden while locked and a run is refused, whatever it says. Leave it off for anything that changes CouchHUD's own settings.
    /// </summary>
    public bool AllowedWhileLocked { get; init; }

    /// <summary>
    /// The id of the <see cref="PluginFunction"/> this belongs to (declared with <see cref="IPluginContext.RegisterFunctions"/>). The host
    /// enforces the level the owner chose for that function (admin only, restricted too, nobody). Null: untagged, the flags above apply as before.
    ///
    /// </summary>
    public string? Function { get; init; }
}

public enum SettingKind
{
    Toggle,
    Text,
    /// <summary>Like text, but never shown again (tokens, passwords).</summary>
    Secret,
    Choice
}

/// <summary>A setting on the plugin's page in the settings. Values are stored by CouchHud.</summary>
public sealed class PluginSetting
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public string? Description { get; init; }
    public SettingKind Kind { get; init; } = SettingKind.Toggle;

    /// <summary>"true"/"false" for toggles, one of <see cref="Choices"/> for a choice.</summary>
    public string? Default { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>
    /// The id of the <see cref="PluginFunction"/> that covers changing this setting. Null: the plugin's function called "settings" when it declared
    /// one, else admin only. The host enforces the owner's level for it.
    /// </summary>
    public string? Function { get; init; }

    /// <summary>For a <see cref="SettingKind.Text"/> setting: with a minimum and/or maximum the value must be a number inside the range. Null: no limit.</summary>
    public double? Min { get; init; }

    public double? Max { get; init; }
}

/// <summary>
/// An operation for the AI assistant and MCP clients. Tools that change something
/// (<see cref="Writes"/>) are confirmed by the user on the TV before they run.
/// </summary>
public sealed class PluginTool
{
    /// <summary>snake_case; CouchHud prefixes it with the plugin id.</summary>
    public required string Name { get; init; }

    /// <summary>English works best for the models.</summary>
    public required string Description { get; init; }

    /// <summary>JSON schema of the arguments, e.g. {"type":"object","properties":{}}.</summary>
    public string InputSchema { get; init; } = """{"type":"object","properties":{}}""";

    public bool Writes { get; init; }

    /// <summary>SDK 3: may run on the locked couch. Honoured only for CouchHUD's own plugins (the TV still asks first when it writes).</summary>
    public bool AllowedWhileLocked { get; init; }

    /// <summary>
    /// The id of the <see cref="PluginFunction"/> this belongs to (declared with <see cref="IPluginContext.RegisterFunctions"/>). The host
    /// enforces the level the owner chose for that function (admin only, restricted too, nobody). Null: untagged, the flags above apply as before.
    ///
    /// </summary>
    public string? Function { get; init; }

    /// <summary>Returns text (often JSON) for the model.</summary>
    public required Func<JsonElement, CancellationToken, Task<string>> Run { get; init; }
}

/// <summary>A game as plugins see it in events.</summary>
public sealed record GameEvent(string Id, string Name, string? InstallDir, double Minutes)
{
    /// <summary>SDK 3: the game's main process id, when known.</summary>
    public int? ProcessId { get; init; }
}

public interface IPluginEvents
{
    event Action<GameEvent>? GameStarted;

    /// <summary><see cref="GameEvent.Minutes"/> is the length of this session.</summary>
    event Action<GameEvent>? GameEnded;

    /// <summary>The user switched to the Windows desktop.</summary>
    event Action? PcModeEntered;

    /// <summary>Back on the couch from PC mode.</summary>
    event Action? CouchModeEntered;

    /// <summary>
    /// The user switched the language ("de" or "en"). Register settings, pages and quick actions again with the same
    /// ids: a registration replaces the earlier one with that id. Texts built on demand (rows, details) just ask
    /// <see cref="IPluginContext.Language"/> each time.
    /// </summary>
    event Action<string>? LanguageChanged
    {
        add { }
        remove { }
    }

    /// <summary>SDK 2: the couch was locked (true) or unlocked (false). <see cref="IPluginContext.Locked"/> has the current state.</summary>
    event Action<bool>? LockChanged
    {
        add { }
        remove { }
    }
}

/// <summary>Values of the plugin's <see cref="PluginSetting"/>s and anything else it wants to keep.</summary>
public interface IPluginSettings
{
    string? Get(string key);

    bool GetBool(string key, bool fallback = false);

    void Set(string key, string? value);

    /// <summary>
    /// SDK 3: like <see cref="Set"/>, but the host keeps the value encrypted for the Windows user (a token, a password) and never shows it
    /// again; <see cref="Get"/> returns the plain value to the plugin that stored it, nobody else. Older hosts store it as given (the default
    /// implementation), so a plugin should not rely on it for more than they promise.
    /// </summary>
    void SetSecret(string key, string? value) => Set(key, value);
}
