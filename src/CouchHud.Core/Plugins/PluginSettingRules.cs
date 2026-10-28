using System.Globalization;
using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>
/// Changing a plugin's setting from the HUD's settings page as pure decisions: which function covers the setting, whether the owner's level
/// for it allows the change now, and whether the value fits what the plugin declared. The same function levels as pages, rows and tabs
/// (<see cref="PluginFunctionRules"/>); the plugin is never asked.
/// </summary>
public static class PluginSettingRules
{
    /// <summary>The function a plugin declares for its settings when a setting names none of its own.</summary>
    public const string SettingsFunctionId = "settings";

    /// <summary>
    /// The core's own settings page of a plugin counts as one page for visits. A plugin page id can never start with "$" (RegisterPage refuses
    /// it, FindPage never resolves it). One visit under this id covers all admin settings of that plugin, across functions, while it lasts:
    /// the settings page is one page.
    /// </summary>
    public const string SettingsPageId = "$settings";

    /// <summary>A page id a plugin may not use: anything starting with "$" is the core's (or empty).</summary>
    public static bool IsReservedPageId(string? id) => string.IsNullOrEmpty(id) || id[0] == '$';

    public const string NeedVisit = "plugin.setting.needVisit";

    public const int MaxText = 500;
    public const int MaxSecret = 4096;

    /// <summary>
    /// The function id that covers the setting: its own tag, else the plugin's function called "settings" when it declared one, else null
    /// (treated as admin only). <paramref name="declared"/> answers whether the plugin declared a function of that id.
    /// </summary>
    public static string? FunctionOf(PluginSetting setting, Func<string, bool> declared)
    {
        if (!string.IsNullOrEmpty(setting.Function)) return setting.Function;
        return declared(SettingsFunctionId) ? SettingsFunctionId : null;
    }

    /// <summary>
    /// Why the change is refused now (a text key); null when it may go ahead. No function (or one the plugin never declared, which
    /// <c>PluginManager.LevelOf</c> turns into admin) counts as admin only: not on the locked couch; in a child's profile only while a visit
    /// of a page of that function is on (the parents' code opened it, at most five minutes, bound to the profile). Restricted is for everyone,
    /// nobody is nobody.
    /// </summary>
    public static string? Refusal(PluginLevel? level, bool locked, bool childProfile, bool visitActive)
    {
        var access = PluginFunctionRules.Access(level ?? PluginLevel.Admin, locked, childProfile);
        if (access.Refused is { } why) return why;
        return access.ParentCode && !visitActive ? NeedVisit : null;
    }

    /// <summary>
    /// The value in the form that is stored, or null when it does not fit the declared kind: a toggle takes "true" or "false", a choice one of its
    /// choices, a text with a range (<see cref="PluginSetting.Min"/>/<see cref="PluginSetting.Max"/>) a finite number inside it, other text and
    /// secrets are bounded in length with no control characters (plain text or a secret may be cleared with null or empty).
    /// </summary>
    public static string? Check(PluginSetting setting, string? value)
    {
        switch (setting.Kind)
        {
            case SettingKind.Toggle:
                return value is "true" or "false" ? value : null;
            case SettingKind.Choice:
                return value is not null && setting.Choices.Contains(value, StringComparer.Ordinal) ? value : null;
            case SettingKind.Secret:
                return Text(value, MaxSecret);
            default:
                if (setting.Min is null && setting.Max is null) return Text(value, MaxText);
                if (value is null || value.Length > 40 || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) return null;
                if (number < (setting.Min ?? double.NegativeInfinity) || number > (setting.Max ?? double.PositiveInfinity)) return null;
                return number.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string? Text(string? value, int max)
    {
        if (value is null) return "";
        if (value.Length > max || value.Any(char.IsControl)) return null;
        return value;
    }
}
