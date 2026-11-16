using System.Security.Cryptography;
using System.Text;
using CouchHud.Core.Config;
using CouchHud.Core.Security;

namespace CouchHud.Core.Library;

/// <summary>What the host does with a companion start once the pin has been looked at.</summary>
public enum PinStart
{
    Start,
    /// <summary>The couch is locked and the rule does not start on a locked couch.</summary>
    SkipNotLocked,
    /// <summary>Locked, and the parent has not confirmed the program for it (a legacy rule, or an old pin format).</summary>
    SkipUnpinned,
    /// <summary>Locked, and the program is not the one that was confirmed (file, path, arguments or folder changed).</summary>
    SkipChanged,
    /// <summary>Locked, confirmed, but the program's folder can be changed by others now and the parent did not allow that.</summary>
    SkipFolderWritable
}

/// <summary>
/// A game was started through CouchHud while the couch was open: its companions are judged as at an open couch. The note belongs to that one
/// launch (its serial), so a session that came from another route, an earlier or later launch, or a start outside CouchHud never uses it.
/// </summary>
public sealed record OpenLaunchNote(string ItemId, long Serial, DateTimeOffset At)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    /// <summary>Does this session come from exactly the launch the note was made for, still within a few minutes.</summary>
    public bool Accepts(GameSession session, DateTimeOffset now) =>
        Serial != 0 && session.Launch == Serial && !session.External && session.Item.Id == ItemId && now - At < Window;

    /// <summary>Does the note belong to this session (it ends with it).</summary>
    public bool Belongs(GameSession session) => Serial != 0 && session.Launch == Serial;

    /// <summary>
    /// The note after the unlock is forgotten. Bringing the session's own game forward (its hand-over, a resume) keeps the note of exactly that
    /// launch; every other occasion (a trip to the HUD, a profile switch, a new start, a resume of another session) ends it.
    /// </summary>
    public static OpenLaunchNote? AfterForgetUnlock(OpenLaunchNote? note, GameSession? resuming) =>
        note is not null && resuming is not null && note.Belongs(resuming) && !resuming.External ? note : null;
}

/// <summary>
/// The parent's confirmation of a companion program for the locked couch. The pin is a digest over what the start really is: the canonical
/// path of the held file, its arguments, its working folder and the SHA-256 of the file. A start on a locked couch bypasses the child's own
/// list, so it only happens for exactly what the parent confirmed. It protects against a swapped file (or changed start), not against a
/// process of the same user that edits the settings file: same-user file access is out of scope, as before.
/// All decisions are here, pure, so they can be tested; the host only reads the facts (held file, lock, folder ACL).
/// </summary>
public static class CompanionPin
{
    /// <summary>Format version in front of the digest. A pin without it (an older format) counts as "not confirmed": fail closed.</summary>
    public const string Version = "3:";

    public static bool IsPin(string? pin) =>
        pin is { Length: 2 + 64 } && pin.StartsWith(Version, StringComparison.Ordinal) && pin.AsSpan(2).ToString().All(Uri.IsHexDigit);

    /// <summary>The pin as it is kept (lower case), or empty when it is not one.</summary>
    public static string Normalize(string? pin) => IsPin(pin?.Trim()) ? pin!.Trim().ToLowerInvariant() : "";

    /// <summary>The folder the program starts in: the rule's own, or the program's folder.</summary>
    public static string EffectiveFolder(StartPlan plan, string canonicalPath) =>
        string.IsNullOrWhiteSpace(plan.WorkingDirectory) ? Path.GetDirectoryName(canonicalPath) ?? "" : plan.WorkingDirectory;

    /// <summary>The digest of a start. The same function runs when the parent confirms and right before the start.</summary>
    public static string Digest(StartPlan plan, string canonicalPath, string exeSha256Hex, bool allowWritableFolder = false)
    {
        var text = new StringBuilder("couchhud-companion-pin|3");
        foreach (var field in new[] { canonicalPath.ToUpperInvariant(), plan.Arguments ?? "", EffectiveFolder(plan, canonicalPath).ToUpperInvariant(), exeSha256Hex.ToLowerInvariant(), allowWritableFolder ? "allow-writable-folder" : "folder-must-be-protected" })
            text.Append('|').Append(field.Length).Append(':').Append(field);
        return Version + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    /// <summary>The program of a plan held open (writers kept out), by the same path string for the pin and for the start.</summary>
    public static HeldProgram? Hold(StartPlan plan) => plan.Direct || plan.PackagedAppId is not null ? null : CompanionPath.Hold(plan.FileName);

    /// <summary>The digest of a held program for a plan.</summary>
    public static string DigestOf(StartPlan plan, HeldProgram held, bool allowWritableFolder = false) => Digest(plan, held.Path, held.Sha256Hex(), allowWritableFolder);

    /// <summary>The first 12 hex digits of the file's SHA-256, for people to compare.</summary>
    public static string Short(string exeSha256Hex) => exeSha256Hex.Length <= 12 ? exeSha256Hex : exeSha256Hex[..12];

    /// <summary>Is a rule newly pinned by a change: it starts on the locked couch from now on (it did not before) and has no signature rule.</summary>
    public static bool ShouldPin(CompanionRule? before, bool nowLocked) =>
        nowLocked && before?.WhenLocked != true && string.IsNullOrEmpty(before?.Sha256);

    /// <summary>
    /// What the host does right before it starts. <paramref name="plannedLocked"/> is the lock at planning time, <paramref name="lockedNow"/> a
    /// fresh read right before CreateProcess: locked at either moment counts as locked. A signature rule (Wand) is not held to the pin, but
    /// never starts on a locked couch unless it says so.
    /// </summary>
    public static PinStart Decide(CompanionRule rule, bool plannedLocked, bool lockedNow, string currentDigest, Func<bool>? folderWritable = null)
    {
        if (!(plannedLocked || lockedNow)) return PinStart.Start;
        if (!rule.WhenLocked) return PinStart.SkipNotLocked;
        if (!string.IsNullOrEmpty(rule.Sha256)) return PinStart.Start;
        if (!IsPin(rule.LockedSha256)) return PinStart.SkipUnpinned;
        if (!string.Equals(rule.LockedSha256, currentDigest, StringComparison.OrdinalIgnoreCase)) return PinStart.SkipChanged;
        // The folder is judged now, not only when it was confirmed: a folder that became writable since blocks the start, unless the parent
        // allowed a writable folder explicitly (that flag is inside the digest compared above).
        return !rule.AllowWritableFolder && (folderWritable?.Invoke() ?? true) ? PinStart.SkipFolderWritable : PinStart.Start;
    }

    /// <summary>
    /// The page's problem state of a rule: "pinMissing" (not confirmed), "pinChanged" (the last locked start found another program),
    /// "folderWritable" (confirmed, but the folder can be written by others: a warning, not a refusal) or null. Only rules that start on a
    /// locked couch have one; a signature rule (Wand) has its own.
    /// </summary>
    public static string? Problem(CompanionRule rule, bool changedAtLastLockedStart, bool folderWritable)
    {
        if (!rule.WhenLocked || !string.IsNullOrEmpty(rule.Sha256)) return null;
        if (!IsPin(rule.LockedSha256)) return "pinMissing";
        if (changedAtLastLockedStart) return "pinChanged";
        return folderWritable && !rule.AllowWritableFolder ? "folderWritable" : null;
    }

    /// <summary>Does the page offer the one-action "confirm" for this problem (a writable folder is cleared by allowing it there).</summary>
    public static bool Confirmable(string? problem) => problem is "pinMissing" or "pinChanged" or "folderWritable";

    /// <summary>
    /// Why a confirmation is refused, or null. The rule must exist, start on a locked couch and have no signature rule, and the file now must be
    /// the one the person saw (<paramref name="seenDigest"/>), whose digest the second factor was bound to.
    /// </summary>
    public static string? ConfirmRefusal(CompanionRule? rule, string? seenDigest, string currentDigest)
    {
        if (rule is null) return "missing";
        if (!rule.WhenLocked) return "notLocked";
        if (!string.IsNullOrEmpty(rule.Sha256)) return "signatureRule";
        if (!IsPin(seenDigest) || !string.Equals(seenDigest, currentDigest, StringComparison.OrdinalIgnoreCase)) return "changed";
        return null;
    }

    // What may be done to a file: write, append, delete, change the ACL, take ownership.
    private const int FileWriteMask = FolderProtection.WriteData | FolderProtection.AppendData | FolderProtection.Delete | FolderProtection.ChangePermissions | FolderProtection.TakeOwnership;

    /// <summary>
    /// Can others change what is in the folder (from its ACL): anyone but administrators, SYSTEM and TrustedInstaller may write in it or own it,
    /// or an entry that is inherited by the files in it (OI), also an inherit-only one such as Users:(OI)(IO)(M), gives them write access to those files.
    /// </summary>
    public static bool FolderWritable(string? ownerSid, IEnumerable<AclEntry> entries)
    {
        var list = entries as IReadOnlyCollection<AclEntry> ?? entries.ToList();
        if (!FolderProtection.IsProtected(ownerSid, list)) return true;
        return list.Any(e => e.ObjectInherit && !FolderProtection.IsTrusted(e.Sid) && (FolderProtection.MapGeneric(e.Rights) & FileWriteMask) != 0);
    }

    /// <summary>The ACL of a path (owner and allow entries), or null when it cannot be read.</summary>
    public delegate (string? Owner, IReadOnlyList<AclEntry> Entries)? AclReader(string path);

    /// <summary>
    /// Can others change what starts: the program file itself, its folder (and files inherited into it), the folder it starts in, or swap either
    /// folder by renaming or replacing it anywhere up the chain (delete, delete-child, change-permissions, take-ownership on an ancestor).
    /// Any ACL that cannot be read counts as writable: fail closed.
    /// </summary>
    public static bool Writable(AclReader aclOf, string exePath, string workingFolder)
    {
        try
        {
            if (aclOf(exePath) is not { } file || !FolderProtection.IsProtected(file.Owner, file.Entries)) return true;
            var folders = new List<string>();
            var exeFolder = Path.GetDirectoryName(exePath);
            if (!string.IsNullOrEmpty(exeFolder)) folders.Add(exeFolder);
            if (!string.IsNullOrWhiteSpace(workingFolder) && !folders.Contains(workingFolder, StringComparer.OrdinalIgnoreCase)) folders.Add(workingFolder);
            foreach (var folder in folders)
            {
                if (aclOf(folder) is not { } acl || FolderWritable(acl.Owner, acl.Entries)) return true;
                for (var up = Path.GetDirectoryName(folder.TrimEnd('\\', '/')); !string.IsNullOrEmpty(up); up = Path.GetDirectoryName(up))
                {
                    if (aclOf(up) is not { } ancestor || !FolderProtection.IsProtected(ancestor.Owner, ancestor.Entries, FolderProtection.ReplaceMask)) return true;
                }
            }
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }
}
