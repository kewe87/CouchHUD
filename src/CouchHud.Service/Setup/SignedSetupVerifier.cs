using System.Text;
using System.Text.RegularExpressions;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Setup;

public enum SetupKind
{
    CouchHud
}

/// <summary>What the signed checksum file says about the setup that is to come: its name, version and hash. Nothing in here is from the client.</summary>
public sealed record SetupPlan(SetupKind Kind, string FileName, SemVer Version, string Sha256, long Size);

/// <summary>A plan, or the error code for the client and the reason for the log.</summary>
public sealed record SetupVerdict(SetupPlan? Plan, string Code, string? Reason = null)
{
    public bool Ok => Plan is not null;

    public static SetupVerdict Refuse(string code, string reason) => new(null, code, reason);
}

/// <summary>
/// Checks the header of a setup stream before a single byte of the setup is accepted: the signature of the checksum file with the
/// public key built into this program (the CouchHUD release key with the context "couchhud-release-v1"), then the setup's name, version and hash from the signed content. The client's file name is only compared
/// with it; the version comes from the signed name and must be newer than what is installed (no downgrade, no replay of the same
/// release) and at least the minimum. The crypto is <see cref="ReleaseIntegrity"/> of Core, not a copy.
/// </summary>
/// <param name="couchHudKeyPem">Null: the built-in key (<see cref="CatalogSignature.PublicKeyPem"/>). Tests pass a throwaway key.</param>
public sealed class SignedSetupVerifier(string? couchHudKeyPem = null)
{
    /// <summary>The first CouchHUD that has the service: an older setup would leave it behind (design §4 "Rückweg").</summary>
    public static readonly SemVer CouchHudMinimum = new(0, 4, 1, "");

    /// <summary>The largest CouchHUD setup the service takes (below StreamCodec's general 400 MB; a setup is far smaller).</summary>
    public const long MaxCouchHudSetupBytes = 200L * 1024 * 1024;

    public static long MaxSetupBytes(SetupKind kind) => MaxCouchHudSetupBytes;

    // The same shapes the validator accepts for the client's name (ASCII digits, no pre-release).
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex CouchHudName = new(@"^CouchHud-Setup-[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(\.[0-9]{1,4})?\.exe\z", Options);
    // A sha256sum line: hash, space, space or '*' (binary mode), name. Only to find the name; the hash is read by Core's parser.
    private static readonly Regex Line = new(@"^[0-9a-fA-F]{64} [ *](.+)\z", Options);

    public static string Prefix(SetupKind kind) => SelfUpdateRules.SetupPrefix;

    public static SemVer Minimum(SetupKind kind) => CouchHudMinimum;

    /// <param name="installed">The installed version; null: CouchHUD's cannot be read, which is refused.</param>
    public SetupVerdict Verify(SetupKind kind, SetupStreamArgs args, SemVer? installed)
    {
        if (args.Size <= 0 || args.Size > MaxSetupBytes(kind)) return SetupVerdict.Refuse(ErrorCodes.StreamTooLarge, $"announced {args.Size} bytes, at most {MaxSetupBytes(kind)}");
        byte[] checksum;
        try
        {
            checksum = Convert.FromBase64String(args.ChecksumFile);
        }
        catch (FormatException)
        {
            return SetupVerdict.Refuse(ErrorCodes.VerificationFailed, "checksum file not base64");
        }

        try
        {
            ReleaseIntegrity.VerifySignature(checksum, args.Signature, couchHudKeyPem);
        }
        catch (CatalogException)
        {
            return SetupVerdict.Refuse(ErrorCodes.VerificationFailed, "signature not valid");
        }

        // From here on the content is signed by the release key.
        if (SignedName(kind, checksum) is not { } name) return SetupVerdict.Refuse(ErrorCodes.VerificationFailed, "the signed file names no single setup");
        string sha256;
        try
        {
            sha256 = ReleaseIntegrity.ParseChecksum(checksum, name);
        }
        catch (CatalogException)
        {
            return SetupVerdict.Refuse(ErrorCodes.VerificationFailed, "signed checksum file not in the expected form");
        }
        if (!string.Equals(args.FileName, name, StringComparison.OrdinalIgnoreCase)) return SetupVerdict.Refuse(ErrorCodes.VerificationFailed, "file name differs from the signed one");

        if (!SemVer.TryParse(name[Prefix(kind).Length..^".exe".Length], out var version)) return SetupVerdict.Refuse(ErrorCodes.VerificationFailed, "no version in the signed name");
        if (version < Minimum(kind)) return SetupVerdict.Refuse(ErrorCodes.Denied, $"version {version} below the minimum {Minimum(kind)}");
        if (installed is not { } current)
        {
            return SetupVerdict.Refuse(ErrorCodes.Denied, "installed CouchHUD version unknown");
        }
        if (version <= current) return SetupVerdict.Refuse(ErrorCodes.Denied, $"version {version} not newer than the installed {current}");

        return new SetupVerdict(new SetupPlan(kind, name, version, sha256, args.Size), ErrorCodes.Ok);
    }

    /// <summary>The one setup of this kind the signed file lists (a file that names two different setups names none).</summary>
    private static string? SignedName(SetupKind kind, byte[] checksum)
    {
        var pattern = CouchHudName;
        string? found = null;
        foreach (var raw in Encoding.UTF8.GetString(checksum).Split('\n'))
        {
            var m = Line.Match(raw.TrimEnd('\r'));
            if (!m.Success) continue;
            var name = m.Groups[1].Value;
            if (!pattern.IsMatch(name)) continue;
            if (found is not null && !found.Equals(name, StringComparison.OrdinalIgnoreCase)) return null;
            found ??= name;
        }
        return found;
    }
}
