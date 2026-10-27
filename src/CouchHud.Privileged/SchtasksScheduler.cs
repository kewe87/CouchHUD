namespace CouchHud.Privileged;

/// <summary>The Task Scheduler through schtasks.exe (and its COM interface for the access list).</summary>
public sealed class SchtasksScheduler(IProcessLauncher processes, IEventSink events) : ITaskScheduler
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly string Program = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
    private readonly TaskAcl _acl = new(events);

    public int Run(string taskName) => Call("/Run", "/TN", taskName);

    public int Query(string taskName) => Call("/Query", "/TN", taskName);

    public int Delete(string taskName) => Call("/Delete", "/F", "/TN", taskName);

    public int Create(string taskName, string xmlFile) => Call("/Create", "/F", "/TN", taskName, "/XML", xmlFile);

    public IReadOnlyList<string> List()
    {
        var names = new List<string>();
        var result = processes.Run(Program, ["/Query", "/FO", "CSV", "/NH"], Timeout);
        foreach (var line in result.StdOut.Split('\n'))
        {
            var first = line.Split(',')[0].Trim().Trim('"');
            if (first.StartsWith($"\\{TaskSetup.Folder}\\", StringComparison.OrdinalIgnoreCase)) names.Add(first.TrimStart('\\'));
        }
        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public bool EnsureStartRight(string taskName, string sid) => _acl.Ensure(taskName, sid);

    public bool RestrictToAdministrators(string taskName) => _acl.Restrict(taskName);

    public bool RestrictToUserStart(string taskName, string sid) => _acl.RestrictToUserStart(taskName, sid);

    private int Call(params string[] args) => processes.Run(Program, args, Timeout).ExitCode;
}
