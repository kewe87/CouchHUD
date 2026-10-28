using System.Net;
using System.Text;

namespace CouchHud.Core.Browser;

/// <summary>
/// The couch browser's rules for what a web page may do, as pure functions (the WebView2 wiring lives in the Shell's BrowserPane).
/// Web content is untrusted: it never reaches the HUD's bridge, never opens a window of its own, gets no permission, downloads only
/// after a question in the HUD and only into the Downloads folder, and its dialogs are the HUD's (never a Windows window that would
/// take the controller away).
/// </summary>
public static class BrowserPolicy
{
    /// <summary>Web pages and the browser's own pages; the browser's error page ("chrome-error") must show, otherwise a failed load stays blank.</summary>
    public static bool IsNavigable(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "about" or "data" or "blob" or "chrome-error";

    /// <summary>A page asks for a new window (window.open, target=_blank): there is one screen on the couch, so it opens in the page itself,
    /// and only when the user clicked (pop-unders and ad windows without a click are dropped). Null: dropped.</summary>
    public static string? PopupTarget(string? url, bool userInitiated) =>
        userInitiated && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 ? uri.AbsoluteUri : null;

    /// <summary>Camera, microphone, location, notifications, clipboard, MIDI, sensors …: never (a couch PC has no one to ask in time).</summary>
    public static bool AllowPermission(string kind) => false;

    public const int MaxDialogText = 300;

    /// <summary>A page's dialog text for the HUD: no control or direction characters (no fake line layout, no reversed file names), at most 300 characters.</summary>
    public static string DialogText(string? text, int max = MaxDialogText)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(Math.Min(text.Length, max + 1));
        var space = false;
        var truncated = false;
        foreach (var c in text)
        {
            if (sb.Length >= max)
            {
                truncated = true;
                break;
            }
            var bad = char.IsControl(c) || IsBidiControl(c);
            if (bad || char.IsWhiteSpace(c))
            {
                if (!space && sb.Length > 0) sb.Append(c == '\n' ? '\n' : ' ');
                space = true;
                continue;
            }
            space = false;
            sb.Append(c);
        }
        var result = sb.ToString().Trim();
        return truncated ? result[..Math.Min(result.Length, max - 1)] + "…" : result;
    }

    private static bool IsBidiControl(char c) => c is '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩') or '؜';

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>The file name a download gets: the page's suggestion without a folder, without characters Windows does not allow or that hide the real extension, never a device name.</summary>
    public static string SafeFileName(string? suggested)
    {
        var name = (suggested ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var sb = new StringBuilder();
        foreach (var c in name)
        {
            if (char.IsControl(c) || IsBidiControl(c)) continue;
            sb.Append(invalid.Contains(c) ? '_' : c);
        }
        name = sb.ToString().Trim().TrimEnd('.', ' ').TrimStart('.');
        if (name.Length == 0) name = "download";
        var stem = Path.GetFileNameWithoutExtension(name);
        if (ReservedNames.Contains(stem)) name = "_" + name;
        if (name.Length > 150)
        {
            var ext = Path.GetExtension(name);
            if (ext.Length > 20) ext = "";
            name = name[..(150 - ext.Length)] + ext;
        }
        return name;
    }

    /// <summary>
    /// File types that run, install, link or mount something when opened (programs, scripts, shortcuts, installers, disk images,
    /// Office add-ins …). The couch browser never downloads them: one click in Explorer later and a web page's file runs.
    /// </summary>
    public static readonly IReadOnlySet<string> DangerousExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".msi", ".msp", ".mst", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller",
        ".bat", ".cmd", ".ps1", ".ps1xml", ".psm1", ".psd1", ".psc1", ".vb", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ws", ".wsc",
        ".hta", ".cpl", ".msc", ".reg", ".inf", ".ins", ".isp", ".lnk", ".url", ".website", ".scf", ".library-ms", ".searchconnector-ms",
        ".search-ms", ".settingcontent-ms", ".appref-ms", ".application", ".xbap", ".gadget", ".jar", ".jnlp", ".dll", ".ocx", ".sys",
        ".drv", ".chm", ".hlp", ".shs", ".shb", ".job", ".diagcab", ".msh", ".msh1", ".msh2", ".mshxml", ".msh1xml", ".msh2xml",
        ".iso", ".img", ".vhd", ".vhdx", ".xll", ".xla", ".xlam", ".ppam", ".wiz", ".mof", ".sct", ".pl", ".py", ".pyw", ".rb", ".sh",
        // Themes and remote desktop files point Windows at a server (credentials, a remote session); updates, cabinets and
        // certificates install something; Access databases run macros.
        ".theme", ".themepack", ".deskthemepack", ".rdp", ".msu", ".cab", ".cer", ".crt", ".der", ".p7b", ".ade", ".adp", ".mdb",
        ".mde", ".accde",
        // Sandbox configurations, web queries and spreadsheet links, search and content shortcuts, troubleshooter packages and
        // web archives (MHT opens in the old Internet Explorer engine).
        ".wsb", ".iqy", ".slk", ".appcontent-ms", ".diagpkg", ".mht", ".mhtml"
    };

    /// <summary>The download's (cleaned) file name ends in a type that is never downloaded; no extension at all counts as safe text.</summary>
    public static bool IsDangerousDownload(string? name)
    {
        var safe = SafeFileName(name);
        return DangerousExtensions.Contains(Path.GetExtension(safe));
    }

    /// <summary>
    /// The name a download really has. The browser's own suggestion is not enough: for types it distrusts (.scf, .lnk …) it appends
    /// ".download" or names the file "Unconfirmed 1234.crdownload", which would look harmless. So the server's name (Content-Disposition)
    /// comes first, then the suggestion without those endings, then the last part of the address.
    /// </summary>
    public static string DownloadName(string? suggested, string? contentDisposition, string? url)
    {
        var fromHeader = DispositionName(contentDisposition);
        if (fromHeader.Length > 0) return SafeFileName(fromHeader);
        var name = Path.GetFileName((suggested ?? "").Replace('/', '\\'));
        foreach (var tail in new[] { ".crdownload", ".download", ".partial" })
        {
            if (name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) name = name[..^tail.Length];
        }
        if (name.Length > 0 && !name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)) return SafeFileName(name);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var last = Uri.UnescapeDataString(uri.AbsolutePath.TrimEnd('/'));
            last = last[(last.LastIndexOf('/') + 1)..];
            if (last.Length > 0) return SafeFileName(last);
        }
        return SafeFileName(name);
    }

    /// <summary>Any of the names a download goes by (the server's, the browser's, the address's) is of a dangerous type.</summary>
    public static bool IsDangerousDownload(string? suggested, string? contentDisposition, string? url)
    {
        if (IsDangerousDownload(DownloadName(suggested, contentDisposition, url)) || IsDangerousDownload(suggested)) return true;
        var header = DispositionName(contentDisposition);
        return header.Length > 0 && IsDangerousDownload(header);
    }

    /// <summary>The file name of a Content-Disposition header (filename* in UTF-8 first, then filename); empty without one.</summary>
    public static string DispositionName(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return "";
        string? plain = null;
        foreach (var raw in header.Split(';'))
        {
            var part = raw.Trim();
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
            {
                var quote = value.IndexOf("''", StringComparison.Ordinal);
                if (quote >= 0) value = value[(quote + 2)..];
                try
                {
                    value = Uri.UnescapeDataString(value.Trim('"'));
                }
                catch (UriFormatException)
                {
                    continue;
                }
                if (value.Length > 0) return value;
            }
            else if (key.Equals("filename", StringComparison.OrdinalIgnoreCase) && plain is null)
            {
                plain = value.Trim('"');
            }
        }
        return plain ?? "";
    }

    /// <summary>The extension shown on its own in the download question (a long name is cut, the type never), without the dot; empty when there is none.</summary>
    public static string DownloadExtension(string? name)
    {
        var ext = Path.GetExtension(SafeFileName(name));
        return ext.Length > 1 ? ext[1..].ToLowerInvariant() : "";
    }

    /// <summary>Where a confirmed download goes: always straight into <paramref name="downloadsDir"/>, never over an existing file ("name (2).ext").</summary>
    public static string DownloadPath(string downloadsDir, string? suggested, Func<string, bool> exists)
    {
        var name = SafeFileName(suggested);
        var path = Path.Combine(downloadsDir, name);
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; exists(path) && i < 1000; i++) path = Path.Combine(downloadsDir, $"{stem} ({i}){ext}");
        return path;
    }

    /// <summary>The text key for a failed load by the WebView2 error status name; null when nothing is to be shown (a new navigation replaced it).</summary>
    public static string? ErrorKey(string status) => status switch
    {
        "OperationCanceled" => null,
        "HostNameNotResolved" => "browser.error.dns",
        "Timeout" => "browser.error.timeout",
        "Disconnected" => "browser.error.offline",
        "CannotConnect" or "ServerUnreachable" or "ConnectionAborted" or "ConnectionReset" or "ErrorHttpInvalidServerResponse" => "browser.error.connect",
        "ValidAuthenticationCredentialsRequired" or "ValidProxyAuthenticationRequired" => "browser.error.auth",
        "RedirectFailed" => "browser.error.redirect",
        _ when status.Contains("Certificate", StringComparison.Ordinal) => "browser.error.certificate",
        _ => "browser.error.generic"
    };

    /// <summary>
    /// The page shown when a load failed: plain HTML without scripts (a CSP forbids them), every text escaped. The failed address is
    /// shown as text, never as a link.
    /// </summary>
    public static string ErrorPage(string title, string message, string address, string hint, string lang)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        return $$"""
            <!doctype html><html lang="{{E(lang)}}"><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'">
            <title>{{E(title)}}</title>
            <style>html,body{height:100%;margin:0}body{display:flex;align-items:center;justify-content:center;background:#10131c;color:#e8ebf2;font:24px/1.5 'Segoe UI',sans-serif}
            main{max-width:44em;padding:2em}h1{font-size:2em;margin:0 0 .4em}p{margin:.4em 0;color:#b9c0d0}code{display:block;margin:1em 0;padding:.5em .8em;border-radius:.4em;background:#1c2130;color:#e8ebf2;word-break:break-all}</style>
            </head><body><main><h1>{{E(title)}}</h1><p>{{E(message)}}</p><code>{{E(DialogText(address, 200))}}</code><p>{{E(hint)}}</p></main></body></html>
            """;
    }
}

/// <summary>
/// A page that opens dialog after dialog (alert in a loop) must not take the couch hostage: more than <see cref="Burst"/> within
/// <see cref="Window"/> and the page's dialogs are dismissed by themselves for <see cref="Pause"/>.
/// </summary>
public sealed class DialogGate
{
    public const int Burst = 3;
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(30);

    /// <summary>A dialog that comes this soon after the last one was answered is a chain (alert in a loop waits for each answer).</summary>
    public static readonly TimeSpan Chained = TimeSpan.FromSeconds(2);

    private readonly Queue<DateTimeOffset> _recent = new();
    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;
    private DateTimeOffset _lastAnswer = DateTimeOffset.MinValue;
    private int _chain;

    /// <summary>The user answered a dialog of this page.</summary>
    public void Answered(DateTimeOffset now) => _lastAnswer = now;

    /// <summary>True: show this dialog. False: dismiss it without asking (and <paramref name="justBlocked"/> says whether this is the first one, for one notice).</summary>
    public bool Allow(DateTimeOffset now, out bool justBlocked)
    {
        justBlocked = false;
        if (now < _blockedUntil) return false;
        while (_recent.Count > 0 && now - _recent.Peek() > Window) _recent.Dequeue();
        // A synchronous loop (each alert waits for its answer) is not fast enough for the window: dialogs that follow their answer
        // right away count as a chain, and a chain as long as a burst is one.
        _chain = _lastAnswer != DateTimeOffset.MinValue && now - _lastAnswer < Chained ? _chain + 1 : 0;
        if (_recent.Count >= Burst || _chain >= Burst)
        {
            _recent.Clear();
            _chain = 0;
            _blockedUntil = now + Pause;
            justBlocked = true;
            return false;
        }
        _recent.Enqueue(now);
        return true;
    }
}

/// <summary>What the watchdog does about the browser's input state (checked twice a second).</summary>
public enum BrowserRepair
{
    None,
    /// <summary>The controller is meant to be on the page but the cursor mode stopped: start it again.</summary>
    RestartCursor,
    /// <summary>The controller is meant to be on the page but the page is not on screen: hand it back to the HUD.</summary>
    ReturnToHud
}

public static class BrowserWatchdog
{
    public static BrowserRepair Check(bool browsing, bool pageShown, bool cursorActive)
    {
        if (!browsing) return BrowserRepair.None;
        if (!pageShown) return BrowserRepair.ReturnToHud;
        return cursorActive ? BrowserRepair.None : BrowserRepair.RestartCursor;
    }

    /// <summary>The page answered no script call (3 s each, every 2 s) for this many checks in a row, about 15 seconds: it hangs and is loaded again.</summary>
    public const int HungAfter = 5;
}
