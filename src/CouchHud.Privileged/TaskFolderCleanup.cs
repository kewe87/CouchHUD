using System.Runtime.InteropServices;

namespace CouchHud.Privileged;

/// <summary>The Task Scheduler's folders, as far as the cleanup needs them (COM in Windows, a fake in the tests).</summary>
internal interface ITaskFolders
{
    /// <summary>The full paths of the direct subfolders of <paramref name="path"/> (e.g. <c>\CouchHud\S-1-5-21-…</c>).</summary>
    IReadOnlyList<string> SubFolders(string path);

    /// <summary>The number of tasks directly in <paramref name="path"/>, hidden ones included.</summary>
    int TaskCount(string path);

    /// <summary>Deletes the folder <paramref name="path"/>; the Task Scheduler itself refuses a folder that is not empty.</summary>
    void Delete(string path);
}

/// <summary>
/// Uninstall: the empty task folders CouchHUD left behind (<c>\CouchHud\&lt;SID&gt;</c>, then <c>\CouchHud</c>) go after the tasks did.
/// Only <c>\CouchHud</c> and folders below it, only when a folder holds no task and no subfolder (looked at right before, and the Task
/// Scheduler refuses a folder that is not empty anyway). A folder that cannot be read or deleted stays; that is logged, never thrown,
/// so the uninstall goes on. Registry keys (Policies\Explorer, Policies\System) are not touched here: they may predate CouchHUD.
/// </summary>
public static class TaskFolderCleanup
{
    private static readonly string Root = "\\" + TaskSetup.Folder;

    /// <summary>Through the Task Scheduler's COM interface (elevated). Never throws.</summary>
    public static void Run(IEventSink events)
    {
        try
        {
            using var folders = ComTaskFolders.Connect();
            if (folders is null)
            {
                events.Error("[uninstall] task folders: task scheduler COM not available, empty folders stay");
                return;
            }
            RemoveEmpty(folders, events);
        }
        catch (Exception ex)
        {
            events.Error($"[uninstall] task folders: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Depth first: the subfolders of <c>\CouchHud</c> before <c>\CouchHud</c> itself. Never throws.</summary>
    internal static void RemoveEmpty(ITaskFolders folders, IEventSink events) => RemoveIfEmpty(folders, Root, events, depth: 0);

    /// <summary>True when <paramref name="path"/> is <c>\CouchHud</c> or below it, without any trick in the name.</summary>
    internal static bool IsOurs(string path) =>
        !string.IsNullOrEmpty(path)
        && (path.Equals(Root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Root + "\\", StringComparison.OrdinalIgnoreCase))
        && !path.Split('\\').Skip(1).Any(part => part.Length == 0 || part is "." or "..")
        && path.IndexOfAny(['/', '\0']) < 0;

    /// <summary>True when the folder is gone (deleted, or never there).</summary>
    private static bool RemoveIfEmpty(ITaskFolders folders, string path, IEventSink events, int depth)
    {
        // Our folders are two levels deep (\CouchHud\<SID>); deeper ones are not ours to walk through, they stay with their parents.
        if (!IsOurs(path) || depth > 2) return false;
        IReadOnlyList<string> subs;
        try
        {
            subs = folders.SubFolders(path);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (Exception ex)
        {
            events.Error($"[uninstall] task folder {path} not read, it stays ({ex.GetType().Name})");
            return false;
        }
        var allGone = true;
        foreach (var sub in subs)
        {
            // A name that would lead out of \CouchHud is never followed.
            if (!IsOurs(sub) || !sub.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase))
            {
                events.Error($"[uninstall] task folder {sub} left alone (not below {path})");
                allGone = false;
                continue;
            }
            allGone &= RemoveIfEmpty(folders, sub, events, depth + 1);
        }
        if (!allGone) return false;
        try
        {
            // Looked at again right before: a task or folder that came in the meantime keeps the folder.
            if (folders.TaskCount(path) > 0 || folders.SubFolders(path).Count > 0)
            {
                events.Info($"[uninstall] task folder {path} is not empty, it stays");
                return false;
            }
            folders.Delete(path);
            events.Info($"[uninstall] empty task folder {path} removed");
            return true;
        }
        catch (Exception ex)
        {
            events.Error($"[uninstall] task folder {path} not removed ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>ITaskService (Schedule.Service) late bound, as in <see cref="TaskAcl"/>.</summary>
    private sealed class ComTaskFolders : ITaskFolders, IDisposable
    {
        private const int TaskEnumHidden = 1;
        private const int ErrorFileNotFound = unchecked((int)0x80070002);
        private const int ErrorPathNotFound = unchecked((int)0x80070003);
        private readonly object _service;

        private ComTaskFolders(object service) => _service = service;

        public static ComTaskFolders? Connect()
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) return null;
            var service = Activator.CreateInstance(type)!;
            try
            {
                ((dynamic)service).Connect();
                return new ComTaskFolders(service);
            }
            catch
            {
                if (Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
                throw;
            }
        }

        private dynamic Folder(string path)
        {
            try
            {
                return ((dynamic)_service).GetFolder(path);
            }
            catch (COMException ex) when (ex.HResult is ErrorFileNotFound or ErrorPathNotFound)
            {
                throw new FileNotFoundException(path, ex);
            }
        }

        public IReadOnlyList<string> SubFolders(string path)
        {
            var result = new List<string>();
            foreach (var sub in Folder(path).GetFolders(0)) result.Add((string)sub.Path);
            return result;
        }

        public int TaskCount(string path) => (int)Folder(path).GetTasks(TaskEnumHidden).Count;

        public void Delete(string path)
        {
            var slash = path.LastIndexOf('\\');
            var parent = slash > 0 ? path[..slash] : "\\";
            // ITaskFolder::DeleteFolder on the parent, with the subfolder's name; it fails on a folder that is not empty.
            Folder(parent).DeleteFolder(path[(slash + 1)..], 0);
        }

        public void Dispose()
        {
            if (Marshal.IsComObject(_service)) Marshal.FinalReleaseComObject(_service);
        }
    }
}
