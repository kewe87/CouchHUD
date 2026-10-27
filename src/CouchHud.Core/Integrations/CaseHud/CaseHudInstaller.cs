using System.Security.Cryptography;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;

namespace CouchHud.Core.Integrations.CaseHud;

/// <summary>Which release a confirmation was given for: tag, setup file and size.</summary>
public sealed record CaseHudReleaseIdentity(string Tag, string? SetupUrl, long SetupSize)
{
    public static CaseHudReleaseIdentity Of(CaseHudRelease r) => new(r.Tag, r.SetupUrl, r.SetupSize);
}

/// <summary>
/// One-time permission to install exactly one release. The host hands it out when it looked at GitHub (and the UI showed the
/// user that very version); install and update accept nothing else, so a missing, used up, expired or foreign token stops them,
/// and so does a release that changed between the dialog and the install.
/// </summary>
public sealed class CaseHudInstallGate(Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, (CaseHudReleaseIdentity Release, DateTimeOffset Expires)> _tokens = [];
    private readonly object _lock = new();
    private DateTimeOffset Now => (now ?? (() => DateTimeOffset.UtcNow))();

    public string Issue(CaseHudRelease release)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_lock)
        {
            // Only the newest permission counts.
            _tokens.Clear();
            _tokens[token] = (CaseHudReleaseIdentity.Of(release), Now + Lifetime);
        }
        return token;
    }

    /// <summary>The release the token was issued for (the token is used up), or null.</summary>
    public CaseHudReleaseIdentity? TryConsume(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        lock (_lock)
        {
            if (!_tokens.Remove(token, out var entry)) return null;
            return entry.Expires >= Now ? entry.Release : null;
        }
    }
}

/// <summary>What the page needs to ask the user before installing: the concrete version and whether it is only checksum-protected.</summary>
public sealed record CaseHudPrepare(string? Token, CaseHudRelease? Release, bool Installed, bool IsUpdate, bool Unsigned, string? Error);

/// <summary>Runs the verified setup elevated (the shell checks the real path of the file and shows UAC). Returns the exit code.</summary>
public interface ICaseHudSetupRunner
{
    /// <exception cref="SetupDeclinedException">The user said no to the administrator prompt.</exception>
    Task<int> RunAsync(VerifiedSetup setup, string arguments, Action<string> step, CancellationToken ct);
}

public sealed class SetupDeclinedException() : Exception(Strings.Get("casehud.installDeclined"));

/// <summary>
/// The install and update flow of CaseHUD, with everything that touches Windows behind delegates (so it can be tested):
/// token → fresh look at the release (same release? really newer?) → download and verification → quit CaseHUD → setup with UAC →
/// start CaseHUD again. CaseHUD is started again when the setup is declined or fails, if it ran before.
/// After a first installation (not an update: that would overrule a choice made in CaseHUD since) and only when the user chose it,
/// <paramref name="enableApi"/> switches CaseHUD's API on in the user's session (<see cref="ICaseHudApiSwitch"/>); the setup itself
/// never gets <c>/API=1</c>, it runs elevated and would set it for the wrong account. A failure there does not fail the installation.
/// </summary>
public sealed class CaseHudInstaller(
    CaseHudReleaseService releases,
    CaseHudInstallGate gate,
    Func<CaseHudInstall?> findInstall,
    Func<CaseHudClient?> client,
    ICaseHudSetupRunner runner,
    Func<Task> start,
    Func<bool> gameRunning,
    Func<(bool Autostart, bool Fps)> options,
    string tempDir,
    Action<string?> busy,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<bool>? apiAfterInstall = null,
    Func<CancellationToken, Task>? enableApi = null)
{
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    /// <summary>Looks at GitHub (fresh) and hands out the token for what it found, if that can be installed.</summary>
    public async Task<CaseHudPrepare> PrepareAsync(CancellationToken ct = default)
    {
        var install = findInstall();
        var check = await releases.CheckAsync(install?.Version, force: true, ct);
        var unsigned = CaseHudPolicy.NeedsUnsignedConfirmation(releases.PublicKeyPem);
        var release = check.Release;
        if (release is null) return new CaseHudPrepare(null, null, install is not null, false, unsigned, check.Error ?? Strings.Get("casehud.noRelease"));
        var isUpdate = install is not null && check.UpdateAvailable;
        var versionUnknown = install is not null && !CaseHudVersion.IsReadable(install.Version);
        if (!release.Installable)
            return new CaseHudPrepare(null, release, install is not null, isUpdate, unsigned, CaseHudIntegrity.ProblemText(release));
        // Installed and not older than the release: nothing to do (no token for a downgrade or the same version).
        if (install is not null && !isUpdate && !versionUnknown) return new CaseHudPrepare(null, release, true, false, unsigned, null);
        // Offered only with a valid signature.
        try { await releases.VerifySignatureAsync(release, ct); }
        catch (CatalogException ex)
        {
            Console.Error.WriteLine($"[casehud] rejected: {ex.Message}");
            return new CaseHudPrepare(null, release, install is not null, isUpdate, unsigned, ex.Message);
        }
        return new CaseHudPrepare(gate.Issue(release), release, install is not null, isUpdate, unsigned, null);
    }

    /// <returns>The installed release.</returns>
    public async Task<CaseHudRelease> InstallAsync(string? token, CancellationToken ct = default)
    {
        if (gameRunning()) throw new InvalidOperationException(Strings.Get("selfupdate.notInGame"));
        var confirmed = gate.TryConsume(token) ?? throw new InvalidOperationException(Strings.Get("casehud.tokenInvalid"));
        if (!await _running.WaitAsync(0, ct)) throw new InvalidOperationException(Strings.Get("casehud.installedOnlyOnce"));
        var wasRunning = client() is not null;
        var quit = false;
        try
        {
            busy(Strings.Get("casehud.checking"));
            var install = findInstall();
            var check = await releases.CheckAsync(install?.Version, force: true, ct);
            var release = check.Release ?? throw new InvalidOperationException(check.Error ?? Strings.Get("casehud.noRelease"));
            if (!release.Installable) throw new InvalidOperationException(CaseHudIntegrity.ProblemText(release));
            // The release the user was shown, and a real update (a first installation has nothing to compare with).
            if (CaseHudReleaseIdentity.Of(release) != confirmed) throw new InvalidOperationException(Strings.Get("casehud.releaseChanged"));
            if (install is not null && CaseHudVersion.IsReadable(install.Version) && !CaseHudVersion.IsUpdate(release, CaseHudVersion.Parse(install.Version)))
                throw new InvalidOperationException(Strings.Get("casehud.notNewer"));

            busy(Strings.Get("selfupdate.downloading", release.Version));
            VerifiedSetup setup;
            try
            {
                PathSafety.PrepareCleanDirectory(tempDir);
                setup = await releases.DownloadAsync(release, tempDir, ct);
            }
            catch (CatalogException ex)
            {
                Console.Error.WriteLine($"[casehud] rejected: {ex.Message}");
                throw new InvalidOperationException(ex.Message);
            }

            using (setup)
            {
                // Only now, with a verified setup in hand and right before the setup replaces files: let CaseHUD end itself.
                if (client() is { } running)
                {
                    quit = true;
                    await running.QuitAsync(ct);
                    for (var i = 0; i < 40 && client() is not null; i++) await _delay(TimeSpan.FromMilliseconds(250), ct);
                }

                var (autostart, fps) = options();
                int exit;
                try
                {
                    exit = await runner.RunAsync(setup, CaseHudPolicy.InstallArguments(autostart, fps), text => busy(text.Replace("{version}", release.Version.ToString())), ct);
                }
                catch (SetupDeclinedException)
                {
                    throw new InvalidOperationException(Strings.Get("casehud.installDeclined"));
                }
                if (exit != 0) throw new InvalidOperationException(Strings.Get("casehud.installFailed", exit));
            }

            if (install is null && enableApi is not null && apiAfterInstall?.Invoke() == true)
            {
                busy(Strings.Get("casehud.apiEnabling"));
                try { await enableApi(ct); }
                catch (Exception ex) when (ex is not OperationCanceledException) { Console.Error.WriteLine($"[casehud] api switch after install: {ex.GetType().Name}"); }
            }

            busy(null);
            // An installation or update leaves CaseHUD in the tray, ready for the page.
            try { await start(); }
            catch (CaseHudException) { }
            return release;
        }
        catch
        {
            // Declined or failed: CaseHUD was closed for nothing, bring it back.
            if (quit && wasRunning)
            {
                try { await start(); }
                catch (CaseHudException) { }
            }
            throw;
        }
        finally
        {
            busy(null);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            _running.Release();
        }
    }
}
