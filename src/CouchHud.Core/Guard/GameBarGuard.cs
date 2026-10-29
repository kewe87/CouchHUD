using CouchHud.Core.Platform;

namespace CouchHud.Core.Guard;

/// <summary>
/// The Xbox button belongs to CouchHud on the couch: the Game Bar's claim on it is switched off for
/// as long as CouchHud is in charge (through <see cref="GuardSnapshot"/>, so the original comes back).
/// Capture settings (AppCaptureEnabled, GameDVR_Enabled) are deliberately not touched, except on a locked couch,
/// where <see cref="CaptureOff"/> also takes the keyboard shortcut (Win+G) away.
/// </summary>
public static class GameBarGuard
{
    public const string Key = @"HKCU\Software\Microsoft\GameBar";
    public const string Value = "UseNexusForGameBarEnabled";

    public static readonly GuardChange Change = new(GuardKind.RegistryDword, Key, Value, "0");

    public const string CaptureKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR";

    /// <summary>Locked couch only (part of <see cref="LockHardening"/>): the Game Bar shortcut does nothing, captures through it stop.</summary>
    public static readonly GuardChange CaptureOff = new(GuardKind.RegistryDword, CaptureKey, "AppCaptureEnabled", "0");

    /// <summary>
    /// Older versions set the value to 0 without a backup. Once, on upgrade: if there is no snapshot, the value is 0 and
    /// the config shows a version that did it (used before, guide tap on), the original was "missing".
    /// </summary>
    public static bool NeedsMigration(bool alreadyMigrated, bool snapshotPending, bool valueIsZero, bool usedBefore, bool guideTapInGame) =>
        !alreadyMigrated && !snapshotPending && valueIsZero && usedBefore && guideTapInGame;

    /// <summary>Once, on upgrade: a raw 0 without a snapshot, tap mode off, used before: the "hold" choice of an older version.</summary>
    public static bool NeedsKeptOffMigration(bool snapshotPending, bool valueIsZero, bool usedBefore, bool guideTapInGame, bool keptOff) =>
        !snapshotPending && valueIsZero && usedBefore && !guideTapInGame && !keptOff;

    /// <summary>
    /// Everywhere CouchHud is in charge of the couch (app mode and shell mode alike), outside PC mode, with the option on and the
    /// guide tap in use; a locked couch (shell mode only) takes the button whatever the options say. The change is per user,
    /// backed up first and put back on quit, in PC mode, with the option off and after a crash.
    /// </summary>
    public static bool ShouldApply(bool full, bool pcMode, bool suppressCompetitors, bool guideTapInGame, bool locked = false, bool keptOff = false) =>
        !pcMode && (full && locked || suppressCompetitors && guideTapInGame || keptOff);

    // ---- Safety net: a Run entry of the user, there while the snapshot holds the Game Bar value --------------------------------------

    public const string RunKey = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunValue = "CouchHudReleaseGameBar";
    public const string ReleaseSwitch = "--release-game-bar";
    public const string StateFile = "guard.json";

    /// <summary>
    /// What the Run entry (and nothing else) may restore from the user's state file: exactly the Game Bar button value, back to missing or a
    /// plain number. A changed file can name no other key, no string, no VDF block.
    /// </summary>
    public static bool AcceptsRestore(GuardEntry entry) =>
        entry.Kind == GuardKind.RegistryDword
        && entry.Location.Equals(Key, StringComparison.OrdinalIgnoreCase)
        && entry.Name.Equals(Value, StringComparison.OrdinalIgnoreCase)
        && (entry.Original is null || entry.Original is "0" or "1");

    /// <summary>The entry follows the snapshot: written while it holds something, gone once everything is back. Fixed command, no arguments but the switch.</summary>
    public static void SyncRunEntry(IRegistry registry, bool pending, string? exePath)
    {
        if (pending)
        {
            // A development run (dotnet.exe host) must never leave an entry that starts dotnet.
            if (!string.IsNullOrEmpty(exePath) && !Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) registry.WriteString(RunKey, RunValue, $"\"{exePath}\" {ReleaseSwitch}");
        }
        else registry.DeleteValue(RunKey, RunValue);
    }

    /// <summary>For the Run entry's short process (only when no CouchHud runs): puts the value back and drops the entry once it holds.</summary>
    public static IReadOnlyList<string> ReleaseFromRunEntry(IRegistry registry, string dataDir, Action<string>? log = null)
    {
        var snapshot = new GuardSnapshot(registry, new FileVdfStore(), Path.Combine(dataDir, StateFile), log, AcceptsRestore);
        var errors = snapshot.Restore();
        SyncRunEntry(registry, snapshot.HasPending, null);
        return errors;
    }
}
