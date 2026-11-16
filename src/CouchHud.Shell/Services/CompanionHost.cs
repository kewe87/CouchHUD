using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CouchHud.Core.Guard;
using CouchHud.Core.Library;
using CouchHud.Shell.Native;

namespace CouchHud.Shell.Services;

/// <summary>
/// The machine side of companion apps. A companion starts through the same plan as any app (LaunchOptions.Plan: program, parameters and
/// folder as separate fields, nothing parsed by a shell), but with CreateProcess and a show command that does not activate the window
/// (SW_SHOWMINNOACTIVE or SW_SHOWNOACTIVATE), and without AllowSetForegroundWindow: it has no right to the foreground. A program that needs
/// the administrator is not started (CreateProcess fails with 740, no UAC prompt over the game).
/// <para>
/// The program is judged and started on its real path: the file is opened (writers kept out), Windows tells the final path from the open
/// handle, the name and folder rules run on that path, and CreateProcess gets exactly it while the handle is still held. The identity of the
/// new process (start time, image) is read from the process handle CreateProcess returned, before that handle is closed.
/// </para>
/// </summary>
public sealed class CompanionHost(Func<IReadOnlySet<int>> gamePids, Action<string, bool, string?>? signatureChecked = null, Action<string, bool>? lockedPinChecked = null, Func<bool>? isLockedNow = null) : ICompanionHost
{
    private const int ShowMinNoActive = 7;
    private const int ShowNoActivate = 4;
    private const uint StartfUseShowWindow = 0x1;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint ProcessQueryLimited = 0x1000;
    private const uint TokenQuery = 0x8;
    private const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public uint dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string? application, char[] commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startup, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(int processId, out int sessionId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int information, IntPtr buffer, int length, out int needed);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);

    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public string? Canonical(CompanionStart start)
    {
        // The same plan and the same checks as at planning time, on the real path of the file.
        try
        {
            var plan = Core.Library.LaunchOptions.Plan(start.Start);
            if (plan.Direct || plan.PackagedAppId is not null) return null;
            using var held = CompanionPath.Hold(plan.FileName);
            return held is not null && CompanionPath.Judge(plan.FileName, held.Path, WindowsDir) == CompanionVerdict.Ok ? held.Path : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            return null;
        }
    }

    public bool IsRunning(string canonicalPath)
    {
        var buffer = new char[1024];
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (string.Equals(Win32ProcessProbe.ImagePathOf(p.Id, buffer), canonicalPath, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        return false;
    }

    public CompanionStarted? Start(CompanionStart start, string canonical)
    {
        var plan = Core.Library.LaunchOptions.Plan(start.Start);
        if (plan.Direct || plan.PackagedAppId is not null) throw new InvalidOperationException("not a plain program");
        // The file is held open (nobody may write or delete it) while it is judged again and started: what was checked is what runs. If the
        // real path is not the one that was verified a moment ago, nothing starts.
        using var held = CompanionPin.Hold(plan);
        if (held is null || !string.Equals(held.Path, canonical, StringComparison.OrdinalIgnoreCase)
            || CompanionPath.Judge(plan.FileName, held.Path, WindowsDir) != CompanionVerdict.Ok)
        {
            Log($"companion {Name(start)}: the program changed, not started");
            return null;
        }
        // A signed rule (Wand) keeps the file's hash. The same hash starts at once; another one (the program updated itself, or was replaced)
        // starts only if the signature check, run again on this very held file, holds. Otherwise nothing starts and the page says so.
        if (!string.IsNullOrEmpty(start.Rule.Sha256))
        {
            var decision = SignedProgram.Decide(start.Rule.Sha256, held.Sha256Hex(), () => Authenticode.VerifyPublisher(held));
            signatureChecked?.Invoke(start.App.Id, !decision.SignatureInvalid, decision.NewHash);
            if (!decision.Start)
            {
                Log($"companion {Name(start)}: the signature is no longer valid, not started");
                return null;
            }
        }
        // A locked couch bypasses the child's own list, and the file may sit where the child can write: only exactly what the parent confirmed
        // starts (path, arguments, folder, file), judged on this held file. In this order: the digest of the held file first (the slow part,
        // only when the start is locked: an unlocked start, an adult's, is not held to the pin and Decide does not look at the digest), then
        // the lock, read as late as possible so that locked now counts even if the plan was made unlocked (then the digest is made after all),
        // then the decision, which for a locked start also reads the folder ACLs now (a folder that became writable since the confirmation
        // blocks the start unless the parent allowed that).
        var digest = start.Locked || (isLockedNow?.Invoke() ?? false) ? CompanionPin.DigestOf(plan, held, start.Rule.AllowWritableFolder) : "";
        var lockedNow = isLockedNow?.Invoke() ?? false;
        if (digest.Length == 0 && (start.Locked || lockedNow)) digest = CompanionPin.DigestOf(plan, held, start.Rule.AllowWritableFolder);
        var pinDecision = CompanionPin.Decide(start.Rule, start.Locked, lockedNow, digest, () => CompanionFolders.Writable(plan, held));
        if (string.IsNullOrEmpty(start.Rule.Sha256) && (start.Locked || lockedNow) && pinDecision is PinStart.Start or PinStart.SkipChanged or PinStart.SkipFolderWritable)
            lockedPinChecked?.Invoke(start.App.Id, pinDecision != PinStart.SkipChanged);
        if (pinDecision != PinStart.Start)
        {
            Log($"companion {Name(start)}: not started on the locked couch ({pinDecision})");
            return null;
        }
        var line = ($"\"{held.Path}\" {plan.Arguments}".TrimEnd() + "\0").ToCharArray();
        var folder = CompanionPin.EffectiveFolder(plan, held.Path);
        var info = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = StartfUseShowWindow,
            wShowWindow = start.Rule.Minimized ? (short)ShowMinNoActive : (short)ShowNoActivate
        };
        if (!CreateProcessW(held.Path, line, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment, IntPtr.Zero, folder, ref info, out var process))
        {
            var error = Marshal.GetLastWin32Error();
            Log($"companion {Name(start)}: CreateProcess failed ({error}{(error == 740 ? ", needs the administrator" : "")})");
            return null;
        }
        try
        {
            // Identity from the handle CreateProcess gave us, while it is still open: the start time and the image of exactly this process.
            var startTime = GetProcessTimes(process.hProcess, out var created, out _, out _, out _) ? (long)(((ulong)created.High << 32) | created.Low) : 0;
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            var image = NativeMethods.QueryFullProcessImageName(process.hProcess, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
            // What CouchHud starts for the user is not a competitor for the foreground guard. No AllowSetForegroundWindow: no right to the front.
            OwnLaunches.Register(process.dwProcessId, startTime);
            return new CompanionStarted(process.dwProcessId, new ProcessStamp(image, startTime), held.Path);
        }
        finally
        {
            NativeMethods.CloseHandle(process.hThread);
            NativeMethods.CloseHandle(process.hProcess);
        }
    }

    public ForegroundInfo? Foreground()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return null;
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        return pid <= 0 ? null : new ForegroundInfo(window.ToInt64(), pid, Win32ProcessControl.StartOf(pid));
    }

    public bool InGameTree(int pid)
    {
        var pids = gamePids();
        return pids.Count > 0 && OwnLaunches.DescendsFrom(pid, p => pids.Contains(p));
    }

    public bool InCompanionTree(int pid, IReadOnlyDictionary<int, long> roots) =>
        // Every ancestor is compared by id and by start time (a number that came back is not the root), and a child is never older than its parent.
        OwnLaunches.DescendsFrom(pid, p => roots.TryGetValue(p, out var start) && start > 0 && Win32ProcessControl.StartOf(p) == start);

    public bool MayMinimize(ForegroundInfo front)
    {
        var image = Win32ProcessProbe.ImagePathOf(front.Pid, new char[1024]);
        var text = new StringBuilder(64);
        var window = new IntPtr(front.Window);
        var cls = GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : null;
        var sameSession = ProcessIdToSessionId(front.Pid, out var session) && ProcessIdToSessionId(Environment.ProcessId, out var ownSession) && session == ownSession;
        var sameIntegrity = IntegrityOf(front.Pid) is { } other && IntegrityOf(Environment.ProcessId) is { } own && other == own;
        return !CompanionFocus.Exempt(image, cls, sameSession, sameIntegrity, WindowsDir);
    }

    /// <summary>The integrity level (RID of the token's label) of a process, null when Windows will not say.</summary>
    private static uint? IntegrityOf(int pid)
    {
        var process = NativeMethods.OpenProcess(ProcessQueryLimited, false, pid);
        if (process == IntPtr.Zero) return null;
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out token)) return null;
            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 1024) return null;
            buffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _)) return null;
            var sid = Marshal.ReadIntPtr(buffer);
            var count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
            return count == 0 ? null : (uint)Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) NativeMethods.CloseHandle(token);
            NativeMethods.CloseHandle(process);
        }
    }

    public bool MinimizeWindow(long window) => ShowWindowAsync(new IntPtr(window), ShowMinNoActive);

    public Task Delay(TimeSpan time, CancellationToken cancel) => Task.Delay(time, cancel);

    public void Log(string line) => Console.WriteLine($"[companion] {line}");

    private static string Name(CompanionStart start) => CouchHud.Core.Plugins.PluginSettingRules.LogText(start.App.Name);
}
