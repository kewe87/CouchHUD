using System.Net.NetworkInformation;
using CouchHud.Core.Library;
using CouchHud.Core.Platform;
using CouchHud.Sdk;

namespace CouchHud.Core.Plugins;

/// <summary>
/// The SDK 3 host services that are not just a callback: the programs CouchHUD's own plugins restrict on the locked couch, their changes
/// to the shell's autostart, and the checks before a plugin's command line runs. The Windows side (canonical paths, starting processes) is
/// handed in by the shell.
/// </summary>
public sealed class PluginHostServices
{
    public const int MaxArguments = 32;
    public const int MaxArgumentChars = 1024;
    public static readonly TimeSpan MaxCliTimeout = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan AdaptTimeout = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly Dictionary<string, string> _restricted = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Plugin, string Exe, Func<string, string?> Adapt)> _adapters = [];

    /// <summary>
    /// The canonical path of an installed program that may be named by a plugin: below Program Files, no links, existing; null otherwise.
    /// Set by the shell (TrustedCliRunner); without it nothing is accepted (fail closed).
    /// </summary>
    public Func<string, string?>? CanonicalTrustedExe { get; set; }

    /// <summary>Set by the shell: the canonical path of the program a running process belongs to (for the foreground guard).</summary>
    public Func<int, string?>? ProcessImage { get; set; }

    /// <summary>The canonical exe a plugin may name, or null (relative, not below Program Files, a link, missing, not an .exe).</summary>
    public string? Canonical(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || !TrustedCli.IsDrivePath(exe) || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || HasControl(exe)) return null;
        try
        {
            var canonical = CanonicalTrustedExe?.Invoke(exe);
            return canonical is not null && TrustedCli.IsDrivePath(canonical) ? canonical : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- lock.restrict

    /// <summary>Windows of this program are minimized on the locked couch (first-party plugins only; the capability gate is in the context).</summary>
    public bool Restrict(string pluginId, string exe)
    {
        if (Canonical(exe) is not { } canonical) return false;
        lock (_lock) _restricted[canonical] = pluginId;
        return true;
    }

    public IReadOnlyCollection<string> RestrictedExes
    {
        get
        {
            lock (_lock) return [.. _restricted.Keys];
        }
    }

    /// <summary>For the foreground guard: does this process belong to a program a plugin restricted?</summary>
    public bool IsRestrictedProcess(int pid)
    {
        lock (_lock)
        {
            if (_restricted.Count == 0) return false;
        }
        string? image;
        try
        {
            image = ProcessImage?.Invoke(pid);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        if (image is null) return false;
        lock (_lock) return _restricted.ContainsKey(image);
    }

    // ---------------------------------------------------------------- startup.args

    public bool AddStartupAdapter(string pluginId, string exe, Func<string, string?> adapt)
    {
        if (Canonical(exe) is not { } canonical) return false;
        lock (_lock)
        {
            _adapters.RemoveAll(a => a.Plugin == pluginId && a.Exe.Equals(canonical, StringComparison.OrdinalIgnoreCase));
            _adapters.Add((pluginId, canonical, adapt));
        }
        return true;
    }

    /// <summary>
    /// The shell's autostart adapter with the plugins' on top: for the entry whose program (canonical) a plugin registered, its arguments go
    /// through the plugin's function. Only the arguments: the program stays the one in the entry. A plugin that throws, takes longer than
    /// <see cref="AdaptTimeout"/> or answers with control characters or more than <see cref="MaxArgumentChars"/> changes nothing.
    /// </summary>
    public Func<string, string> ComposeStartup(Func<string, string>? inner, Func<string, string>? canonicalPath = null)
    {
        List<(string Plugin, string Exe, Func<string, string?> Adapt)> adapters;
        lock (_lock) adapters = [.. _adapters];
        return commandLine =>
        {
            var line = inner?.Invoke(commandLine) ?? commandLine;
            if (adapters.Count == 0) return line;
            try
            {
                var (file, arguments) = CommandLine.Split(line);
                var full = Path.GetFullPath(file);
                var normal = canonicalPath?.Invoke(full) ?? full;
                foreach (var (plugin, exe, adapt) in adapters)
                {
                    if (!normal.Equals(exe, StringComparison.OrdinalIgnoreCase)) continue;
                    var call = Task.Run(() => adapt(arguments));
                    if (!call.Wait(AdaptTimeout)) return line;
                    var result = call.Result;
                    if (result is null || result.Length > MaxArgumentChars || HasControl(result)) return line;
                    return "\"" + file + "\"" + (result.Length > 0 ? " " + result : "");
                }
            }
            catch (Exception ex) when (ex is ArgumentException or AggregateException or IOException or NotSupportedException)
            {
                Console.Error.WriteLine($"[plugins] startup adapter: {ex.Message}");
            }
            return line;
        };
    }

    // ---------------------------------------------------------------- process (TrustedCli)

    /// <summary>
    /// Why a plugin's command line may not run, or null: the exe must be one the user confirmed for this plugin (canonical, below Program
    /// Files), at most <see cref="MaxArguments"/> arguments without control characters, a timeout up to <see cref="MaxCliTimeout"/>, and on
    /// the locked couch only for CouchHUD's own plugins.
    /// </summary>
    public string? CheckCli(string exe, IReadOnlyList<string> arguments, TimeSpan timeout, PluginOrigin origin, bool locked, IReadOnlyCollection<string> confirmedExes, out string? canonical)
    {
        canonical = Canonical(exe);
        if (canonical is null) return "notTrusted";
        var c = canonical;
        if (!confirmedExes.Any(x => x.Equals(c, StringComparison.OrdinalIgnoreCase))) return "notConfirmed";
        if (locked && !PluginPolicy.IsOwn(origin)) return "locked";
        if (arguments.Count > MaxArguments || arguments.Any(a => a is null || a.Length > MaxArgumentChars || HasControl(a))) return "badArguments";
        if (timeout <= TimeSpan.Zero || timeout > MaxCliTimeout) return "badTimeout";
        return null;
    }

    internal static bool HasControl(string s) => s.Any(ch => char.IsControl(ch) || char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator or System.Globalization.UnicodeCategory.Format);
}

/// <summary>Read-only network state for plugins (System.Net.NetworkInformation, no native calls in the plugin).</summary>
public sealed class PluginNetworkState : IPluginNetwork
{
    /// <summary>One adapter as read from Windows, before the default route is picked.</summary>
    public sealed record Raw(string Id, string Name, string Description, bool Up, bool Tunnel, bool HasGateway);

    public IReadOnlyList<PluginNetworkAdapter> Adapters()
    {
        var raw = new List<Raw>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var gateway = false;
                try
                {
                    gateway = nic.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(System.Net.IPAddress.Any) && !g.Address.Equals(System.Net.IPAddress.IPv6Any));
                }
                catch (NetworkInformationException)
                {
                    // An adapter that cannot be read is simply not the default route.
                }
                raw.Add(new Raw(nic.Id, nic.Name, nic.Description, nic.OperationalStatus == OperationalStatus.Up, IsTunnel(nic.NetworkInterfaceType, nic.Description), gateway));
            }
        }
        catch (NetworkInformationException)
        {
            return [];
        }
        return Pick(raw);
    }

    /// <summary>
    /// Which adapter carries the default route. .NET does not expose route metrics, so: a tunnel that is up and has a gateway wins (VPN apps
    /// route everything through theirs), else the first adapter that is up and has a gateway. Best effort, read-only.
    /// </summary>
    public static IReadOnlyList<PluginNetworkAdapter> Pick(IReadOnlyList<Raw> raw)
    {
        var candidates = raw.Where(r => r.Up && r.HasGateway).ToList();
        var route = candidates.FirstOrDefault(r => r.Tunnel) ?? candidates.FirstOrDefault();
        return raw.Select(r => new PluginNetworkAdapter(r.Id, r.Name, r.Description, r.Up, r.Tunnel, ReferenceEquals(r, route))).ToList();
    }

    public static bool IsTunnel(NetworkInterfaceType type, string description) =>
        type is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp
        || description.Contains("TAP-", StringComparison.OrdinalIgnoreCase)
        || description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
        || description.Contains("Wintun", StringComparison.OrdinalIgnoreCase)
        || description.Contains("NordLynx", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A plugin's lines on the downloads page: its ids are prefixed with the plugin id, so plugins never touch each other's or CouchHUD's lines.</summary>
public sealed class PluginDownloads(DownloadCenter center, string pluginId, string pluginName) : IPluginDownloads
{
    private string Key(string id) => $"plugin:{pluginId}:{id}";

    public void Begin(string id, string label, bool isUpdate = false) => center.Begin(Key(id), DownloadKind.Plugin, pluginName, Clip(label), isUpdate);

    public void Report(string id, double? progress = null, string? detail = null, long? bytesDone = null, long? bytesTotal = null) =>
        center.Report(Key(id), progress, detail is null ? null : Clip(detail), bytesDone: bytesDone, bytesTotal: bytesTotal);

    public void Complete(string id, bool ok, string? detail = null) => center.Complete(Key(id), ok, detail is null ? null : Clip(detail));

    private static string Clip(string s)
    {
        var clean = new string(s.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length <= 120 ? clean : clean[..120] + "…";
    }
}
