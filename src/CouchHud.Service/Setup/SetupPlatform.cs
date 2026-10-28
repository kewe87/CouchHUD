using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using CouchHud.Core.Platform;
using CouchHud.Core.Plugins;
using CouchHud.Privileged;

namespace CouchHud.Service.Setup;

/// <summary>A started setup.</summary>
public interface ISetupProcess : IDisposable
{
    int Id { get; }

    /// <summary>The image the process really runs (QueryFullProcessImageName), null when Windows does not tell.</summary>
    string? ImagePath { get; }

    bool WaitForExit(TimeSpan timeout);

    int ExitCode { get; }

    void Kill();
}

/// <summary>Starts a setup as the service's own account (SYSTEM), without a window and without a job object that would end it with the service.</summary>
public interface ISetupStarter
{
    /// <summary>Null when it did not start. <paramref name="environment"/> is the whole environment of the setup (nothing is inherited).</summary>
    ISetupProcess? Start(string path, string arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment);
}

/// <summary>
/// The environment a setup gets (P8 review L5): built from known places, not inherited from the service. The Windows folders, PATH
/// with system folders only, TEMP/TMP in the service's protected folder, and nothing that loads code into a .NET or other runtime
/// (COR_*, CORECLR_*, DOTNET_*, COMPlus_* never appear: no inherited variable is copied).
/// </summary>
public static class SetupEnvironment
{
    private static readonly string[] RuntimePrefixes = ["COR_", "CORECLR_", "DOTNET_", "COMPlus_"];

    public static IReadOnlyDictionary<string, string> Build(string tempFolder) => Build(
        tempFolder,
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.Is64BitOperatingSystem);

    /// <summary>With the folders passed in (tests).</summary>
    public static IReadOnlyDictionary<string, string> Build(string tempFolder, string windows, string programData, string programFiles, string programFilesX86, bool is64Bit)
    {
        foreach (var folder in new[] { tempFolder, windows, programData, programFiles })
        {
            if (string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder)) throw new ArgumentException("folders must be fully qualified");
        }
        var system32 = Path.Combine(windows, "System32");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = windows,
            ["windir"] = windows,
            ["SystemDrive"] = Path.GetPathRoot(windows)!.TrimEnd('\\'),
            ["ComSpec"] = Path.Combine(system32, "cmd.exe"),
            ["PATH"] = string.Join(';', system32, windows, Path.Combine(system32, "Wbem"), Path.Combine(system32, "WindowsPowerShell", "v1.0")),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["TEMP"] = tempFolder,
            ["TMP"] = tempFolder,
            // Read by scripts the setup runs (PowerShell), all from the known folders.
            ["ProgramData"] = programData,
            ["ALLUSERSPROFILE"] = programData,
            ["ProgramFiles"] = programFiles,
            ["CommonProgramFiles"] = Path.Combine(programFiles, "Common Files"),
            ["PROCESSOR_ARCHITECTURE"] = is64Bit ? "AMD64" : "x86",
        };
        if (is64Bit && !string.IsNullOrEmpty(programFilesX86))
        {
            environment["ProgramFiles(x86)"] = programFilesX86;
            environment["ProgramW6432"] = programFiles;
        }
        if (environment.Keys.Any(IsRuntimeVariable)) throw new InvalidOperationException("runtime variable in the setup environment");
        return environment;
    }

    public static bool IsRuntimeVariable(string name) => RuntimePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Starts the setup with CreateProcessW directly (security review L2): no handle of the service is inherited (bInheritHandles false;
/// Process.Start always passes true), the environment block is exactly the one given, the image is named by its full path (nothing is
/// looked up) and no window is made. The process handle is held for the whole wait, so its id cannot be reused meanwhile.
/// </summary>
public sealed class SystemSetupStarter : ISetupStarter
{
    private const int CreateUnicodeEnvironment = 0x00000400;
    private const int CreateNoWindow = 0x08000000;
    private const int MaxCommandLine = 32767;

    public ISetupProcess? Start(string path, string arguments, string workingDirectory, IReadOnlyDictionary<string, string> environment)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("the setup must be given by its full path", nameof(path));
        if (path.Contains('"')) throw new ArgumentException("no quote in the path", nameof(path));
        var commandLine = CommandLine(path, arguments);
        if (commandLine.Length >= MaxCommandLine) throw new ArgumentException("command line too long", nameof(arguments));
        var block = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
        try
        {
            var startup = new STARTUPINFOW { cb = Marshal.SizeOf<STARTUPINFOW>() };
            var line = new StringBuilder(commandLine, MaxCommandLine);
            if (!CreateProcessW(path, line, IntPtr.Zero, IntPtr.Zero, false, CreateUnicodeEnvironment | CreateNoWindow, block, workingDirectory, ref startup, out var info))
            {
                return null;
            }
            CloseHandle(info.hThread);
            return new NativeSetupProcess(new SafeProcessHandle(info.hProcess, ownsHandle: true), info.dwProcessId);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }

    /// <summary>The program in quotes, then the fixed arguments.</summary>
    public static string CommandLine(string path, string arguments) => $"\"{path}\" {arguments}";

    /// <summary>"name=value\0…\0\0", sorted by name without regard to case (as Windows expects); names with '=' or a NUL are refused.</summary>
    public static string EnvironmentBlock(IReadOnlyDictionary<string, string> environment)
    {
        var text = new StringBuilder();
        foreach (var (name, value) in environment.OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (name.Length == 0 || name.Contains('=') || name.Contains('\0') || value.Contains('\0')) throw new ArgumentException($"bad environment entry {name}");
            text.Append(name).Append('=').Append(value).Append('\0');
        }
        // StringToHGlobalUni adds the last NUL of the double one.
        if (text.Length == 0) text.Append('\0');
        return text.ToString();
    }

    private sealed class NativeSetupProcess(SafeProcessHandle handle, int id) : ISetupProcess
    {
        public int Id => id;

        public string? ImagePath
        {
            get
            {
                var buffer = new StringBuilder(1024);
                var size = (uint)buffer.Capacity;
                return QueryFullProcessImageNameW(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
            }
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            var ms = timeout == Timeout.InfiniteTimeSpan ? uint.MaxValue : (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
            return WaitForSingleObject(handle, ms) == 0;
        }

        public int ExitCode => GetExitCodeProcess(handle, out var code) ? code : -1;

        public void Kill()
        {
            // The tree first (Inno's second stage runs as a child); the held handle keeps the id from being reused meanwhile.
            try
            {
                using var process = Process.GetProcessById(id);
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            TerminateProcess(handle, 1);
        }

        public void Dispose() => handle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, int creationFlags, IntPtr environment, string currentDirectory, ref STARTUPINFOW startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out int exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeProcessHandle handle, int exitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
}

/// <summary>What is installed: the versions a setup must be newer than.</summary>
public interface IInstalledVersions
{
    /// <summary>The ProductVersion of <c>{app}\CouchHud.exe</c>; null when it cannot be read (then no setup is accepted).</summary>
    SemVer? CouchHud();
}

/// <param name="appDirectory">The install folder of CouchHUD (the service runs from <c>{app}\service</c>).</param>
public sealed class InstalledVersions(string appDirectory) : IInstalledVersions
{
    /// <summary>The service's own place: <c>{app}\service\CouchHud.Service.exe</c>, so <c>{app}</c> is two levels up.</summary>
    public static InstalledVersions ForService() =>
        new(Path.GetDirectoryName(Path.GetDirectoryName(Environment.ProcessPath) ?? "") ?? "");

    public SemVer? CouchHud()
    {
        if (!Path.IsPathFullyQualified(appDirectory)) return null;
        var exe = Path.Combine(appDirectory, "CouchHud.exe");
        if (!File.Exists(exe)) return null;
        try
        {
            return SemVer.TryParse(FileVersionInfo.GetVersionInfo(exe).ProductVersion, out var version) ? version : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
