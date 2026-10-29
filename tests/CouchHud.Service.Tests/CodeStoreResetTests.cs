using System.Text;
using CouchHud.Service.Authorization;
using CouchHud.Service.Contracts;
using CouchHud.Service.State;

namespace CouchHud.Service.Tests;

/// <summary>
/// ResetCodeStore through the dispatcher: only for an elevated administrator or an administrator's approval (UAC), only while the code
/// file is damaged, refused for every other caller and grant; the file is set aside, never deleted.
/// </summary>
public class CodeStoreResetTests
{
    private const string Damaged = "{\"version\":1,\"accounts\":{},\"extra\":1}";

    private static CallerInfo User => Callers.User();

    private static byte[] Reset(string? grant = null) => ProtocolJson.SerializeRequest(Operation.ResetCodeStore, grant: grant);

    private static string[] SetAside(CodeRig rig) => Directory.GetFiles(rig.Folder, CodeStore.FileName + ".broken-*");

    [Fact]
    public void The_policy_takes_an_administrator_only()
    {
        Assert.Equal(Requirement.ParentGrantOrAdmin, OperationPolicy.Table[Operation.ResetCodeStore].Requirement);
        Assert.Contains(StateCheck.CodeStoreDamaged, OperationPolicy.Table[Operation.ResetCodeStore].Checks);
        // Whatever the lock: no couch code, no Windows unlock, no parents' code; an administrator's approval only.
        Assert.Equal(GrantKinds.WindowsAdmin, OperationPolicy.AcceptedGrants(Operation.ResetCodeStore));
        foreach (var view in new[] { LockView.Closed, new LockView(LockModes.Windows, false, true), new LockView(LockModes.Off, false, false), new LockView(LockModes.Code, true, false) { Legacy = true } })
            Assert.Equal(GrantKinds.WindowsAdmin, OperationPolicy.AcceptedGrants(new ValidatedRequest(Operation.ResetCodeStore, null, null), view));
        Assert.Equal(RateBucket.CodeStoreReset, RateLimiter.BucketFor(Operation.ResetCodeStore));
    }

    [Theory]
    [InlineData("""{"v":1,"op":"ResetCodeStore","args":{"kind":"couch"}}""")]
    [InlineData("""{"v":1,"op":"ResetCodeStore","args":{"path":"C:\\Windows"}}""")]
    public void It_takes_no_arguments(string json) =>
        Assert.False(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes(json)).Ok);

    [Fact]
    public void It_can_be_asked_for_in_an_administrator_approval() =>
        Assert.True(new RequestValidator(_ => false).Validate(Encoding.UTF8.GetBytes("""{"v":1,"op":"RequestAdminApproval","args":{"ops":["ResetCodeStore"]}}""")).Ok);

    [Fact]
    public async Task A_user_without_approval_is_refused_and_nothing_is_set_aside()
    {
        using var rig = new CodeRig(Damaged);
        Assert.True(rig.Codes.IsDamaged);
        var response = (await rig.CallAsync(User, Reset())).Response;
        Assert.False(response.Ok);
        Assert.Equal(ErrorCodes.NeedGrant, response.Code);
        // Administrators group in the token but not elevated: no different.
        Assert.False((await rig.CallAsync(User with { IsAdminGroupEnabled = true }, Reset())).Response.Ok);
        Assert.True(rig.Codes.IsDamaged);
        Assert.Empty(SetAside(rig));
        Assert.Equal(Damaged, File.ReadAllText(Path.Combine(rig.Folder, CodeStore.FileName)));
    }

    [Fact]
    public async Task A_forged_or_foreign_grant_does_not_open_it()
    {
        using var rig = new CodeRig(Damaged);
        Assert.False((await rig.CallAsync(User, Reset("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"))).Response.Ok);
        // An administrator's approval for another operation is not one for the reset.
        var other = await rig.AdminGrantAsync(User, Operation.SetAutoLogon);
        Assert.NotNull(other);
        Assert.False((await rig.CallAsync(User, Reset(other))).Response.Ok);
        Assert.True(rig.Codes.IsDamaged);
        Assert.Empty(SetAside(rig));
    }

    [Fact]
    public async Task An_administrators_approval_resets_the_damaged_store_once()
    {
        using var rig = new CodeRig(Damaged);
        var grant = await rig.AdminGrantAsync(User, Operation.ResetCodeStore);
        Assert.NotNull(grant);
        var response = (await rig.CallAsync(User, Reset(grant))).Response;
        Assert.True(response.Ok, response.Code);
        Assert.False(rig.Codes.IsDamaged);
        Assert.True(rig.Codes.IsAvailable);
        Assert.Equal(Damaged, File.ReadAllText(Assert.Single(SetAside(rig))));
        Assert.Contains(rig.Log.Lines, l => l.Contains("[codes] the damaged code store was reset for", StringComparison.Ordinal));
        // The grant is used up, and a working store is not reset again even for an elevated administrator.
        Assert.False((await rig.CallAsync(User, Reset(grant))).Response.Ok);
        var again = (await rig.CallAsync(Callers.ElevatedAdmin(), Reset())).Response;
        Assert.False(again.Ok);
        Assert.Equal(ErrorCodes.Denied, again.Code);
        Assert.Single(SetAside(rig));
        // The codes can be set again afterwards.
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashA)).Ok);
    }

    [Fact]
    public async Task An_elevated_administrator_resets_without_a_grant()
    {
        using var rig = new CodeRig(Damaged);
        var response = (await rig.CallAsync(Callers.ElevatedAdmin(), Reset())).Response;
        Assert.True(response.Ok, response.Code);
        Assert.False(rig.Codes.IsDamaged);
        Assert.Single(SetAside(rig));
    }

    [Fact]
    public async Task A_working_store_is_never_reset_by_anyone()
    {
        using var rig = new CodeRig();
        Assert.True((await rig.LockWithCodeAsync(User, Codes.HashA)).Ok);
        var user = (await rig.CallAsync(User, Reset())).Response;
        Assert.False(user.Ok);
        var admin = (await rig.CallAsync(Callers.ElevatedAdmin(), Reset())).Response;
        Assert.False(admin.Ok);
        Assert.Equal(ErrorCodes.Denied, admin.Code);
        Assert.True(rig.Codes.Has(Callers.UserSid, CodeKind.Couch));
        Assert.Empty(SetAside(rig));
    }

    [Fact]
    public async Task The_status_tells_a_damaged_store()
    {
        using var rig = new CodeRig(Damaged);
        rig.Harness.State.CodesDamagedLookup = () => rig.Codes.IsDamaged;
        var (_, raw) = await rig.CallAsync(User, ProtocolJson.SerializeRequest(Operation.Status));
        Assert.True(ProtocolJson.DeserializeResponse<StatusData>(raw)!.Data!.CodesDamaged);
        Assert.True((await rig.CallAsync(Callers.ElevatedAdmin(), Reset())).Response.Ok);
        var (_, after) = await rig.CallAsync(User, ProtocolJson.SerializeRequest(Operation.Status));
        Assert.Null(ProtocolJson.DeserializeResponse<StatusData>(after)!.Data!.CodesDamaged);
    }
}
