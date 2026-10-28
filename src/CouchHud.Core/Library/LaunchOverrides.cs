using CouchHud.Core.Config;
using CouchHud.Core.Files;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;
using CouchHud.Sdk;

namespace CouchHud.Core.Library;

/// <summary>What the checks need to know about the disk; <see cref="Real"/> asks Windows, tests pass their own.</summary>
/// <param name="RedirectBelow">A junction, mount point or link on the way from the install folder (root) down to the path
/// (<see cref="Saves.SavePathGuard.HasReparsePointBelow"/>); null: that one.</param>
/// <param name="Fingerprint">Size and SHA-256 of a file, null when it cannot be read; null: <see cref="FileFingerprint.Read"/>.</param>
public sealed record LaunchFileSystem(
    Func<string, bool> FileExists,
    Func<string, bool> DirectoryExists,
    Func<char, bool> IsNetworkDrive,
    Func<string, bool> IsRedirect,
    string WindowsDir,
    Func<string, string, bool>? RedirectBelow = null,
    Func<string, FileFingerprint?>? Fingerprint = null)
{
    public static LaunchFileSystem Real => new(File.Exists, Directory.Exists, LocalPaths.IsNetworkDrive, Saves.SavePathGuard.IsRedirect,
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), (root, path) => Saves.SavePathGuard.HasReparsePointBelow(root, path), FileFingerprint.Read);

    public bool HasRedirectBelow(string root, string path) =>
        RedirectBelow is { } below ? below(root, path) : Saves.SavePathGuard.HasReparsePointBelow(root, path);

    public FileFingerprint? FingerprintOf(string path) => (Fingerprint ?? FileFingerprint.Read)(path);
}

/// <summary>Size and SHA-256 (lower-case hex) of a file's content: what the approval of a batch file as a start target covers.</summary>
public readonly record struct FileFingerprint(long Size, string Sha256)
{
    /// <summary>
    /// The largest batch file that is pinned (1 MB). A start script is a few lines; a bigger one is not hashed (that would hold up every start),
    /// so it is neither approved nor started as an own start target (<see cref="TooLarge"/>).
    /// </summary>
    public const long MaxScriptBytes = 1024 * 1024;

    /// <summary>A file over <see cref="MaxScriptBytes"/>: its fingerprint has only the size, no hash, and matches no pin.</summary>
    public bool TooLarge => Size > MaxScriptBytes;

    /// <summary>The fingerprint of an open stream from its start (the caller keeps it open, so what was hashed is what runs).</summary>
    public static FileFingerprint Of(Stream stream)
    {
        if (stream.Length > MaxScriptBytes) return new FileFingerprint(stream.Length, "");
        stream.Position = 0;
        var hash = System.Security.Cryptography.SHA256.HashData(stream);
        var size = stream.Position;
        stream.Position = 0;
        return new FileFingerprint(size, Convert.ToHexStringLower(hash));
    }

    /// <summary>The fingerprint of a file (read with writers shut out meanwhile); null when it cannot be read.</summary>
    public static FileFingerprint? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Of(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The fingerprint an override pinned, null when none is stored (or a hand-edited one is no SHA-256).</summary>
    public static FileFingerprint? Pinned(ItemOverride? o) =>
        o?.LaunchTargetSha256 is { Length: 64 } hash && hash.All(char.IsAsciiHexDigit) && o.LaunchTargetSize is { } size and >= 0 and <= MaxScriptBytes
            ? new FileFingerprint(size, hash.ToLowerInvariant())
            : null;
}

/// <summary>One change of the launch override. A field with Set false stays as it is; Set with a null or empty value goes back to automatic.</summary>
public sealed record LaunchOverrideChange
{
    public bool SetArgs { get; init; }
    public string? Args { get; init; }
    public bool SetCwd { get; init; }
    public string? Cwd { get; init; }
    public bool SetTarget { get; init; }
    public string? Target { get; init; }
}

/// <summary>What the edit menu offers for an entry and what is set now.</summary>
/// <param name="ArgsMode">"own" (an entry the user added: its own fields), "override" (an exe start or the own start target),
/// "steam" (set in Steam's properties: steam:// carries no parameters reliably), "launcher" (the store's launcher starts it, no parameters), "none".</param>
/// <param name="TargetAllowed">An own start target is possible: the entry has an install folder CouchHUD watches, and no web page.</param>
/// <param name="Custom">An override is active (the detail page says so).</param>
public sealed record LaunchOptionsView(
    string ArgsMode,
    IReadOnlyList<string> Args,
    string? WorkingDirectory,
    string? ProgramFolder,
    bool TargetAllowed,
    string? Target,
    string? InstallFolder,
    bool Custom,
    string? SteamAppId);

/// <summary>
/// Startup parameters, working folder and an own start target for every entry, kept as an override (<see cref="ItemOverride"/>) so rescans
/// keep them. The entry stays the same entry: id, install folder and with them play time, the running game's detection and the parental
/// rules (<see cref="Parental.ParentalService.LimitKey"/>) do not change. That is why an own start target must lie inside the install
/// folder: whatever runs from there is seen by <see cref="GameSessionTracker"/> as this game.
/// </summary>
public static class LaunchOverrides
{
    public const int MaxPathLength = 260;

    private static readonly string[] TargetExtensions = [".exe", ".bat", ".cmd"];

    /// <summary>Windows' device names: as a file name they open a device, not a file.</summary>
    private static readonly HashSet<string> DeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string ArgsMode(LibraryItem item, ItemOverride? o)
    {
        if (item.Launch.Embedded) return "none";
        if (o?.LaunchTarget is not null) return "override";
        if (item.Launch.Kind == LaunchKind.Exe) return item.Source == "custom" ? "own" : "override";
        if (SteamStore.AppIdOf(item) is not null) return "steam";
        return item.Source == "custom" ? "none" : "launcher";
    }

    public static bool TargetAllowed(LibraryItem item) => !item.Launch.Embedded && GameSessionTracker.InstallRoot(item) is not null;

    public static LaunchOptionsView View(LibraryItem item, ItemOverride? o)
    {
        var mode = ArgsMode(item, o);
        var target = o?.LaunchTarget;
        var exe = target ?? (item.Launch.Kind == LaunchKind.Exe ? item.Launch.Target : null);
        IReadOnlyList<string> args = mode switch
        {
            "own" => item.Launch.Args,
            "override" => o?.LaunchArgs ?? (target is null ? item.Launch.Args : []),
            _ => []
        };
        var cwd = mode switch
        {
            "own" => item.Launch.WorkingDirectory,
            "override" => o?.LaunchCwd ?? (target is null ? item.Launch.WorkingDirectory : null),
            _ => null
        };
        return new LaunchOptionsView(mode, args, cwd, exe is null ? null : Path.GetDirectoryName(exe), TargetAllowed(item), target,
            GameSessionTracker.InstallRoot(item)?.TrimEnd('\\'), o?.HasLaunch ?? false, SteamStore.AppIdOf(item));
    }

    /// <summary>
    /// The start target the user picked, in its normal form: an .exe, .bat or .cmd on a local drive (no share, no device or NT prefix, no
    /// stream, no device name, no link to elsewhere), existing, inside the entry's install folder, and for a game no shell, tool or browser.
    /// </summary>
    public static string ValidateTarget(string? path, LibraryItem item, LaunchFileSystem fs)
    {
        var text = (path ?? "").Trim().Trim('"');
        if (text.Length == 0 || text.Length > MaxPathLength || text.Any(char.IsControl)) throw new ArgumentException(Strings.Get("entry.targetInvalid", text));
        string full;
        try
        {
            full = Path.GetFullPath(text);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException(Strings.Get("entry.targetInvalid", text));
        }
        if (!LocalPaths.IsAllowed(full, fs.IsNetworkDrive)) throw new ArgumentException(Strings.Get("entry.targetInvalid", text));
        var ext = Path.GetExtension(full);
        if (!TargetExtensions.Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException(Strings.Get("entry.targetType"));
        var stem = Path.GetFileNameWithoutExtension(full).TrimEnd(' ', '.');
        if (DeviceNames.Contains(stem) || full.Split('\\').Skip(1).Any(part => DeviceNames.Contains(Path.GetFileNameWithoutExtension(part).TrimEnd(' ', '.'))))
            throw new ArgumentException(Strings.Get("entry.targetInvalid", text));
        if (LaunchOptions.IsScript(full) && !LaunchOptions.IsScriptPathSafe(full)) throw new ArgumentException(Strings.Get("entry.scriptPathInvalid", full));
        var root = GameSessionTracker.InstallRoot(item) ?? throw new ArgumentException(Strings.Get("entry.targetNoFolder"));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(Strings.Get("entry.targetOutside", root.TrimEnd('\\')));
        if (!fs.FileExists(full)) throw new ArgumentException(Strings.Get("entry.targetMissing", full));
        // Neither the file nor a folder between the install folder and it may lead elsewhere (a junction "bin" swapped in after the approval).
        // The install folder itself may be one (a library moved to another drive): the scanned start and the play time go by it as well.
        if (fs.IsRedirect(full) || fs.HasRedirectBelow(root, full)) throw new ArgumentException(Strings.Get("entry.targetInvalid", full));
        if (item.Type == ItemType.Game && !GameEntryRules.MayBeGame(full, fs.WindowsDir)) throw new ArgumentException(Strings.Get("entry.notAGame", item.Name));
        return full;
    }

    /// <summary>
    /// The working folder: empty is the default (the program's folder), otherwise a local folder that exists. For a batch file only inside the
    /// install folder (see <see cref="ScriptCwdAllowed"/>).
    /// </summary>
    private static string? ValidateCwd(string? folder, string exe, LaunchFileSystem fs)
    {
        var cwd = LaunchOptions.ValidateWorkingDirectory(folder, exe, fs.DirectoryExists);
        if (cwd is not null && !LocalPaths.IsAllowed(cwd, fs.IsNetworkDrive)) throw new ArgumentException(Strings.Get("entry.cwdMissing", cwd));
        return cwd;
    }

    /// <summary>
    /// A batch file's working folder: the install folder or one inside it, reached without a link to elsewhere. cmd.exe looks for a program the
    /// script names without a path in the working folder first, so a folder outside would decide what the approved script runs.
    /// </summary>
    private static bool ScriptCwdAllowed(string cwd, LibraryItem item, LaunchFileSystem fs)
    {
        if (GameSessionTracker.InstallRoot(item) is not { } root) return false;
        var folder = Path.TrimEndingDirectorySeparator(cwd);
        if (!(folder + "\\").StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
        return folder.Length + 1 == root.Length || (!fs.IsRedirect(folder) && !fs.HasRedirectBelow(root, folder));
    }

    /// <summary>The honest refusal for a batch file over <see cref="FileFingerprint.MaxScriptBytes"/>.</summary>
    internal static string ScriptTooLarge(string script) =>
        Strings.Get("entry.scriptTooLarge", Path.GetFileName(script), FileFingerprint.MaxScriptBytes / (1024 * 1024));

    private static string ScriptCwdOutside(LibraryItem item) =>
        Strings.Get("entry.scriptCwdOutside", (GameSessionTracker.InstallRoot(item) ?? "").TrimEnd('\\'));

    /// <summary>
    /// Applies a change to the override, all checks first (nothing is changed when one fails). Going back to the automatic start target
    /// also drops the parameters and the folder: they belonged to the file that was started.
    /// </summary>
    public static void Apply(ItemOverride o, LibraryItem item, LaunchOverrideChange change, LaunchFileSystem fs)
    {
        var target = o.LaunchTarget;
        var args = o.LaunchArgs;
        var cwd = o.LaunchCwd;
        var pin = FileFingerprint.Pinned(o);
        if (change.SetTarget)
        {
            if (string.IsNullOrWhiteSpace(change.Target))
            {
                target = null;
                args = null;
                cwd = null;
                pin = null;
            }
            else
            {
                if (!TargetAllowed(item)) throw new ArgumentException(Strings.Get("entry.targetNoFolder"));
                var picked = ValidateTarget(change.Target, item, fs);
                // Another file: its own folder is the default again.
                if (!string.Equals(picked, target, StringComparison.OrdinalIgnoreCase)) cwd = null;
                target = picked;
                // Picking the target (again) approves its content: a batch file's is pinned now, an .exe's is not (see SECURITY.md).
                pin = LaunchOptions.IsScript(picked)
                    ? fs.FingerprintOf(picked) ?? throw new ArgumentException(Strings.Get("entry.targetInvalid", picked))
                    : null;
                if (pin is { TooLarge: true }) throw new ArgumentException(ScriptTooLarge(picked));
            }
        }
        // A batch file changed since its approval: another change (parameters, folder) does not approve it on the side; picking it again does.
        else if (target is not null && LaunchOptions.IsScript(target) && (pin is null || fs.FingerprintOf(target) != pin))
            throw new ArgumentException(Strings.Get("entry.targetChanged", Path.GetFileName(target)));
        // The program the parameters and the folder are for: the own target, or the scanned exe (not an entry the user added: its fields are its own).
        var exe = target ?? (item.Launch.Kind == LaunchKind.Exe && item.Source != "custom" && !item.Launch.Embedded ? item.Launch.Target : null);
        if (change.SetArgs)
        {
            if (exe is null) throw new ArgumentException(Strings.Get(ArgsMode(item, o) == "steam" ? "entry.argsInSteam" : "entry.argsInLauncher"));
            args = string.IsNullOrWhiteSpace(change.Args) && target is null ? null : [.. LaunchOptions.ParseArguments(change.Args)];
        }
        if (change.SetCwd)
        {
            if (exe is null) throw new ArgumentException(Strings.Get("entry.argsInLauncher"));
            cwd = ValidateCwd(change.Cwd, exe, fs);
        }
        if (exe is not null && LaunchOptions.IsScript(exe))
        {
            LaunchOptions.CheckScriptArguments(args ?? (target is null ? item.Launch.Args : []));
            // Also a folder kept from before (set for an .exe target) must fit a batch file.
            if (cwd is not null && !ScriptCwdAllowed(cwd, item, fs)) throw new ArgumentException(ScriptCwdOutside(item));
        }
        o.LaunchTarget = target;
        o.LaunchArgs = args;
        o.LaunchCwd = cwd;
        o.LaunchTargetSha256 = pin?.Sha256;
        o.LaunchTargetSize = pin?.Size;
    }

    /// <summary>
    /// What is started for the entry. The own start target is checked again (it may have been swapped, moved or the config edited since):
    /// a target that no longer passes stops the start instead of starting something else. A batch file must still have the content it was
    /// approved with (<see cref="FileFingerprint"/>); the launcher checks that once more on the handle it keeps open while cmd.exe runs.
    /// </summary>
    public static LaunchTarget Effective(LibraryItem item, ItemOverride? o, LaunchFileSystem fs)
    {
        if (o is null || !o.HasLaunch || item.Launch.Embedded) return item.Launch;
        if (o.LaunchArgs is { } list && (list.Sum(a => a.Length + 1) > LaunchOptions.MaxArgumentsLength || list.Any(a => a.Any(char.IsControl))))
            throw new InvalidOperationException(Strings.Get("entry.argsInvalid"));
        if (o.LaunchTarget is { } stored)
        {
            string target;
            try
            {
                target = ValidateTarget(stored, item, fs);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(ex.Message);
            }
            if (!string.Equals(target, stored, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(Strings.Get("entry.targetInvalid", stored));
            if (LaunchOptions.IsScript(target))
            {
                // Hashed once: the refusal names the reason from the same reading.
                var now = fs.FingerprintOf(target);
                if (FileFingerprint.Pinned(o) is not { } pin || now != pin)
                    throw new InvalidOperationException(now is { TooLarge: true } ? ScriptTooLarge(target) : Strings.Get("entry.targetChanged", Path.GetFileName(target)));
            }
            var cwd = o.LaunchCwd;
            if (cwd is not null && !LocalPaths.IsAllowed(cwd, fs.IsNetworkDrive)) throw new InvalidOperationException(Strings.Get("entry.cwdMissing", cwd));
            if (cwd is not null && LaunchOptions.IsScript(target) && !ScriptCwdAllowed(cwd, item, fs)) throw new InvalidOperationException(ScriptCwdOutside(item));
            return new LaunchTarget(LaunchKind.Exe, target) { Args = o.LaunchArgs ?? [], WorkingDirectory = cwd ?? Path.GetDirectoryName(target) };
        }
        if (item.Launch.Kind == LaunchKind.Exe && item.Source != "custom")
        {
            if (o.LaunchCwd is { } cwd && !LocalPaths.IsAllowed(cwd, fs.IsNetworkDrive)) throw new InvalidOperationException(Strings.Get("entry.cwdMissing", cwd));
            if (o.LaunchCwd is { } scriptCwd && LaunchOptions.IsScript(item.Launch.Target) && !ScriptCwdAllowed(scriptCwd, item, fs))
                throw new InvalidOperationException(ScriptCwdOutside(item));
            return item.Launch with { Args = o.LaunchArgs ?? item.Launch.Args, WorkingDirectory = o.LaunchCwd ?? item.Launch.WorkingDirectory };
        }
        return item.Launch;
    }

    /// <summary>
    /// The content a start's batch file must have: the pinned fingerprint when it is the own start target (checked by <see cref="Effective"/>),
    /// null otherwise.
    /// </summary>
    public static FileFingerprint? PinFor(LaunchTarget start, ItemOverride? o) =>
        start.Kind == LaunchKind.Exe && LaunchOptions.IsScript(start.Target)
        && o?.LaunchTarget is { } own && string.Equals(own, start.Target, StringComparison.OrdinalIgnoreCase)
            ? FileFingerprint.Pinned(o)
            : null;

    /// <summary>
    /// A batch file whose content no approval covers: an entry the user added with a .bat/.cmd, or a scanned one. It starts only on an open couch,
    /// as before (when the shell's lock refused cmd.exe for it); an own start target with its pinned content starts like the game.
    /// </summary>
    public static bool IsUnpinnedScript(LaunchTarget start, ItemOverride? o) =>
        start.Kind == LaunchKind.Exe && LaunchOptions.IsScript(start.Target) && PinFor(start, o) is null;
}
