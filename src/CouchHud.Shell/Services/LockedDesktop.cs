using System.Globalization;
using System.Runtime.InteropServices;
using CouchHud.Core.Guard;
using CouchHud.Core.Localization;
using CouchHud.Core.Platform;
using Microsoft.Win32;

namespace CouchHud.Shell.Services;

/// <summary>
/// The Windows side of a locked couch beyond the foreground guard: per-user policies (through their own
/// <see cref="GuardSnapshot"/>, so a crash or the uninstall puts the user's values back) and the accessibility
/// shortcuts (Sticky Keys and friends) switched off at run time only. Apply when the couch locks, Release when it
/// opens, in PC mode, on quit, in the emergency desktop and at the start after a crash.
/// While applied, a Run entry of the user starts CouchHud with <see cref="ReleaseSwitch"/>: if the next sign-in does not
/// start CouchHud as the shell (it is gone, uninstalled or its shell entry was removed), the policies still go away.
/// The Policies keys only let administrators write: on an account that cannot write them, the setup's scheduled tasks do it
/// (see <see cref="PolicyHelper"/>); the release, from CouchHud or from that Run entry, asks the same tasks. Without them
/// those policies are simply not active.
/// </summary>
public sealed class LockedDesktop(string dataDir)
{
    /// <summary>Gives the policies back and ends; does nothing while a CouchHud runs (it manages them itself).</summary>
    public const string ReleaseSwitch = "--release-lock-policies";

    private const string StateFile = "guard-locked.json";
    // Keys that did not exist before the first apply (removed again if empty), and the mark that the shortcuts were switched off.
    private const string KeysFile = "guard-locked.keys";
    internal const string KeysFileName = KeysFile;
    private const string HotkeysFile = "guard-locked.hotkeys";
    private const string RunKey = ReleaseEntry.KeyPath;
    private const string RunValue = ReleaseEntry.ValueName;
    internal const string RunKeyPath = RunKey;
    internal const string RunValueName = RunValue;
    private readonly object _gate = new();
    private bool _on;
    // Any instance: the Run entry stays as long as the lock is on, or the helper has not released everything yet.
    private static volatile bool _anyOn;

    public bool On => _on;

    // elevated: the state file is the user's, so an administrator process only restores what ElevatedGuard accepts.
    private static GuardSnapshot Snapshot(string dir, bool elevated = false, string? userHive = null) =>
        new(new WindowsRegistry(userHive), new FileVdfStore(), Path.Combine(dir, StateFile),
            message => Console.Error.WriteLine($"[lock] hardening: {message}"), elevated ? ElevatedGuard.Accepts : null);

    /// <summary>Idempotent; false puts everything back (also what an earlier, crashed run left behind).</summary>
    public void Set(bool on, bool force = false)
    {
        lock (_gate)
        {
            if (on == _on && !force) return;
            try
            {
                if (on) ApplyCore(dataDir);
                // The unlock gives back what this class owns; Task Manager belongs to the couch security and stays.
                else ReleaseCore(dataDir, onlyWithUnlock: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"[lock] hardening: {ex.Message}");
            }
            _on = on;
            _anyOn = on;
        }
        // The shell and running programs re-read their policies on this message; a hung window must not hold us up.
        Task.Run(Broadcast);
    }

    /// <summary>
    /// Policies and shortcuts back as the user had them. Safe from any process, e.g. the recovery or the uninstaller.
    /// <paramref name="wait"/>: also wait for the broadcast (a process that ends right after), for the helper tasks at most
    /// <paramref name="maxWait"/> (default 20 s). <paramref name="elevated"/>: an administrator process (the uninstaller) reads
    /// the user's files only as far as <see cref="ElevatedGuard"/> allows.
    /// </summary>
    public static void Release(string dir, bool wait = false, bool elevated = false, TimeSpan? maxWait = null)
    {
        try
        {
            ReleaseCore(dir, elevated: elevated);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[lock] hardening: {ex.Message}");
        }
        // The helper tasks run on their own: wait for them where the process ends right after.
        if (wait && !PolicyHelper.WaitIdle(maxWait ?? TimeSpan.FromSeconds(20))) Console.Error.WriteLine("[lock] policy helper still busy, giving up waiting");
        if (wait) Broadcast();
        else Task.Run(Broadcast);
    }

    /// <summary>The keys the lock created itself at its first apply (the note stays until they are cleaned up again).</summary>
    public static IReadOnlyList<string> CreatedKeys(string dir)
    {
        try
        {
            var file = Path.Combine(dir, KeysFile);
            return File.Exists(file) ? File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0).ToList() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void ApplyCore(string dir)
    {
        var registry = new WindowsRegistry();
        // Which policy keys are ours: written once, at the first apply (a crash must not make them look like the user's).
        var keysFile = Path.Combine(dir, KeysFile);
        if (!File.Exists(keysFile) && !File.Exists(Path.Combine(dir, StateFile)))
        {
            Directory.CreateDirectory(dir);
            File.WriteAllLines(keysFile, LockHardening.MissingKeys(registry));
        }
        // The Policies keys: written here only where the account may write them, otherwise by the helper tasks (or not at all).
        var direct = PolicyHelper.Mode() == "direct";
        var changes = LockHardening.Changes(registry);
        Snapshot(dir).Apply(direct ? changes : changes.Where(c => !LockHardening.IsProtectedKey(c.Location)).ToList());
        if (!direct) PolicyHelper.Want(PolicyGroups.Lock, true, dir);
        File.WriteAllText(Path.Combine(dir, HotkeysFile), "1");
        Accessibility.DisableHotkeys();
        WriteReleaseEntry(registry);
    }

    private static void ReleaseCore(string dir, bool onlyWithUnlock = false, bool elevated = false)
    {
        var registry = new WindowsRegistry();
        var errors = Snapshot(dir, elevated).RestoreLeftovers();
        // Only if the shortcuts were switched off by us (a mark that survives a crash).
        var hotkeys = Path.Combine(dir, HotkeysFile);
        if (File.Exists(hotkeys))
        {
            Accessibility.RestoreHotkeys();
            File.Delete(hotkeys);
        }
        // Keys we created go again when they are empty; with errors left in the snapshot the next try finds them still marked.
        // The protected ones (Policies) only the helper can delete: the note stays until it is done with the last group.
        var keysFile = Path.Combine(dir, KeysFile);
        var keysClean = errors.Count == 0 && File.Exists(keysFile);
        if (keysClean)
        {
            LockHardening.RemoveEmpty(registry, File.ReadAllLines(keysFile));
            if (!PolicyHelper.HasMarks(dir)) File.Delete(keysFile);
        }
        // What the helper set it releases through its tasks; the Run entry, the safety net for that, goes when it is done.
        PolicyHelper.ReleaseMarked(dir, whenReleased: () =>
        {
            if (keysClean) FinishKeys(dir);
            if (!_anyOn) DeleteReleaseEntryIfClear();
        }, onlyWithUnlock);
        if (!PolicyHelper.HasMarks(dir)) DeleteReleaseEntryIfClear();
    }

    /// <summary>After the helper released the last group: the empty Policies keys that its import created go, then the note.</summary>
    private static void FinishKeys(string dir)
    {
        var keysFile = Path.Combine(dir, KeysFile);
        try
        {
            if (!File.Exists(keysFile)) return;
            var keys = File.ReadAllLines(keysFile);
            PolicyHelper.CleanupKeys(keys);
            LockHardening.RemoveEmpty(new WindowsRegistry(), keys);
            File.Delete(keysFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[lock] keys note: {ex.Message}");
        }
    }

    /// <summary>
    /// Administrator tidy-up for an account that is not the process's own (uninstall from another account): gives back what the
    /// lock set in that account's hive, as far as <see cref="ElevatedGuard"/> allows, and removes the Run entry that would
    /// start the release. <paramref name="hive"/> is the name below HKEY_USERS, <paramref name="dir"/> that account's data folder.
    /// </summary>
    public static void ReleaseOfOtherAccount(string dir, string hive)
    {
        var errors = Snapshot(dir, elevated: true, userHive: hive).RestoreLeftovers();
        foreach (var error in errors) Console.Error.WriteLine($"[uninstall] {hive}: {error}");
        var registry = new WindowsRegistry(hive);
        // What the helper tasks set (they cannot run any more): the fixed values of the groups it marked, only where they still
        // have the value the lock gave them, and only our programs in the DisallowRun list.
        foreach (var group in PolicyHelper.Marked(dir).Select(PolicyGroups.ById).OfType<PolicyGroup>())
        {
            foreach (var c in group.Dwords)
            {
                if (registry.ReadDword(c.Location, c.Name)?.ToString(CultureInfo.InvariantCulture) == c.Value) registry.DeleteValue(c.Location, c.Name);
            }
            foreach (var key in group.ListKeys)
            {
                foreach (var (name, value) in registry.ReadValues(key))
                {
                    if (LockHardening.BlockedPrograms.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase)) registry.DeleteValue(key, name);
                }
            }
        }
        registry.DeleteValue(RunKey, RunValue);
        registry.DeleteValue(SignInLock.RunKey, SignInLock.RunValue);
    }

    /// <summary>The Run entries CouchHud sets (release of the lock policies, lock at sign-in) in another account's hive.</summary>
    public static void RemoveRunEntriesOf(string hive)
    {
        var registry = new WindowsRegistry(hive);
        registry.DeleteValue(RunKey, RunValue);
        registry.DeleteValue(SignInLock.RunKey, SignInLock.RunValue);
    }

    /// <summary>Uninstall: the Run entry of the account of this process goes, also when a helper task could not release everything.</summary>
    public static void RemoveReleaseEntry() => DeleteReleaseEntry();

    /// <summary>The Run entry is the safety net for the release: it stays as long as some of our values are still in the registry.</summary>
    private static void DeleteReleaseEntryIfClear()
    {
        try
        {
            if (PolicyRelease.AnyOurs(new WindowsRegistry()))
            {
                Console.WriteLine("[lock] run entry stays: some of the lock's policies are still set");
                return;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Console.Error.WriteLine($"[lock] run entry: {ex.Message}");
            return;
        }
        DeleteReleaseEntry();
    }

    /// <summary>The state of the snapshot is there (the next release restores it itself, no helper needed).</summary>
    public static bool HasPendingState(string dir)
    {
        try
        {
            return Snapshot(dir).HasPending;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true;
        }
    }

    /// <summary>
    /// The Run entry at sign-in: gives the policies back (see <see cref="Release"/>), and when some of ours are still there afterwards
    /// (no helper task, nothing that explains them) says so and offers the cleanup, instead of ending without a word.
    /// </summary>
    public static void ReleaseFromRunEntry(string dir)
    {
        Release(dir, wait: true);
        try
        {
            // Only the language is needed: read without side effects (this file also holds the elevated release, see ElevatedContractTests).
            if (CouchHud.Core.Config.ConfigStore.Peek(dir) is { } settings) Strings.Use(settings.ActiveProfile.Language);
            if (PolicyHelper.StuckGroups(dir, [], HasPendingState(dir)).Count == 0) return;
            Console.Error.WriteLine("[lock] Windows restrictions of the lock are stuck, offering the cleanup");
            var answer = MessageBoxW(IntPtr.Zero, Strings.Get("stuck.ask"), "CouchHUD", 0x4 | 0x30 | 0x10000 | 0x40000);
            // IDYES
            if (answer != 6) return;
            // Off the UI context: a sync wait on the dispatcher thread would never see the continuation.
            Task.Run(() => AdminService.CleanupStuckPoliciesAsync(dir)).GetAwaiter().GetResult();
            MessageBoxW(IntPtr.Zero, Strings.Get("stuck.done"), "CouchHUD", 0x0 | 0x40 | 0x10000 | 0x40000);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"[lock] cleanup: {ex.Message}");
            MessageBoxW(IntPtr.Zero, Strings.Get("stuck.failed", ex.Message), "CouchHUD", 0x0 | 0x10 | 0x10000 | 0x40000);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);

    private static void DeleteReleaseEntry()
    {
        try
        {
            new WindowsRegistry().DeleteValue(RunKey, RunValue);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            Console.Error.WriteLine($"[lock] run entry: {ex.Message}");
        }
    }

    private static void WriteReleaseEntry(IRegistry registry)
    {
        if (Environment.ProcessPath is not { } exe) return;
        registry.WriteString(RunKey, RunValue, $"\"{exe}\" {ReleaseSwitch}");
    }

    private const uint WmSettingChange = 0x001A;
    private const uint SmtoAbortIfHung = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    private static void Broadcast()
    {
        try
        {
            SendMessageTimeout(new IntPtr(0xFFFF), WmSettingChange, IntPtr.Zero, "Policy", SmtoAbortIfHung, 1500, out _);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"[lock] policy broadcast: {ex.Message}");
        }
    }

    /// <summary>
    /// The shortcuts that open Windows' accessibility settings (five times Shift, eight seconds Shift, …) switched off with
    /// SystemParametersInfo and without SPIF_UPDATEINIFILE: they last until the session ends or <see cref="RestoreHotkeys"/>,
    /// and the user's saved setting (registry) is the truth to go back to.
    /// </summary>
    private static class Accessibility
    {
        private const uint SpiBase = 0;

        // Name under HKCU\Control Panel\Accessibility, SPI_GET, SPI_SET, size of the structure.
        private static readonly (string Key, uint Get, uint Set, int Size, bool Scheme)[] Features =
        [
            ("StickyKeys", 0x003A, 0x003B, 8, false),
            ("Keyboard Response", 0x0032, 0x0033, 24, false),
            ("ToggleKeys", 0x0034, 0x0035, 8, false),
            ("MouseKeys", 0x0036, 0x0037, 28, false),
            ("HighContrast", 0x0042, 0x0043, 16, true)
        ];

        [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(uint action, uint param, IntPtr data, uint winIni);

        public static void DisableHotkeys()
        {
            foreach (var feature in Features)
            {
                try
                {
                    if (Read(feature) is { } flags && (flags & LockHardening.HotkeyActive) != 0) Write(feature, LockHardening.WithoutHotkey(flags));
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
                {
                    Console.Error.WriteLine($"[lock] accessibility {feature.Key}: {ex.Message}");
                }
            }
        }

        public static void RestoreHotkeys()
        {
            foreach (var feature in Features)
            {
                try
                {
                    if (SavedFlags(feature.Key) is not { } saved || Read(feature) is not { } current) continue;
                    var wanted = LockHardening.WithHotkeyRestored(current, saved);
                    if (wanted != current) Write(feature, wanted);
                }
                catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ArgumentException)
                {
                    Console.Error.WriteLine($"[lock] accessibility {feature.Key}: {ex.Message}");
                }
            }
        }

        /// <summary>What the user saved (Flags is a string in the registry); null when there is nothing readable.</summary>
        private static uint? SavedFlags(string key)
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Accessibility\" + key);
                return k?.GetValue("Flags") is { } v && uint.TryParse(v.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var flags) ? flags : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return null;
            }
        }

        // All five structures start with cbSize and dwFlags; HIGHCONTRAST goes on with a pointer to a scheme name buffer.
        private static uint? Read((string Key, uint Get, uint Set, int Size, bool Scheme) f)
        {
            var buffer = Marshal.AllocHGlobal(f.Size + 512);
            try
            {
                for (var i = 0; i < f.Size + 512; i += 4) Marshal.WriteInt32(buffer, i, 0);
                Marshal.WriteInt32(buffer, 0, f.Size);
                if (f.Scheme) Marshal.WriteIntPtr(buffer, 8, buffer + f.Size);
                if (!SystemParametersInfo(f.Get, (uint)f.Size, buffer, SpiBase)) return null;
                return unchecked((uint)Marshal.ReadInt32(buffer, 4));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static void Write((string Key, uint Get, uint Set, int Size, bool Scheme) f, uint flags)
        {
            var buffer = Marshal.AllocHGlobal(f.Size + 512);
            try
            {
                for (var i = 0; i < f.Size + 512; i += 4) Marshal.WriteInt32(buffer, i, 0);
                Marshal.WriteInt32(buffer, 0, f.Size);
                // Read the rest of the structure first (speeds, scheme) and change only the flags.
                if (f.Scheme) Marshal.WriteIntPtr(buffer, 8, buffer + f.Size);
                if (!SystemParametersInfo(f.Get, (uint)f.Size, buffer, SpiBase)) return;
                Marshal.WriteInt32(buffer, 4, unchecked((int)flags));
                // No SPIF_UPDATEINIFILE: nothing is saved, the next sign-in starts from the user's own setting.
                SystemParametersInfo(f.Set, (uint)f.Size, buffer, SpiBase);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }
}
