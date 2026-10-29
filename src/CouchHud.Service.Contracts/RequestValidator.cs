using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CouchHud.Service.Contracts;

/// <summary>A request that passed <see cref="RequestValidator"/>: the operation, the grant (if one came along) and the typed arguments.</summary>
public sealed record ValidatedRequest(Operation Operation, string? Grant, object? Args)
{
    // The grant and the arguments may be secrets (a password, a code): never in a log line made from ToString().
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Operation = ").Append(Operation).Append(", Grant = ").Append(Grant is null ? "none" : "***").Append(", Args = ").Append(Args is null ? "none" : "***");
        return true;
    }

    public T GetArgs<T>() where T : class => Args as T ?? throw new InvalidOperationException($"{Operation} has no {typeof(T).Name}");
}

/// <summary>The verdict: a request, or one of the <see cref="ErrorCodes"/> and a short reason (for the log, never for the request's author to parse).</summary>
public sealed record ValidationResult(ValidatedRequest? Request, string Code, string? Detail = null)
{
    public bool Ok => Request is not null;

    public static ValidationResult Pass(ValidatedRequest request) => new(request, ErrorCodes.Ok);

    /// <summary>The detail may echo what the sender wrote: printable ASCII only, and short, so it can go into a log as it is.</summary>
    public static ValidationResult Fail(string code, string detail) => new(null, code, Printable(detail));

    private static string Printable(string text)
    {
        var length = Math.Min(text.Length, 80);
        return string.Create(length, text, (span, source) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = source[i] is >= ' ' and <= '~' ? source[i] : '?';
        });
    }
}

/// <summary>
/// Checks a message before anything else looks at it: the version, a known operation, only known fields (an unknown one is refused,
/// also a field twice), and for every argument a positive list, a length, a range or a pattern. Pure: no registry, no file.
/// What it cannot know (whether a service name is on the service's own list) the caller must pass in as a predicate (there is no
/// default: a validator that lets every well-formed name through is a mistake); the service checks the same again before it acts.
/// </summary>
public sealed class RequestValidator(Func<string, bool> serviceAllowed)
{
    private readonly Func<string, bool> _serviceAllowed = serviceAllowed ?? throw new ArgumentNullException(nameof(serviceAllowed));

    public static readonly IReadOnlySet<string> PolicyGroupIds = new HashSet<string>(StringComparer.Ordinal) { "lock", "taskmgr" };

    private static readonly IReadOnlySet<string> PasscodeButtons = new HashSet<string>(StringComparer.Ordinal)
        { "a", "b", "x", "y", "lb", "rb", "up", "down", "left", "right" };

    public const int MaxServiceNames = 16;
    public const int MaxUpdateIds = 100;
    public const int MaxPasswordChars = 256;
    public const int MaxChecksumBytes = 4096;
    public const int MaxSignatureBytes = 1024;
    public const int MaxEvents = 200;
    public const int MinPort = 1024;
    public const int MaxPorts = 2;

    // \z, not $ ($ lets a trailing newline through), [0-9], not \d (\d matches other scripts' digits), and no backtracking.
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    private static readonly Regex ServiceName = new(@"^[A-Za-z0-9_.\-]{1,80}\z", Options);
    private static readonly Regex GrantText = new(@"^[A-Za-z0-9+/]{43}=\z", Options);
    private static readonly Regex JobId = new(@"^[0-9a-f]{32}\z", Options);
    private static readonly Regex CouchHudSetupName = new(@"^CouchHud-Setup-[0-9]{1,4}\.[0-9]{1,4}\.[0-9]{1,4}(\.[0-9]{1,4})?\.exe\z", Options);
    // The iteration count is the one Passcode.Hash uses (100000); anything else is not a hash of ours.
    private static readonly Regex PasscodeHash = new(@"^pbkdf2-sha256\$100000\$[A-Za-z0-9+/]{22}==\$[A-Za-z0-9+/]{43}=\z", Options);

    private static readonly JsonDocumentOptions Strict = new() { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow };

    private static readonly string[] EnvelopeFields = ["v", "op", "grant", "args"];

    private static readonly IReadOnlyDictionary<string, Operation> Operations =
        Enum.GetValues<Operation>().ToDictionary(o => o.ToString(), o => o, StringComparer.Ordinal);

    /// <summary>
    /// Never throws: whatever the bytes are, the answer is a result. (Text with a lone surrogate parses but cannot be read as a string; that
    /// and anything else unexpected counts as bad JSON.) The copy of the bytes that is parsed is wiped afterwards; the strings read from
    /// it (a password, a code) are immutable .NET strings and live until the garbage collector takes them.
    /// </summary>
    public ValidationResult Validate(ReadOnlySpan<byte> json)
    {
        if (json.Length == 0) return ValidationResult.Fail(ErrorCodes.BadJson, "empty");
        if (json.Length > Protocol.MaxMessageBytes) return ValidationResult.Fail(ErrorCodes.TooLarge, $"{json.Length} bytes");
        var copy = json.ToArray();
        try
        {
            return ValidateCore(copy);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return ValidationResult.Fail(ErrorCodes.BadJson, ex.GetType().Name);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copy);
        }
    }

    private ValidationResult ValidateCore(byte[] json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, Strict);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail(ErrorCodes.BadJson, ex.Message);
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ValidationResult.Fail(ErrorCodes.BadJson, "not an object");
            if (UnknownOrTwice(root, EnvelopeFields) is { } field) return ValidationResult.Fail(ErrorCodes.UnknownField, field);

            if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v))
            {
                return ValidationResult.Fail(ErrorCodes.BadJson, "v missing");
            }
            if (v != Protocol.Version) return ValidationResult.Fail(ErrorCodes.BadVersion, $"v {v}");

            if (!root.TryGetProperty("op", out var opText) || opText.ValueKind != JsonValueKind.String) return ValidationResult.Fail(ErrorCodes.BadJson, "op missing");
            if (!Operations.TryGetValue(opText.GetString()!, out var operation)) return ValidationResult.Fail(ErrorCodes.UnknownOperation, Short(opText.GetString()));

            string? grant = null;
            if (root.TryGetProperty("grant", out var grantElement) && grantElement.ValueKind != JsonValueKind.Null)
            {
                if (grantElement.ValueKind != JsonValueKind.String || !GrantText.IsMatch(grantElement.GetString()!)) return ValidationResult.Fail(ErrorCodes.BadArgument, "grant");
                grant = grantElement.GetString();
            }

            JsonElement? args = root.TryGetProperty("args", out var argsElement) && argsElement.ValueKind != JsonValueKind.Null ? argsElement : null;
            return ValidateArguments(operation, grant, args);
        }
    }

    private ValidationResult ValidateArguments(Operation operation, string? grant, JsonElement? args)
    {
        switch (operation)
        {
            case Operation.Hello or Operation.Status or Operation.Ping or Operation.CleanupPolicyKeys or Operation.AutoLogonOff or Operation.CollectAdminApproval
                or Operation.RemoveRemoteFirewall or Operation.RemoveLegacyFirewallRules or Operation.MigrateTasks or Operation.ReleaseAllAccounts
                or Operation.SearchWindowsUpdates
                or Operation.ResetServiceState or Operation.ResetCodeStore:
                if (args is { } none && (none.ValueKind != JsonValueKind.Object || none.EnumerateObject().Any())) return Bad("no arguments allowed");
                return Pass(operation, grant, null);
            case Operation.ApplyLockPolicies:
                return Typed(operation, grant, args, ["groups"], ProtocolJsonContext.Default.ApplyLockPoliciesArgs,
                    a => Groups(a.Groups));
            case Operation.ReleaseLockPolicies:
                return Typed(operation, grant, args, ["groups"], ProtocolJsonContext.Default.ReleaseLockPoliciesArgs,
                    a => Groups(a.Groups));
            case Operation.PauseServices or Operation.ResumeServices:
                return Typed(operation, grant, args, ["names"], ProtocolJsonContext.Default.ServicesArgs, a => Names(a.Names));
            case Operation.SetAutoLogon:
                return Typed(operation, grant, args, ["password", "allowPasswordSignIn"], ProtocolJsonContext.Default.SetAutoLogonArgs, a =>
                    a.Password is not { Length: > 0 and <= MaxPasswordChars } || a.Password.Contains('\0') ? "password" : null);
            case Operation.InstallWindowsUpdates:
                if (args is null) return Pass(operation, grant, new InstallWindowsUpdatesArgs());
                return Typed(operation, grant, args, ["ids"], ProtocolJsonContext.Default.InstallWindowsUpdatesArgs, a => UpdateIds(a.Ids));
            case Operation.SetRemoteFirewall:
                return Typed(operation, grant, args, ["ports"], ProtocolJsonContext.Default.SetRemoteFirewallArgs, a => Ports(a.Ports));
            case Operation.InstallCouchHudUpdate:
                return Typed(operation, grant, args, ["fileName", "size", "checksumFile", "signature"], ProtocolJsonContext.Default.SetupStreamArgs,
                    a => Setup(a, CouchHudSetupName));
            case Operation.EnrollPasscode or Operation.ChangePasscode:
                return Typed(operation, grant, args, ["hash", "kind"], ProtocolJsonContext.Default.PasscodeHashArgs, a =>
                    a.Kind is not (CodeKinds.Couch or CodeKinds.Parent or CodeKinds.Both) ? "kind" : !IsPasscodeHash(a.Hash) ? "hash" : null);
            case Operation.RequestAdminApproval:
                // No operations: only "an administrator confirmed" (the HUD's own parental checks), no grant.
                return Typed(operation, grant, args, ["ops"], ProtocolJsonContext.Default.AdminApprovalArgs, a =>
                    a.Ops is null || (a.Ops.Length > 0 && !IsOps(a.Ops)) ? "ops" : null);
            case Operation.SetLockMode:
                return Typed(operation, grant, args, ["mode", "hash", "kind"], ProtocolJsonContext.Default.LockModeArgs, a =>
                    LockModes.Rank(a.Mode) < 0 ? "mode"
                    // A code only with the code lock, and both or neither of hash and kind.
                    : (a.Hash is null) != (a.Kind is null) ? "kind"
                    : a.Hash is not null && (a.Mode != LockModes.Code || !IsPasscodeHash(a.Hash) || a.Kind is not (CodeKinds.Couch or CodeKinds.Both)) ? "hash"
                    : null);
            case Operation.RemovePasscode:
                return Typed(operation, grant, args, ["kind"], ProtocolJsonContext.Default.CodeKindArgs, a =>
                    a.Kind is not (CodeKinds.Couch or CodeKinds.Parent) ? "kind" : null);
            case Operation.VerifyCode:
                return Typed(operation, grant, args, ["kind", "sequence", "ops"], ProtocolJsonContext.Default.VerifyCodeArgs, a =>
                    a.Kind is not (CodeKinds.Couch or CodeKinds.Parent) ? "kind"
                    : !IsSequence(a.Sequence) ? "sequence"
                    // A couch grant is for the unlock as a whole (no operations); a parent's grant only for the operations named.
                    : a.Ops is not null && (a.Kind != CodeKinds.Parent || !IsOps(a.Ops)) ? "ops" : null);
            case Operation.RequestWindowsGrant:
                // The unlock is something the service saw itself: nothing else comes along.
                return Typed(operation, grant, args, ["scope"], ProtocolJsonContext.Default.RequestWindowsGrantArgs, a =>
                    a.Scope != WindowsGrantScopes.Unlock ? "scope" : null);
            case Operation.GetJob:
                return Typed(operation, grant, args, ["jobId"], ProtocolJsonContext.Default.GetJobArgs, a =>
                    a.JobId is null || !JobId.IsMatch(a.JobId) ? "jobId" : null);
            case Operation.GetRecentEvents:
                if (args is null) return Pass(operation, grant, new GetRecentEventsArgs());
                return Typed(operation, grant, args, ["count"], ProtocolJsonContext.Default.GetRecentEventsArgs, a =>
                    a.Count is < 1 or > MaxEvents ? "count" : null);
            default:
                return ValidationResult.Fail(ErrorCodes.UnknownOperation, operation.ToString());
        }

        static ValidationResult Bad(string detail) => ValidationResult.Fail(ErrorCodes.BadArgument, detail);
    }

    /// <summary>Reads the arguments into their type (only the listed fields, each once) and lets <paramref name="check"/> name the first bad one.</summary>
    private static ValidationResult Typed<T>(Operation operation, string? grant, JsonElement? args, string[] fields,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, Func<T, string?> check) where T : class
    {
        if (args is not { ValueKind: JsonValueKind.Object } element) return ValidationResult.Fail(ErrorCodes.BadArgument, "arguments missing");
        if (UnknownOrTwice(element, fields) is { } field) return ValidationResult.Fail(ErrorCodes.UnknownField, field);
        T? value;
        try
        {
            value = element.Deserialize(info);
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail(ErrorCodes.BadArgument, ex.Message);
        }
        if (value is null) return ValidationResult.Fail(ErrorCodes.BadArgument, "arguments missing");
        if (check(value) is { } bad) return ValidationResult.Fail(ErrorCodes.BadArgument, bad);
        return Pass(operation, grant, value);
    }

    private static ValidationResult Pass(Operation operation, string? grant, object? args) =>
        ValidationResult.Pass(new ValidatedRequest(operation, grant, args));

    /// <summary>The first property that is not in the list, or one that occurs twice (a parser that takes the last of two hides the first).</summary>
    private static string? UnknownOrTwice(JsonElement obj, string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in obj.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) return Short(property.Name);
            if (!seen.Add(property.Name)) return Short(property.Name) + " twice";
        }
        return null;
    }

    private static string Short(string? text) => text is null ? "" : text.Length <= 40 ? text : text[..40];

    /// <summary>
    /// The operations a parent's or an administrator's grant may be named for (each use of the grant is one of them): the ones whose rule
    /// accepts such a grant.
    /// </summary>
    public static readonly IReadOnlySet<string> GrantableOperations = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Operation.SetAutoLogon), nameof(Operation.ChangePasscode), nameof(Operation.EnrollPasscode), nameof(Operation.RemovePasscode),
        nameof(Operation.SetLockMode), nameof(Operation.ReleaseLockPolicies), nameof(Operation.CleanupPolicyKeys), nameof(Operation.SetRemoteFirewall),
        nameof(Operation.InstallCouchHudUpdate), nameof(Operation.ResetCodeStore)
    };

    /// <summary>How many uses one parent's or administrator's grant may name (e.g. two policy groups and their key cleanup).</summary>
    public const int MaxGrantOps = 4;


    private static bool IsSequence(string[]? sequence) =>
        sequence is { Length: >= 6 and <= 8 } && sequence.All(b => b is not null && PasscodeButtons.Contains(b));

    private static bool IsOps(string[]? ops) => ops is { Length: >= 1 and <= MaxGrantOps } && ops.All(o => o is not null && GrantableOperations.Contains(o));


    /// <summary>The format <c>Passcode.Hash</c> writes, with its iteration count (100000): anything else is no hash of ours.</summary>
    public static bool IsPasscodeHash(string? hash) => hash is { Length: <= 120 } && PasscodeHash.IsMatch(hash);


    private static string? Groups(string[]? groups) =>
        groups is not { Length: >= 1 } || groups.Length > PolicyGroupIds.Count || groups.Any(g => g is null || !PolicyGroupIds.Contains(g)) || groups.Distinct(StringComparer.Ordinal).Count() != groups.Length
            ? "groups" : null;

    private string? Names(string[]? names)
    {
        if (names is not { Length: >= 1 and <= MaxServiceNames } || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length) return "names";
        return names.Any(n => n is null || !ServiceName.IsMatch(n) || !_serviceAllowed(n)) ? "names" : null;
    }

    private static string? UpdateIds(string[]? ids) =>
        ids is null ? null   // none named: all of them
        : ids.Length is >= 1 and <= MaxUpdateIds && ids.All(i => i is not null && Guid.TryParseExact(i, "D", out _)) ? null : "ids";

    private static string? Ports(int[]? ports) =>
        ports is not { Length: >= 1 and <= MaxPorts } || ports.Any(p => p is < MinPort or > 65535) || ports.Distinct().Count() != ports.Length ? "ports" : null;

    private static string? Setup(SetupStreamArgs a, Regex name)
    {
        if (a.FileName is null || !name.IsMatch(a.FileName)) return "fileName";
        if (!StreamCodec.IsValidSize(a.Size)) return "size";
        if (!DecodesTo(a.ChecksumFile, MaxChecksumBytes)) return "checksumFile";
        if (!DecodesTo(a.Signature, MaxSignatureBytes)) return "signature";
        return null;
    }

    private static bool DecodesTo(string? base64, int maxBytes)
    {
        if (string.IsNullOrEmpty(base64) || base64.Length > maxBytes * 4 / 3 + 4) return false;
        var buffer = new byte[maxBytes + 3];
        return Convert.TryFromBase64String(base64, buffer, out var written) && written > 0 && written <= maxBytes;
    }
}
