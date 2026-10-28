using System.ComponentModel;
using System.Diagnostics;
using CouchHud.Core.Config;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;
using CouchHud.Core.Plugins;
using CouchHud.Core.Updates;

namespace CouchHud.Shell;

/// <summary>
/// The host side of <c>IPluginContext.RunSetupAsync</c> (capability "setup.run", granted to CouchHUD's own plugins only): a setup that a
/// first-party plugin downloaded and verified is handed to Windows. CouchHUD does not trust the plugin's word for it:
/// <list type="bullet">
/// <item>the couch is unlocked and no child's profile is active, and a person pressed something: the host set a one-shot token (bound to the plugin id,
/// short-lived) when a row or guide menu entry of that plugin that asks for the parents' code was pressed, after its gates passed; the hand-over
/// consumes it and is refused without one, and only one setup runs at a time;</item>
/// <item>the file is an .exe in this plugin's own data folder (plugin-dataid, never another plugin's), named by its normal full path, alone in its
/// folder; the SHA-256 the plugin names is 64 hex digits;</item>
/// <item>the file is opened for reading with no writer allowed and held until the setup ended, hashed again against the plugin's hash, its
/// real path (links and short names resolved) is the one named and no folder on the way is a reparse point;</item>
/// <item>it is started with the "runas" verb, so the Windows admin prompt always asks (and a refusal there is a "no", not an error), with the
/// system folder as working directory, not waited for longer than ten minutes, and nothing about the program is logged but the plugin's name.</item>
/// </list>
/// Updating CaseHUD is not done here: the no-UAC update through the service was left out of the plugin on purpose (a plugin never gets a
/// privileged power). The first installation of a program stays what it was: Windows' prompt for a person who is at the PC.
/// </summary>
public sealed partial class Hud
{
    private readonly PluginSetupPress _setupPress = new();
    private readonly SemaphoreSlim _setupBusy = new(1, 1);

    /// <summary>Only a first-party plugin that holds setup.run gets a token for its press (not every first-party row that asks for the parents' code).</summary>
    private bool PressPaysForSetup(string pluginId) =>
        plugins.GrantOf(pluginId) is { Origin: PluginOrigin.FirstParty } grant && grant.Capabilities.Contains(CouchHud.Sdk.PluginCapabilities.SetupRun);

    private async Task<string?> RunPressedAsync(string pluginId, CouchHud.Sdk.QuickAction action)
    {
        try
        {
            return await CouchHud.Core.Plugins.PluginManager.SafeAsync($"Plugin {pluginId}", action.Run);
        }
        finally
        {
            _setupPress.Clear(pluginId);
        }
    }

    public async Task<bool> RunPluginSetupAsync(string pluginId, string pluginName, string path, string sha256, CancellationToken cancellationToken)
    {
        RefuseWhileConfigFaulted();
        if (Locked) throw new LockedException(Strings.Get("lock.unlockFirst"));
        RequireUnlocked();
        var c = config.Current;
        if (ProfileSwitch.IsChild(c, c.ActiveProfile.Id)) throw new InvalidOperationException(Strings.Get("plugin.gate.child"));
        // Without a press of that plugin's own row (or entry) there is no setup, whatever the plugin calls: one use per press.
        if (!_setupPress.TryConsume(pluginId))
        {
            Console.Error.WriteLine($"[plugins] {pluginName}: setup refused (no press)");
            return false;
        }
        if (!await _setupBusy.WaitAsync(0, cancellationToken))
        {
            Console.Error.WriteLine($"[plugins] {pluginName}: setup refused (another one runs)");
            return false;
        }
        try
        {
            return await RunPluginSetupCoreAsync(pluginId, pluginName, path, sha256, cancellationToken);
        }
        finally
        {
            _setupBusy.Release();
        }
    }

    private async Task<bool> RunPluginSetupCoreAsync(string pluginId, string pluginName, string path, string sha256, CancellationToken cancellationToken)
    {
        var refusal = PluginSetupRules.Refuse(path, sha256, Path.Combine(dataDir, "plugin-data", pluginId)) ?? (PluginSetupRules.FolderHoldsOnlyTheSetup(path) ? null : "folder");
        if (refusal is not null)
        {
            Console.Error.WriteLine($"[plugins] {pluginName}: setup refused ({refusal})");
            return false;
        }
        FileStream file;
        try
        {
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[plugins] {pluginName}: setup not readable");
            return false;
        }
        using (file)
        {
            try
            {
                if (!PluginSetupRules.HashMatches(file, sha256))
                {
                    Console.Error.WriteLine($"[plugins] {pluginName}: setup does not match its hash, not run");
                    return false;
                }
                PathSafety.RequireSamePath(path, CouchHud.Privileged.SetupProbe.FinalPath(file.SafeFileHandle));
                PathSafety.RequireNoReparsePoints(path);
            }
            catch (Exception ex) when (ex is CatalogException or IOException)
            {
                Console.Error.WriteLine($"[plugins] {pluginName}: setup path rejected");
                return false;
            }
            // Once more, right before the start: the handle is open and the hash matched, and the folder still holds nothing but the setup.
            if (!PluginSetupRules.FolderHoldsOnlyTheSetup(path))
            {
                Console.Error.WriteLine($"[plugins] {pluginName}: setup refused (folder)");
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Process process;
            try
            {
                Console.WriteLine($"[plugins] {pluginName} runs a downloaded setup (hash checked)");
                process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Environment.SystemDirectory }) ?? throw new InvalidOperationException(Strings.Get("selfupdate.setupNotStarted"));
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // The prompt was declined.
                return false;
            }
            using (process)
            {
                var ended = await Task.Run(() => process.WaitForExit(PluginSetupRules.MaxRun), cancellationToken);
                if (!ended)
                {
                    Console.Error.WriteLine($"[plugins] {pluginName}: setup still running after the time limit, not waited for any longer");
                    return false;
                }
                return process.ExitCode == 0;
            }
        }
    }
}
