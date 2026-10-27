using CouchHud.Privileged;

namespace CouchHud.Privileged.Tests;

/// <summary>
/// The empty task folders after the uninstall: only \CouchHud and below, only empty ones, depth first, and a failure is logged but
/// never thrown (the uninstall goes on).
/// </summary>
public class TaskFolderCleanupTests
{
    private sealed class Events : IEventSink
    {
        public List<string> Errors { get; } = [];

        public void Info(string message) { }

        public void Error(string message) => Errors.Add(message);
    }

    /// <summary>A Task Scheduler tree: folder path to its task count; like the real one, deleting a folder that is not empty fails.</summary>
    private sealed class FakeFolders : ITaskFolders
    {
        public Dictionary<string, int> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Deleted { get; } = [];
        public HashSet<string> FailDelete { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> FailRead { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Extra { get; } = [];

        public IReadOnlyList<string> SubFolders(string path)
        {
            if (FailRead.Contains(path)) throw new UnauthorizedAccessException();
            if (!Folders.ContainsKey(path)) throw new FileNotFoundException(path);
            var subs = Folders.Keys.Where(f => f.StartsWith(path + "\\", StringComparison.OrdinalIgnoreCase) && !f[(path.Length + 1)..].Contains('\\')).ToList();
            if (path.Equals("\\CouchHud", StringComparison.OrdinalIgnoreCase)) subs.AddRange(Extra);
            return subs;
        }

        public int TaskCount(string path) => Folders.TryGetValue(path, out var n) ? n : throw new FileNotFoundException(path);

        public void Delete(string path)
        {
            if (FailDelete.Contains(path)) throw new System.Runtime.InteropServices.COMException("access denied");
            if (Folders[path] > 0 || SubFolders(path).Count > 0) throw new IOException("not empty");
            Folders.Remove(path);
            Deleted.Add(path);
        }
    }

    private const string Sid = "S-1-5-21-1000-2000-3000-1001";

    [Fact]
    public void Empty_folders_go_depth_first_and_the_couchhud_folder_last()
    {
        var folders = new FakeFolders();
        folders.Folders["\\"] = 3;
        folders.Folders["\\CouchHud"] = 0;
        folders.Folders[$"\\CouchHud\\{Sid}"] = 0;
        folders.Folders["\\Microsoft"] = 0;
        var events = new Events();

        TaskFolderCleanup.RemoveEmpty(folders, events);

        Assert.Equal([$"\\CouchHud\\{Sid}", "\\CouchHud"], folders.Deleted);
        Assert.True(folders.Folders.ContainsKey("\\Microsoft"));
        Assert.Empty(events.Errors);
    }

    [Fact]
    public void A_folder_with_a_task_stays_and_so_does_its_parent()
    {
        var folders = new FakeFolders();
        folders.Folders["\\CouchHud"] = 0;
        folders.Folders[$"\\CouchHud\\{Sid}"] = 1;
        folders.Folders["\\CouchHud\\S-1-5-21-1000-2000-3000-1002"] = 0;

        TaskFolderCleanup.RemoveEmpty(folders, new Events());

        Assert.Equal(["\\CouchHud\\S-1-5-21-1000-2000-3000-1002"], folders.Deleted);
        Assert.True(folders.Folders.ContainsKey($"\\CouchHud\\{Sid}"));
        Assert.True(folders.Folders.ContainsKey("\\CouchHud"));
    }

    [Fact]
    public void A_task_directly_in_the_couchhud_folder_keeps_it()
    {
        var folders = new FakeFolders();
        folders.Folders["\\CouchHud"] = 1;

        TaskFolderCleanup.RemoveEmpty(folders, new Events());

        Assert.Empty(folders.Deleted);
    }

    [Fact]
    public void No_couchhud_folder_is_nothing_to_do()
    {
        var folders = new FakeFolders();
        folders.Folders["\\"] = 0;
        var events = new Events();

        TaskFolderCleanup.RemoveEmpty(folders, events);

        Assert.Empty(folders.Deleted);
        Assert.Empty(events.Errors);
    }

    [Fact]
    public void Failures_are_logged_and_never_thrown()
    {
        var folders = new FakeFolders();
        folders.Folders["\\CouchHud"] = 0;
        folders.Folders[$"\\CouchHud\\{Sid}"] = 0;
        folders.Folders["\\CouchHud\\S-1-5-21-1000-2000-3000-1002"] = 0;
        folders.FailDelete.Add($"\\CouchHud\\{Sid}");
        folders.FailRead.Add("\\CouchHud\\S-1-5-21-1000-2000-3000-1002");
        var events = new Events();

        TaskFolderCleanup.RemoveEmpty(folders, events);

        Assert.Empty(folders.Deleted);
        Assert.Equal(2, events.Errors.Count);
    }

    [Fact]
    public void A_subfolder_name_that_leads_out_of_couchhud_is_never_followed()
    {
        var folders = new FakeFolders();
        folders.Folders["\\CouchHud"] = 0;
        folders.Folders["\\Microsoft"] = 0;
        folders.Extra.Add("\\Microsoft");
        folders.Extra.Add("\\CouchHud\\..\\Microsoft");
        var events = new Events();

        TaskFolderCleanup.RemoveEmpty(folders, events);

        Assert.Empty(folders.Deleted);
        Assert.True(folders.Folders.ContainsKey("\\Microsoft"));
        Assert.Equal(2, events.Errors.Count);
    }

    [Theory]
    [InlineData("\\CouchHud", true)]
    [InlineData("\\couchhud\\S-1-5-21-1", true)]
    [InlineData("\\", false)]
    [InlineData("", false)]
    [InlineData("\\CouchHudX", false)]
    [InlineData("\\Microsoft\\Windows", false)]
    [InlineData("\\CouchHud\\..", false)]
    [InlineData("\\CouchHud\\..\\Microsoft", false)]
    [InlineData("\\CouchHud\\", false)]
    [InlineData("\\CouchHud\\a/b", false)]
    public void Only_couchhud_and_below_is_ours(string path, bool ours) => Assert.Equal(ours, TaskFolderCleanup.IsOurs(path));
}
