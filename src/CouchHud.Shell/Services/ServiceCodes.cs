using CouchHud.Core.Config;
using CouchHud.Core.Localization;
using CouchHud.Core.Security;
using CouchHud.Service.Contracts;
using CouchHud.ServiceClient;
using ServiceCodeState = CouchHud.ServiceClient.HeldCodes;

namespace CouchHud.Shell.Services;

/// <summary>How the confirmation of a lock of 0.4.0 with a code went (<see cref="ServiceCodes.ConfirmLegacyCodeAsync"/>).</summary>
public enum LegacyConfirm
{
    /// <summary>An administrator confirmed it as a new code: the service holds the lock with the code just entered, and nothing else.</summary>
    Confirmed,
    /// <summary>
    /// Confirmed like <see cref="Confirmed"/>, with the old code of 0.4.0: that code lay readable in the configuration, so the HUD asks for a
    /// new one in the settings.
    /// </summary>
    ConfirmedOldCode,
    /// <summary>Nothing changed (no lock of 0.4.0, UAC cancelled, the service refused).</summary>
    NotConfirmed,
    /// <summary>
    /// The code does not match the stored code of 0.4.0, or there is none stored (missing, only the marker); nothing was asked. The HUD
    /// offers the administrator's confirmation.
    /// </summary>
    Mismatch
}

/// <summary>
/// The codes and the lock mode with the CouchHUD service (P9): the service keeps them and checks the buttons; the config keeps only
/// <see cref="Passcode.HeldByService"/> (the UI needs to know that a code exists, nothing more). Without a service by design (an unprotected
/// or development copy) the config keeps the hash as before and the shell checks it itself.
/// </summary>
public static class ServiceCodes
{
    /// <summary>Whether codes go through the service (the installed copy).</summary>
    public static bool Expected => AdminService.Ops.Expected;

    /// <summary>What the service said last about this account's codes and mode (settings page, setup assistant); null: not asked yet.</summary>
    public static ServiceCodeState? Known { get; private set; }

    /// <summary>Asks the service which codes it holds (in the background of the service watch).</summary>
    public static async Task<ServiceCodeState?> RefreshAsync()
    {
        if (!Expected) return null;
        var codes = await AdminService.Ops.CodeStatusAsync().ConfigureAwait(false);
        if (codes is not null) Known = codes;
        return codes;
    }

    /// <summary>The service holds a code of its own, so the local one was not taken over (the settings and the setup assistant say so).</summary>
    public static bool Conflict { get; private set; }

    /// <summary>
    /// The one-time migration of the local hash and lock mode (see <see cref="CodeFlow.MigrateAsync"/>). The service cannot know whether a
    /// program of the user changed the config before; it takes it once per account, only a hash in the format of <see cref="Passcode.Hash"/>,
    /// and never over a code or a lock it has. Taken: the config keeps only the marker. A conflict (the service has its own) is reported, not
    /// treated as success (review L-c): the local hash stays, unlocking goes by the service. Nothing to migrate counts as done.
    /// A lock of 0.4.0 the service has not had confirmed yet (<see cref="HeldCodes.LegacyLock"/>) takes nothing of the config by itself
    /// (security review H-1: a child could have written its own code into the config): "needs approval", no conflict shown; the next code
    /// entry or Windows sign-in asks an administrator (<see cref="ConfirmLegacyCodeAsync"/>, <see cref="ConfirmLegacyWindowsAsync"/>).
    /// </summary>
    public static async Task<MigrationResult> MigrateAsync(ConfigStore config)
    {
        if (!Expected) return MigrationResult.NotYet;
        // While config.json is unusable the store runs on its stand-in (ConfigStore.FaultConfig, Windows lock): that is not the user's
        // choice and must not become the service's lock mode. Taken up once the file works again (or was restored from its .bak).
        if (config.Fault is not null) return MigrationResult.NotYet;
        var hash = config.Current.PasscodeHash;
        var mode = config.Current.CouchLock;
        var local = Passcode.IsLocalHash(hash);
        if (!local && mode != LockModes.Windows) return MigrationResult.Done;
        var held = await RefreshAsync().ConfigureAwait(false);
        if (held is null) return MigrationResult.NotYet;
        if (held.LegacyLock)
        {
            Console.WriteLine("[codes] local code and lock mode stay: the lock of 0.4.0 waits for an administrator's confirmation");
            return MigrationResult.NeedsApproval;
        }
        var result = await CodeFlow.MigrateAsync(AdminService.Ops, local ? hash : null, mode).ConfigureAwait(false);
        Console.WriteLine($"[codes] local code and lock mode to the service: {result}");
        if (result == MigrationResult.Conflict) Conflict = true;
        if (result == MigrationResult.Done && local && mode != LockModes.Windows)
        {
            config.Update(c =>
            {
                if (c.PasscodeHash == hash) c.PasscodeHash = Passcode.HeldByService;
            });
        }
        return result;
    }

    /// <summary>
    /// A lock of 0.4.0 the service has not had confirmed (<see cref="HeldCodes.LegacyLock"/>), opened with a code (security review H-1, second
    /// round): an administrator confirms through UAC that <b>the code just entered</b> is the lock's code; the service then holds exactly that
    /// one (the code lock), and every code the account had before (couch and parents', planted or not) is gone. A code that does not match
    /// the config's code of 0.4.0 asks nothing by itself (<see cref="LegacyConfirm.Mismatch"/>: a mistyped one must not bring up UAC on
    /// every try); only when the person then chooses it explicitly (<paramref name="asNewCode"/>, the HUD's dialog "confirm as an
    /// administrator") an administrator may confirm it as the lock's new code: the config's code may have been changed by a program of
    /// the account, and the parents must still get in with the code they know (third security review).
    /// </summary>
    public static async Task<LegacyConfirm> ConfirmLegacyCodeAsync(ConfigStore config, IReadOnlyList<string> sequence, bool asNewCode = false)
    {
        if (!Expected) return LegacyConfirm.NotConfirmed;
        // On the stand-in of unusable settings nothing is changed in the service: the config could not follow (service and config apart).
        if (config.Fault is not null) return LegacyConfirm.NotConfirmed;
        if (await RefreshAsync().ConfigureAwait(false) is not { LegacyLock: true }) return LegacyConfirm.NotConfirmed;
        var stored = config.Current.PasscodeHash;
        // Only a real stored hash that verifies counts as the old code; nothing stored (or the marker) asks first like a mismatch (review 4),
        // so no entry at all brings up UAC by itself.
        var matches = Passcode.MatchesLocal(sequence, stored);
        if (!matches && !asNewCode) return LegacyConfirm.Mismatch;
        var hash = Passcode.Hash(sequence);
        var reason = matches ? AdminService.LegacyCodeReason : AdminService.LegacyNewCodeReason;
        var result = await AdminService.WithLegacyApprovalAsync(Operation.SetLockMode, reason,
            () => AdminService.Ops.SetLockModeAsync(LockModes.Code, hash, CodeKinds.Couch)).ConfigureAwait(false);
        Console.WriteLine($"[codes] lock of 0.4.0 with the code just entered ({reason}), confirmed by an administrator: {result}");
        if (!result.Ok) return LegacyConfirm.NotConfirmed;
        config.Update(c =>
        {
            if (Passcode.IsLocalHash(c.PasscodeHash)) c.PasscodeHash = Passcode.HeldByService;
        });
        Conflict = false;
        if (await RefreshAsync().ConfigureAwait(false) is not { LegacyLock: false }) return LegacyConfirm.NotConfirmed;
        return matches ? LegacyConfirm.ConfirmedOldCode : LegacyConfirm.Confirmed;
    }

    /// <summary>
    /// The same for a lock of 0.4.0 opened with the Windows sign-in: an administrator confirms that <b>the Windows sign-in</b> is the lock (the
    /// Windows mode); every code the account had before goes. True when the lock is the service's own now. False without asking anything
    /// while the settings are unusable (the stand-in): the service's mode is not changed then, the config could not follow it.
    /// </summary>
    public static async Task<bool> ConfirmLegacyWindowsAsync(ConfigStore config)
    {
        if (!Expected) return false;
        if (config.Fault is not null) return false;
        if (await RefreshAsync().ConfigureAwait(false) is not { LegacyLock: true }) return false;
        var result = await AdminService.WithLegacyApprovalAsync(Operation.SetLockMode, AdminService.LegacyWindowsReason,
            () => AdminService.Ops.SetLockModeAsync(LockModes.Windows)).ConfigureAwait(false);
        Console.WriteLine($"[codes] lock of 0.4.0 with the Windows sign-in, confirmed by an administrator: {result}");
        return result.Ok && await RefreshAsync().ConfigureAwait(false) is { LegacyLock: false };
    }

    /// <summary>
    /// The lock mode changes (P9 review H1): raising is free; lowering needs the current mode's grant (the unlock just done), the parents'
    /// approval or, as the fallback, an administrator's. Throws with the reason when the service did not take it.
    /// </summary>
    public static async Task SetModeAsync(string mode, IReadOnlyList<string>? sequence = null, bool parental = false)
    {
        // From no lock to the code lock the code comes along in the same step (a code enrolled on its own would not survive the switch).
        var hash = sequence is null ? null : Passcode.Hash(sequence);
        var kind = parental ? CodeKinds.Both : CodeKinds.Couch;
        var result = await AdminService.WithGrantAsync(Operation.SetLockMode, () => AdminService.Ops.SetLockModeAsync(mode, hash, kind)).ConfigureAwait(false);
        Console.WriteLine($"[codes] lock mode {mode}: {result}");
        if (!result.Ok) throw new InvalidOperationException(Strings.Get("codes.modeNotStored", await RefusalAsync(result, hash is not null).ConfigureAwait(false)));
        if (hash is not null) Conflict = false;
    }

    /// <summary>
    /// Mode and new code in one step. False (nothing changed) when the service refused it only because it already holds an approved code
    /// (the one-step switch is for a lock with no approved code); every other refusal throws with its reason.
    /// </summary>
    public static async Task<bool> TrySetModeWithCodeAsync(string mode, IReadOnlyList<string> sequence, bool parental)
    {
        var hash = Passcode.Hash(sequence);
        var kind = parental ? CodeKinds.Both : CodeKinds.Couch;
        var result = await AdminService.WithGrantAsync(Operation.SetLockMode, () => AdminService.Ops.SetLockModeAsync(mode, hash, kind)).ConfigureAwait(false);
        Console.WriteLine($"[codes] lock mode {mode} with a new code: {result}");
        var held = result.Code == ErrorCodes.Denied ? await RefreshAsync().ConfigureAwait(false) : null;
        switch (CodeFlow.ClassifyTogether(result, held))
        {
            case CodeFlow.TogetherOutcome.Done:
                Conflict = false;
                return true;
            case CodeFlow.TogetherOutcome.Fallback:
                return false;
        }
        throw new InvalidOperationException(Strings.Get("codes.modeNotStored", await RefusalAsync(result, true).ConfigureAwait(false)));
    }

    /// <summary>
    /// The reason shown for a refusal. A code that came along and was refused ("denied") is told apart by what the service holds: a code from
    /// before is still stored (a new one never comes along next to it), or a parents' code is. Nothing else of the service is revealed.
    /// </summary>
    private static async Task<string> RefusalAsync(OpResult result, bool withCode)
    {
        await ThrowIfDamagedAsync(result).ConfigureAwait(false);
        if (!withCode || result.Code != ErrorCodes.Denied) return AdminService.Message(result);
        var held = await RefreshAsync().ConfigureAwait(false) ?? Known;
        return CodeFlow.RefusalHint(held) switch
        {
            "codeHeld" => Strings.Get("codes.deniedCodeHeld"),
            "parentHeld" => Strings.Get("codes.deniedParentHeld"),
            _ => AdminService.Message(result)
        };
    }

    /// <summary>
    /// A refusal because the service's code file is damaged (it says so in Status, see <see cref="CodeFlow.RefusedForDamage"/>) is said as
    /// what it is, with the way out (an administrator resets it in the settings), instead of the generic "the service refused it".
    /// </summary>
    private static async Task ThrowIfDamagedAsync(OpResult result)
    {
        if (result.Ok || result.Code is not (ErrorCodes.Denied or ErrorCodes.Unavailable)) return;
        if (CodeFlow.RefusedForDamage(result, await RefreshAsync().ConfigureAwait(false))) throw new CodeStoreDamagedException(Strings.Get("codes.storeDamaged"));
    }

    /// <summary>Whether the service reported its code file as damaged when it was asked last.</summary>
    public static bool Damaged => Known?.Damaged == true;

    /// <summary>Before a change of the codes or the lock: asks the service, and a damaged code file is said at once (nothing is tried).</summary>
    public static async Task ThrowIfStoreDamagedAsync()
    {
        if (!Expected) return;
        if (await RefreshAsync().ConfigureAwait(false) is { Damaged: true }) throw new CodeStoreDamagedException(Strings.Get("codes.storeDamaged"));
    }

    /// <summary>
    /// The administrator's reset of a damaged code file: the service sets the file aside and starts with no codes. Windows' UAC prompt is the
    /// gate (the service accepts an administrator's approval only). Afterwards the lock mode this HUD keeps is raised again in the service
    /// (raising is free), so a lock that was on stays on; a code lock then needs its code set again (the caller says so). Throws with the
    /// reason when it did not happen.
    /// </summary>
    public static async Task ResetDamagedAsync(string couchLock)
    {
        var result = await AdminService.WithGrantAsync(Operation.ResetCodeStore, () => AdminService.Ops.ResetCodeStoreAsync()).ConfigureAwait(false);
        Console.WriteLine($"[codes] damaged code store reset: {result}");
        if (!result.Ok) throw new InvalidOperationException(Strings.Get("codes.resetFailed", AdminService.Message(result)));
        if (LockModes.Rank(couchLock) > 0)
        {
            var raised = await AdminService.Ops.SetLockModeAsync(couchLock).ConfigureAwait(false);
            Console.WriteLine($"[codes] lock mode {couchLock} set again after the reset: {raised}");
        }
        Conflict = false;
        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// A code goes from the service (the couch lock or the parental controls are switched off). Nothing to remove counts as done. Throws with the
    /// reason when the service refused (the switch then does not happen).
    /// </summary>
    public static async Task RemoveAsync(string kind)
    {
        var result = await AdminService.WithGrantAsync(Operation.RemovePasscode, () => AdminService.Ops.RemovePasscodeAsync(kind)).ConfigureAwait(false);
        Console.WriteLine($"[codes] {kind} code removed from the service: {result}");
        // A damaged code file refuses with "denied" too: never taken as "nothing to remove".
        await ThrowIfDamagedAsync(result).ConfigureAwait(false);
        // "denied" from the state check: there is no such code.
        if (!result.Ok && result.Code != ErrorCodes.Denied) throw new InvalidOperationException(Strings.Get("codes.notRemoved", AdminService.Message(result)));
    }

    /// <summary>
    /// A new code: as the couch code and, with parental controls, as couch and parents' code in one step (the HUD uses one code for both).
    /// Throws with the reason when the service did not take it.
    /// </summary>
    public static async Task StoreAsync(IReadOnlyList<string> sequence, bool parental)
    {
        var result = await CodeFlow.StoreAsync(AdminService.Ops, Passcode.Hash(sequence), parental, AdminService.WithGrantAsync).ConfigureAwait(false);
        Console.WriteLine($"[codes] new code to the service: {result}");
        await ThrowIfDamagedAsync(result).ConfigureAwait(false);
        if (!result.Ok) throw new InvalidOperationException(Strings.Get("codes.notStored", AdminService.Message(result)));
        Conflict = false;
    }

}

/// <summary>
/// The service's code file is damaged: no code is checked or stored until an administrator resets it. The bridge tells the page by a flag, and
/// the page offers the reset (<see cref="ServiceCodes.ResetDamagedAsync"/>).
/// </summary>
public sealed class CodeStoreDamagedException(string message) : InvalidOperationException(message);
