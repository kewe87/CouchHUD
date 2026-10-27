using System.ServiceProcess;
using CouchHud.Privileged;
using CouchHud.Service.Logging;
using CouchHud.Service.State;

namespace CouchHud.Service;

internal static class Program
{
    /// <summary>The setup's call before the new service runs (security review M-1): the old tasks of 0.4.0 to SYSTEM and administrators only.</summary>
    internal const string RestrictLegacyTasksSwitch = "--restrict-legacy-tasks";

    private static int Main(string[] args)
    {
        var log = new RedactingEventSink(new EventLogSink());

        // Only this one switch, and only to take rights away (it never grants or starts anything): run by the elevated setup, and harmless when
        // anyone else runs it (a standard user cannot change the access list of a task of SYSTEM). 0: every task restricted, 1: some left.
        if (args is [RestrictLegacyTasksSwitch])
        {
            var open = LegacyAdoption.RestrictAll(new SchtasksScheduler(new ProcessLauncher(), log), log);
            log.Info($"[legacy] setup: old tasks restricted to administrators ({(open == 0 ? "all" : $"{open} left open")})");
            return open == 0 ? 0 : 1;
        }

        // An exception nobody caught: log its type and let the process end; the SCM failure actions restart the service.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            log.Error($"[crash] unhandled {(e.ExceptionObject as Exception)?.GetType().Name ?? "exception"}; the service ends and the SCM restarts it");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Error($"[error] unobserved task exception {e.Exception.InnerException?.GetType().Name ?? e.Exception.GetType().Name}");
            e.SetObserved();
        };

        // Only under the SCM: started any other way, ServiceBase.Run fails and the process ends (no console, no window).
        ServiceBase.Run(new CouchHudWindowsService(log));
        return 0;
    }
}
