namespace CouchHud.Core.Files;

public enum KeyFileState
{
    /// <summary>An existing key was read.</summary>
    Read,
    /// <summary>There was no key file (or an empty one): a new key was created and stored.</summary>
    Created,
    /// <summary>A key file exists but cannot be read (the protection belongs to another Windows profile or password state): it stays untouched.</summary>
    Unreadable,
    /// <summary>The file could not be read or written (access error): nothing was changed.</summary>
    Failed
}

/// <summary>
/// The secret behind the signed files, kept in one file. A new key is only ever created when the file is truly absent: a file that
/// exists but cannot be decrypted is never replaced, because every signed file would then silently read as damaged with no way back
/// in the UI. Replacing it is a deliberate step (delete the file).
/// </summary>
public static class KeyFile
{
    /// <summary>
    /// <see cref="Load"/>, tried again only while the state is <see cref="KeyFileState.Failed"/> (a lock for a moment, by a virus scanner for example).
    /// <see cref="KeyFileState.Unreadable"/> is final at once. Blocks for at most (attempts - 1) * delay.
    /// </summary>
    public static (byte[]? Key, KeyFileState State) LoadWithRetry(string file, Func<string, string> protect, Func<string, string> unprotect, int attempts, TimeSpan delay, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        for (var attempt = 1; ; attempt++)
        {
            var result = Load(file, protect, unprotect);
            if (result.State != KeyFileState.Failed || attempt >= attempts) return result;
            sleep(delay);
        }
    }

    public static (byte[]? Key, KeyFileState State) Load(string file, Func<string, string> protect, Func<string, string> unprotect)
    {
        try
        {
            if (File.Exists(file))
            {
                var text = File.ReadAllText(file);
                // Nothing in the file (a write that was cut off): there is no key material to lose.
                if (text.Trim().Length > 0)
                {
                    // Only what DPAPI protected counts: a plain key typed or planted into the file passes Unprotect unchanged and is refused.
                    if (!text.TrimStart().StartsWith("dpapi:", StringComparison.Ordinal)) return (null, KeyFileState.Unreadable);
                    var stored = unprotect(text);
                    if (stored.Length == 0) return (null, KeyFileState.Unreadable);
                    var key = Convert.FromBase64String(stored);
                    return key.Length > 0 ? (key, KeyFileState.Read) : (null, KeyFileState.Unreadable);
                }
            }
            var created = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            DurableFile.WriteAllText(file, protect(Convert.ToBase64String(created)), keepBackup: false);
            return (created, KeyFileState.Created);
        }
        catch (FormatException)
        {
            // Text that is no key: also left as it is.
            return (null, KeyFileState.Unreadable);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Console.Error.WriteLine($"[key] no key for signed files: {ex.Message}");
            return (null, KeyFileState.Failed);
        }
    }
}
