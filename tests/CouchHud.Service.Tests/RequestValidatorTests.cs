using System.Text;
using CouchHud.Service.Contracts;

namespace CouchHud.Service.Tests;

public class RequestValidatorTests
{
    private static readonly RequestValidator Validator = new(name => name is "WSearch" or "SysMain" or "Spooler");

    private static ValidationResult Check(string json) => Validator.Validate(Encoding.UTF8.GetBytes(json));

    private static void Rejected(string json, string code) => Assert.Equal(code, Check(json).Code);

    private static string Request(string op, string? args = null) =>
        args is null ? $$"""{"v":1,"op":"{{op}}"}""" : $$"""{"v":1,"op":"{{op}}","args":{{args}}}""";

    private static readonly string Hash = "pbkdf2-sha256$100000$" + Convert.ToBase64String(new byte[16]) + "$" + Convert.ToBase64String(new byte[32]);
    private static readonly string Grant = Convert.ToBase64String(new byte[32]);

    // ---- positive ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Hello")]
    [InlineData("Status")]
    [InlineData("Ping")]
    [InlineData("CleanupPolicyKeys")]
    [InlineData("AutoLogonOff")]
    [InlineData("RemoveRemoteFirewall")]
    [InlineData("RemoveLegacyFirewallRules")]
    [InlineData("MigrateTasks")]
    [InlineData("ReleaseAllAccounts")]
    [InlineData("ResetServiceState")]
    public void Operations_without_arguments_pass_with_none_or_an_empty_object(string op)
    {
        var plain = Check(Request(op));
        Assert.True(plain.Ok, plain.Detail);
        Assert.Null(plain.Request!.Args);
        Assert.True(Check(Request(op, "{}")).Ok);
        Assert.True(Check(Request(op, "null")).Ok);
    }

    [Fact]
    public void Every_operation_has_a_valid_example()
    {
        var examples = new Dictionary<Operation, string?>
        {
            [Operation.ApplyLockPolicies] = """{"groups":["lock","taskmgr"]}""",
            [Operation.ReleaseLockPolicies] = """{"groups":["lock"]}""",
            [Operation.PauseServices] = """{"names":["WSearch"]}""",
            [Operation.ResumeServices] = """{"names":["WSearch","Spooler"]}""",
            [Operation.SetAutoLogon] = """{"password":"geheim","allowPasswordSignIn":true}""",
            [Operation.InstallWindowsUpdates] = null,
            [Operation.SetRemoteFirewall] = """{"ports":[8765,8766]}""",
            [Operation.InstallCouchHudUpdate] = SetupArgs("CouchHud-Setup-0.4.1.exe"),
            [Operation.EnrollPasscode] = $$"""{"hash":"{{Hash}}"}""",
            [Operation.ChangePasscode] = $$"""{"hash":"{{Hash}}"}""",
            [Operation.VerifyCode] = """{"kind":"parent","sequence":["a","b","x","y","up","down"]}""",
            [Operation.RequestWindowsGrant] = """{"scope":"unlock"}""",
            [Operation.RemovePasscode] = """{"kind":"parent"}""",
            [Operation.SetLockMode] = """{"mode":"code"}""",
            [Operation.RequestAdminApproval] = """{"ops":["SetAutoLogon"]}""",
            [Operation.GetJob] = """{"jobId":"0123456789abcdef0123456789abcdef"}""",
            [Operation.GetRecentEvents] = null
        };
        foreach (var op in Enum.GetValues<Operation>())
        {
            if (Check(Request(op.ToString())).Ok) continue;   // no arguments needed
            if (!examples.TryGetValue(op, out var args)) Assert.Fail($"no example for {op}");
            var result = Check(Request(op.ToString(), args));
            Assert.True(result.Ok, $"{op}: {result.Code} {result.Detail}");
            Assert.Equal(op, result.Request!.Operation);
        }
    }

    private static string SetupArgs(string name, long size = 1000) =>
        $$"""{"fileName":{{System.Text.Json.JsonSerializer.Serialize(name)}},"size":{{size}},"checksumFile":"{{Convert.ToBase64String(new byte[100])}}","signature":"{{Convert.ToBase64String(new byte[64])}}"}""";

    [Fact]
    public void The_arguments_arrive_typed()
    {
        var apply = Check(Request("ApplyLockPolicies", """{"groups":["lock"]}""")).Request!;
        Assert.Equal(["lock"], apply.GetArgs<ApplyLockPoliciesArgs>().Groups);
        var logon = Check(Request("SetAutoLogon", """{"password":"x"}""")).Request!.GetArgs<SetAutoLogonArgs>();
        Assert.Equal("x", logon.Password);
        Assert.False(logon.AllowPasswordSignIn);
        Assert.Equal(50, Check(Request("GetRecentEvents")).Request!.GetArgs<GetRecentEventsArgs>().Count);
        Assert.Null(Check(Request("InstallWindowsUpdates")).Request!.GetArgs<InstallWindowsUpdatesArgs>().Ids);
    }

    [Fact]
    public void A_grant_comes_along_when_it_looks_like_one()
    {
        var result = Check($$$"""{"v":1,"op":"ReleaseLockPolicies","grant":"{{{Grant}}}","args":{"groups":["lock"]}}""");
        Assert.True(result.Ok, result.Detail);
        Assert.Equal(Grant, result.Request!.Grant);
        Assert.True(Check("""{"v":1,"op":"Ping","grant":null}""").Ok);
    }

    [Fact]
    public void A_request_built_by_the_client_side_passes_the_validator()
    {
        Assert.True(Validator.Validate(ProtocolJson.SerializeRequest(Operation.Ping)).Ok);
        var bytes = ProtocolJson.SerializeRequest(Operation.SetRemoteFirewall, new SetRemoteFirewallArgs([8765]), Grant);
        var result = Validator.Validate(bytes);
        Assert.True(result.Ok, result.Detail);
        Assert.Equal([8765], result.Request!.GetArgs<SetRemoteFirewallArgs>().Ports);
    }

    [Fact]
    public void Responses_round_trip_through_the_source_generated_context()
    {
        var bytes = ProtocolJson.Serialize(Response<GrantData>.Success(new GrantData(Grant, "unlock", 120)));
        Assert.Contains("\"ok\":true", Encoding.UTF8.GetString(bytes));
        var back = ProtocolJson.DeserializeResponse<GrantData>(bytes)!;
        Assert.True(back.Ok);
        Assert.Equal(Protocol.Version, back.V);
        Assert.Equal(120, back.Data!.ExpiresInSeconds);

        var failure = ProtocolJson.DeserializeResponse(ProtocolJson.Serialize(Response.Failure(ErrorCodes.NeedGrant)))!;
        Assert.False(failure.Ok);
        Assert.Equal("needGrant", failure.Code);
        Assert.Contains(failure.Code, ErrorCodes.All);
    }

    // ---- negative ------------------------------------------------------------------------------------

    [Fact]
    public void Not_json_or_not_an_object_is_refused()
    {
        Rejected("", ErrorCodes.BadJson);
        Rejected("not json", ErrorCodes.BadJson);
        Rejected("[1,2]", ErrorCodes.BadJson);
        Rejected("\"Ping\"", ErrorCodes.BadJson);
        Rejected("""{"v":1,"op":"Ping",}""", ErrorCodes.BadJson);
        Rejected("""{"v":1,"op":"Ping"} trailing""", ErrorCodes.BadJson);
        Rejected("""{"v":1, /* c */ "op":"Ping"}""", ErrorCodes.BadJson);
        Rejected("""{"op":"Ping"}""", ErrorCodes.BadJson);
        Rejected("""{"v":"1","op":"Ping"}""", ErrorCodes.BadJson);
        Rejected("""{"v":1}""", ErrorCodes.BadJson);
        Rejected("""{"v":1,"op":5}""", ErrorCodes.BadJson);
        Rejected(new string('[', 100) + new string(']', 100), ErrorCodes.BadJson);
    }

    [Fact]
    public void Bytes_that_are_not_utf8_json_are_refused()
    {
        Assert.Equal(ErrorCodes.BadJson, Validator.Validate([0xFF, 0xFE, 0x00, 0x7B]).Code);
        Assert.Equal(ErrorCodes.BadJson, Validator.Validate([]).Code);
    }

    [Fact]
    public void Too_large_a_message_is_refused()
    {
        var big = $$$"""{"v":1,"op":"Ping","args":{"x":"{{{new string('a', Protocol.MaxMessageBytes)}}}"}}""";
        Rejected(big, ErrorCodes.TooLarge);
    }

    [Fact]
    public void Another_version_is_refused()
    {
        Rejected("""{"v":2,"op":"Ping"}""", ErrorCodes.BadVersion);
        Rejected("""{"v":0,"op":"Ping"}""", ErrorCodes.BadVersion);
    }

    [Theory]
    [InlineData("Format")]
    [InlineData("ping")]
    [InlineData("PING")]
    [InlineData("")]
    [InlineData("Ping ")]
    [InlineData("1")]
    public void An_unknown_operation_is_refused_also_when_the_case_differs(string op) =>
        Rejected($$"""{"v":1,"op":"{{op}}"}""", ErrorCodes.UnknownOperation);

    [Fact]
    public void Unknown_fields_are_refused_everywhere()
    {
        Rejected("""{"v":1,"op":"Ping","extra":1}""", ErrorCodes.UnknownField);
        Rejected("""{"v":1,"op":"Ping","sid":"S-1-5-21-1"}""", ErrorCodes.UnknownField);
        Rejected("""{"v":1,"op":"Ping","args":{"x":1}}""", ErrorCodes.BadArgument);
        Rejected(Request("ApplyLockPolicies", """{"groups":["lock"],"sid":"S-1-5-18"}"""), ErrorCodes.UnknownField);
        Rejected(Request("SetAutoLogon", """{"password":"x","user":"admin"}"""), ErrorCodes.UnknownField);
        Rejected(Request("SetRemoteFirewall", """{"ports":[8765],"program":"C:\\evil.exe"}"""), ErrorCodes.UnknownField);
        // Case counts: "Groups" is not "groups".
        Rejected(Request("ApplyLockPolicies", """{"Groups":["lock"]}"""), ErrorCodes.UnknownField);
        // The same field twice would let a parser that takes the last one hide the first.
        Rejected("""{"v":1,"op":"Ping","op":"ReleaseAllAccounts"}""", ErrorCodes.UnknownField);
        Rejected(Request("ApplyLockPolicies", """{"groups":["lock"],"groups":["taskmgr"]}"""), ErrorCodes.UnknownField);
    }

    [Fact]
    public void Missing_or_wrongly_typed_arguments_are_refused()
    {
        Rejected(Request("ApplyLockPolicies"), ErrorCodes.BadArgument);
        Rejected(Request("ApplyLockPolicies", "[]"), ErrorCodes.BadArgument);
        Rejected(Request("ApplyLockPolicies", "{}"), ErrorCodes.BadArgument);
        Rejected(Request("ApplyLockPolicies", """{"groups":"lock"}"""), ErrorCodes.BadArgument);
        Rejected(Request("ApplyLockPolicies", """{"groups":[1]}"""), ErrorCodes.BadArgument);
        Rejected(Request("SetRemoteFirewall", """{"ports":["8765"]}"""), ErrorCodes.BadArgument);
        Rejected(Request("SetRemoteFirewall", """{"ports":[8765.5]}"""), ErrorCodes.BadArgument);
        Rejected(Request("SetAutoLogon", """{"password":5}"""), ErrorCodes.BadArgument);
        Rejected(Request("SetAutoLogon", """{"password":"x","allowPasswordSignIn":"yes"}"""), ErrorCodes.BadArgument);
    }

    [Theory]
    [InlineData("""{"groups":[]}""")]
    [InlineData("""{"groups":["admin"]}""")]
    [InlineData("""{"groups":["lock","lock"]}""")]
    [InlineData("""{"groups":["LOCK"]}""")]
    [InlineData("""{"groups":["lock","taskmgr","lock"]}""")]
    [InlineData("""{"groups":[null]}""")]
    public void Policy_groups_come_from_the_positive_list(string args)
    {
        Rejected(Request("ApplyLockPolicies", args), ErrorCodes.BadArgument);
        Rejected(Request("ReleaseLockPolicies", args), ErrorCodes.BadArgument);
    }

    [Theory]
    [InlineData("""{"names":[]}""")]
    [InlineData("""{"names":["WSearch","WSearch"]}""")]
    [InlineData("""{"names":["TermService"]}""")]                    // not on the list of the caller
    [InlineData("""{"names":["WSearch & calc"]}""")]
    [InlineData("""{"names":["..\\sc"]}""")]
    [InlineData("""{"names":["WSearch\n"]}""")]
    [InlineData("""{"names":[""]}""")]
    [InlineData("""{"names":["a1","a2","a3","a4","a5","a6","a7","a8","a9","a10","a11","a12","a13","a14","a15","a16","a17"]}""")]
    public void Service_names_must_be_short_plain_and_allowed(string args)
    {
        Rejected(Request("PauseServices", args), ErrorCodes.BadArgument);
        Rejected(Request("ResumeServices", args), ErrorCodes.BadArgument);
    }

    [Theory]
    [InlineData("""{"ports":[]}""")]
    [InlineData("""{"ports":[80]}""")]
    [InlineData("""{"ports":[1023]}""")]
    [InlineData("""{"ports":[65536]}""")]
    [InlineData("""{"ports":[-1]}""")]
    [InlineData("""{"ports":[8765,8766,8767]}""")]
    [InlineData("""{"ports":[8765,8765]}""")]
    public void Firewall_ports_are_few_and_not_privileged(string args) =>
        Rejected(Request("SetRemoteFirewall", args), ErrorCodes.BadArgument);

    [Theory]
    [InlineData("")]
    [InlineData("\0")]
    public void An_empty_or_nul_password_is_refused(string password) =>
        Rejected(Request("SetAutoLogon", $$"""{"password":{{System.Text.Json.JsonSerializer.Serialize(password)}}}"""), ErrorCodes.BadArgument);

    [Fact]
    public void A_password_is_limited_in_length()
    {
        Assert.True(Check(Request("SetAutoLogon", $$"""{"password":"{{new string('p', RequestValidator.MaxPasswordChars)}}"}""")).Ok);
        Rejected(Request("SetAutoLogon", $$"""{"password":"{{new string('p', RequestValidator.MaxPasswordChars + 1)}}"}"""), ErrorCodes.BadArgument);
    }

    [Theory]
    [InlineData("""{"ids":["not-a-guid"]}""")]
    [InlineData("""{"ids":[null]}""")]
    [InlineData("""{"ids":[]}""")]
    public void Update_ids_must_be_guids(string args) => Rejected(Request("InstallWindowsUpdates", args), ErrorCodes.BadArgument);

    [Fact]
    public void Update_ids_pass_as_guids_and_are_limited_in_number()
    {
        var one = $$"""{"ids":["{{Guid.NewGuid()}}"]}""";
        Assert.True(Check(Request("InstallWindowsUpdates", one)).Ok);
        var many = string.Join(',', Enumerable.Range(0, RequestValidator.MaxUpdateIds + 1).Select(_ => $"\"{Guid.NewGuid()}\""));
        Rejected(Request("InstallWindowsUpdates", $$"""{"ids":[{{many}}]}"""), ErrorCodes.BadArgument);
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("..\\CouchHud-Setup-0.4.1.exe")]
    [InlineData("C:\\CouchHud-Setup-0.4.1.exe")]
    [InlineData("CouchHud-Setup-0.4.1.exe ")]
    [InlineData("CouchHud-Setup-0.4.1.msi")]
    [InlineData("CouchHud-Setup-latest.exe")]
    [InlineData("CouchHud-Setup-0.4.1.exe:stream")]
    public void The_setup_name_of_a_couchhud_update_follows_the_release_pattern(string name) =>
        Rejected(Request("InstallCouchHudUpdate", SetupArgs(name)), ErrorCodes.BadArgument);

    [Fact]
    public void The_removed_CaseHUD_update_is_an_unknown_operation()
    {
        // The no-UAC CaseHUD update is gone for good: its name is no operation any more, whatever the arguments look like.
        Assert.False(Enum.TryParse<Operation>("InstallCaseHud", out _));
        Rejected(Request("InstallCaseHud", SetupArgs("CaseHUD-Setup-1.2.3.exe")), ErrorCodes.UnknownOperation);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(400L * 1024 * 1024 + 1)]
    public void The_announced_size_of_a_setup_is_limited_to_400_MB(long size) =>
        Rejected(Request("InstallCouchHudUpdate", SetupArgs("CouchHud-Setup-0.4.1.exe", size)), ErrorCodes.BadArgument);

    [Fact]
    public void A_setup_of_exactly_400_MB_is_announced_fine()
    {
        Assert.True(Check(Request("InstallCouchHudUpdate", SetupArgs("CouchHud-Setup-0.4.1.exe", 400L * 1024 * 1024))).Ok);
    }

    [Fact]
    public void Checksum_file_and_signature_must_be_base64_of_a_sensible_size()
    {
        string With(string checksum, string signature) =>
            $$"""{"fileName":"CouchHud-Setup-0.4.1.exe","size":10,"checksumFile":"{{checksum}}","signature":"{{signature}}"}""";
        var ok = Convert.ToBase64String(new byte[10]);
        Assert.True(Check(Request("InstallCouchHudUpdate", With(ok, ok))).Ok);
        Rejected(Request("InstallCouchHudUpdate", With("", ok)), ErrorCodes.BadArgument);
        Rejected(Request("InstallCouchHudUpdate", With("%%%", ok)), ErrorCodes.BadArgument);
        Rejected(Request("InstallCouchHudUpdate", With(ok, Convert.ToBase64String(new byte[RequestValidator.MaxSignatureBytes + 1]))), ErrorCodes.BadArgument);
        Rejected(Request("InstallCouchHudUpdate", With(Convert.ToBase64String(new byte[RequestValidator.MaxChecksumBytes + 1]), ok)), ErrorCodes.BadArgument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pbkdf2-sha256$1$a$b")]
    [InlineData("pbkdf2-sha256$1000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("pbkdf2-sha256$1000000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("plain")]
    [InlineData("md5$100000$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void A_passcode_hash_has_the_form_of_passcode_hash(string hash)
    {
        Rejected(Request("EnrollPasscode", $$"""{"hash":"{{hash}}"}"""), ErrorCodes.BadArgument);
        Rejected(Request("ChangePasscode", $$"""{"hash":"{{hash}}"}"""), ErrorCodes.BadArgument);
    }

    [Theory]
    [InlineData("""{"kind":"admin","sequence":["a","a","a","a","a","a"]}""")]
    [InlineData("""{"kind":"couch","sequence":["a","a","a","a","a"]}""")]            // too short
    [InlineData("""{"kind":"couch","sequence":["a","a","a","a","a","a","a","a","a"]}""")]   // too long
    [InlineData("""{"kind":"couch","sequence":["a","a","a","a","a","start"]}""")]    // not a button
    [InlineData("""{"kind":"couch","sequence":["a","a","a","a","a",null]}""")]
    public void A_code_is_six_to_eight_known_buttons(string args) => Rejected(Request("VerifyCode", args), ErrorCodes.BadArgument);

    [Theory]
    [InlineData("""{"scope":"admin"}""")]
    [InlineData("""{"scope":""}""")]
    [InlineData("""{"scope":"UNLOCK"}""")]
    public void A_windows_grant_names_a_known_scope(string args) => Rejected(Request("RequestWindowsGrant", args), ErrorCodes.BadArgument);

    [Theory]
    [InlineData("""{"jobId":"../../x"}""")]
    [InlineData("""{"jobId":"0123"}""")]
    [InlineData("""{"jobId":"0123456789ABCDEF0123456789ABCDEF"}""")]
    [InlineData("""{"jobId":""}""")]
    public void A_job_id_is_32_hex_digits(string args) => Rejected(Request("GetJob", args), ErrorCodes.BadArgument);

    [Theory]
    [InlineData("""{"count":0}""")]
    [InlineData("""{"count":201}""")]
    [InlineData("""{"count":-1}""")]
    public void The_number_of_events_is_limited(string args) => Rejected(Request("GetRecentEvents", args), ErrorCodes.BadArgument);

    [Theory]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]       // 43 characters without the =
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA<AA=")]
    public void A_grant_must_look_like_32_bytes_in_base64(string grant) =>
        Rejected($$$"""{"v":1,"op":"ReleaseLockPolicies","grant":"{{{grant}}}","args":{"groups":["lock"]}}""", ErrorCodes.BadArgument);

    [Fact]
    public void A_grant_that_is_not_a_string_is_refused() =>
        Rejected("""{"v":1,"op":"Ping","grant":12}""", ErrorCodes.BadArgument);
}
