using System.Security.Cryptography;
using System.Text;

namespace CouchHud.Shell.Services;

/// <summary>
/// Keeps tokens and API keys out of config.json in plain text: they are
/// encrypted for the current Windows user (DPAPI) and only readable by them.
/// </summary>
public static class Secrets
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CouchHud");

    public static string Protect(string plain) => Protect(plain, null);

    /// <summary>Decrypts a stored value; plain values (typed into the file by hand) pass through.</summary>
    public static string Unprotect(string stored) => Unprotect(stored, null);

    /// <summary>
    /// Entropy of a scope (a plugin id, for example): what is protected for one scope cannot be unprotected as another's or as the
    /// program's own through these methods.
    /// </summary>
    private static byte[] EntropyOf(string? scope) => scope is null ? Entropy : Encoding.UTF8.GetBytes("CouchHud:" + scope.ToLowerInvariant());

    public static string Protect(string plain, string? scope)
    {
        if (string.IsNullOrEmpty(plain)) return "";
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), EntropyOf(scope), DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(data);
    }

    public static string Unprotect(string stored, string? scope)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored ?? "";
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), EntropyOf(scope), DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Console.Error.WriteLine($"[secrets] could not decrypt a stored secret: {ex.Message}");
            return "";
        }
    }
}
