using CouchHud.Core.Files;

namespace CouchHud.Shell.Services;

/// <summary>
/// The secret for the HMAC over files that must not be changed by a program that only writes files (the play time file,
/// the list of approved plugins): random, protected with DPAPI for this Windows user, in one file of the data folder.
/// A key file that cannot be decrypted (after a password reset, say) is never replaced: <see cref="Get"/> returns null,
/// <see cref="Unreadable"/> is set, and the user starts over on purpose by deleting parental.key.
/// </summary>
public static class DataKey
{
    private static readonly object Gate = new();
    private static byte[]? _cached;
    private static byte[]? _ephemeral;

    /// <summary>The key file exists but cannot be read: signed files cannot be checked, and nothing is signed with another key.</summary>
    public static bool Unreadable { get; private set; }

    /// <summary>The key file could not be read or written (a lock, an access error): no key this time, nothing was changed.</summary>
    public static bool Failed { get; private set; }

    /// <summary>The key, created on first use when the file is absent; null when it cannot be read or stored (then nothing is signed).</summary>
    public static byte[]? Get(string dataDir)
    {
        lock (Gate)
        {
            if (_cached is not null) return _cached;
            // Unreadable, or still failing after the retries: final for this session (a restart tries again), so nothing blocks twice.
            if (Unreadable || Failed) return null;
            var (key, state) = KeyFile.LoadWithRetry(Path.Combine(dataDir, "parental.key"), Secrets.Protect, Secrets.Unprotect, attempts: 5, delay: TimeSpan.FromMilliseconds(200));
            if (state == KeyFileState.Unreadable)
            {
                Unreadable = true;
                // Once; no key material.
                Console.Error.WriteLine("[key] parental.key cannot be decrypted (Windows profile or password reset?): it is left untouched, signed files read as damaged until it is deleted on purpose");
            }
            if (state == KeyFileState.Failed) Failed = true;
            return _cached = key;
        }
    }

    /// <summary>
    /// For the play time files while the key is unreadable: a random secret that lives only in this process and is never stored. A file
    /// signed with the old key then fails the check (tampered, today counts as used up) instead of being read without any check.
    /// </summary>
    public static byte[] Ephemeral()
    {
        lock (Gate) return _ephemeral ??= System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
    }
}
