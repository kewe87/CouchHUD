using System.Text;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

/// <summary>Findings of the security review: the validator never throws, patterns are exact, secrets stay out of ToString.</summary>
public class HardeningTests
{
    private static readonly RequestValidator Validator = new(_ => true);

    private static ValidationResult Check(string json) => Validator.Validate(Encoding.UTF8.GetBytes(json));

    private static readonly string Hash = "pbkdf2-sha256$100000$" + Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);
    private static readonly string Grant = Convert.ToBase64String(new byte[32]);

    private static string Setup(string name) =>
        $$$"""{"fileName":"{{{name}}}","size":10,"checksumFile":"{{{Convert.ToBase64String(new byte[10])}}}","signature":"{{{Convert.ToBase64String(new byte[10])}}}"}""";

    // ---- M1: never throws ----------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"v":1,"op":"Hel\ud800lo"}""")]
    [InlineData("""{"v":1,"op":"Ping","\ud800":1}""")]
    [InlineData("""{"v":1,"op":"Ping","args":{"\udc00":1}}""")]
    [InlineData("""{"v":1,"op":"ApplyLockPolicies","args":{"groups":["lo\ud800ck"]}}""")]
    [InlineData("""{"v":1,"op":"SetAutoLogon","args":{"password":"a\ud800b"}}""")]
    [InlineData("""{"v":1,"op":"Ping","grant":"\ud800"}""")]
    [InlineData("""{"v":1,"op":"GetJob","args":{"jobId":"\ud800"}}""")]
    public void A_lone_surrogate_gives_an_answer_not_an_exception(string json)
    {
        var result = Check(json);   // would throw InvalidOperationException before
        Assert.False(result.Ok);
        Assert.Contains(result.Code, ErrorCodes.All);
    }

    [Fact]
    public void The_surrogate_in_the_operation_or_a_field_name_is_bad_json()
    {
        Assert.Equal(ErrorCodes.BadJson, Check("""{"v":1,"op":"Hel\ud800lo"}""").Code);
        Assert.Equal(ErrorCodes.BadJson, Check("""{"v":1,"op":"Ping","\ud800":1}""").Code);
    }

    [Fact]
    public void Random_bytes_and_mutated_requests_never_throw()
    {
        var random = new Random(42);
        var good = ProtocolJson.SerializeRequest(Operation.SetAutoLogon, new SetAutoLogonArgs("pw"));
        for (var i = 0; i < 3000; i++)
        {
            var bytes = (byte[])good.Clone();
            for (var m = 0; m < 1 + i % 4; m++) bytes[random.Next(bytes.Length)] = (byte)random.Next(256);
            var result = Validator.Validate(bytes);
            Assert.Contains(result.Code, ErrorCodes.All);
            var noise = new byte[random.Next(1, 300)];
            random.NextBytes(noise);
            Assert.Contains(Validator.Validate(noise).Code, ErrorCodes.All);
        }
    }

    // ---- M2: exact patterns --------------------------------------------------------------------------

    [Fact]
    public void A_trailing_newline_does_not_pass_a_pattern()
    {
        Assert.Equal(ErrorCodes.BadArgument, Check($$$"""{"v":1,"op":"ReleaseLockPolicies","grant":"{{{Grant}}}\n","args":{"groups":["lock"]}}""").Code);
        Assert.Equal(ErrorCodes.BadArgument, Check("""{"v":1,"op":"GetJob","args":{"jobId":"0123456789abcdef0123456789abcdef\n"}}""").Code);
        Assert.Equal(ErrorCodes.BadArgument, Check($$$"""{"v":1,"op":"InstallCouchHudUpdate","args":{{{Setup("CouchHud-Setup-0.4.1.exe\\n")}}}}""").Code);
        Assert.Equal(ErrorCodes.BadArgument, Check($$$"""{"v":1,"op":"EnrollPasscode","args":{"hash":"{{{Hash}}}\n"}}""").Code);
        Assert.Equal(ErrorCodes.BadArgument, Check("""{"v":1,"op":"PauseServices","args":{"names":["WSearch\n"]}}""").Code);
        // ... and the same without the newline passes.
        Assert.True(Check($$$"""{"v":1,"op":"EnrollPasscode","args":{"hash":"{{{Hash}}}"}}""").Ok);
        Assert.True(Check("""{"v":1,"op":"GetJob","args":{"jobId":"0123456789abcdef0123456789abcdef"}}""").Ok);
    }

    [Fact]
    public void Digits_of_other_scripts_do_not_pass()
    {
        // U+0660 ARABIC-INDIC DIGIT ZERO, U+FF11 FULLWIDTH DIGIT ONE
        Assert.Equal(ErrorCodes.BadArgument, Check($$$"""{"v":1,"op":"InstallCouchHudUpdate","args":{{{Setup("CouchHud-Setup-\\u0660.4.1.exe")}}}}""").Code);
        Assert.Equal(ErrorCodes.BadArgument, Check("""{"v":1,"op":"EnrollPasscode","args":{"hash":"pbkdf2-sha256$١٠٠٠٠٠$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}}""").Code);
        Assert.Equal(ErrorCodes.BadArgument, Check("""{"v":1,"op":"GetJob","args":{"jobId":"٠123456789abcdef0123456789abcdef"}}""").Code);
        Assert.True(Check($$$"""{"v":1,"op":"InstallCouchHudUpdate","args":{{{Setup("CouchHud-Setup-0.4.1.exe")}}}}""").Ok);
    }

    [Fact]
    public void The_service_name_check_is_mandatory_and_applies()
    {
        Assert.Throws<ArgumentNullException>(() => new RequestValidator(null!));
    }

    // ---- L4: details are printable ASCII ------------------------------------------------------------

    [Fact]
    public void The_detail_of_a_refusal_is_short_printable_ascii()
    {
        foreach (var json in new[]
        {
            "{\"v\":1,\"op\":\"Ping\",\"\\u0007\\u202e evil\\n\":1}",
            "{\"v\":1,\"op\":\"\\u202egnp\\r\\n[ERROR] forged\"}",
            "{\"v\":1,\"op\":\"" + new string('x', 5000) + "\"}"
        })
        {
            var detail = Check(json).Detail!;
            Assert.True(detail.Length <= 80, detail.Length.ToString());
            Assert.All(detail, c => Assert.InRange(c, ' ', '~'));
        }
        Assert.All(Check("""{"v":1,"op":"Ping",}""").Detail!, c => Assert.InRange(c, ' ', '~'));
    }

    // ---- M3: secrets stay out of ToString ----------------------------------------------------------

    [Fact]
    public void Secrets_never_appear_in_ToString()
    {
        const string password = "hunter2-Passw0rd";
        var texts = new List<string>
        {
            new SetAutoLogonArgs(password, true).ToString(),
            new PasscodeHashArgs(Hash).ToString(),
            new VerifyCodeArgs("parent", ["a", "b", "x", "y", "up", "down"]).ToString(),
            new GrantData(Grant, "unlock", 120).ToString(),
            new RequestEnvelope(1, "SetAutoLogon", Grant, System.Text.Json.JsonDocument.Parse($$"""{"password":"{{password}}"}""").RootElement).ToString(),
            Response<GrantData>.Success(new GrantData(Grant, "unlock", 120)).ToString()
        };
        var request = Check($$$"""{"v":1,"op":"SetAutoLogon","grant":"{{{Grant}}}","args":{"password":"{{{password}}}"}}""").Request!;
        texts.Add(request.ToString());
        texts.Add(request.Args!.ToString()!);

        foreach (var text in texts)
        {
            Assert.DoesNotContain(password, text);
            Assert.DoesNotContain(Grant, text);
            Assert.DoesNotContain(Hash, text);
            Assert.DoesNotContain("Sequence = [", text);
            Assert.DoesNotContain("System.String[]", text);
        }
        Assert.Contains("SetAutoLogon", request.ToString());
        Assert.Contains("unlock", texts[3]);
    }

    [Fact]
    public void A_validation_failure_does_not_echo_the_secret_either()
    {
        var result = Check("""{"v":1,"op":"SetAutoLogon","args":{"password":"hunter2","extra":1}}""");
        Assert.DoesNotContain("hunter2", result.ToString());
        Assert.DoesNotContain("hunter2", result.Detail);
    }
}
