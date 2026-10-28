using System.Runtime.InteropServices;

namespace CouchHud.Shell.Services;

/// <summary>
/// Work that must never be felt while someone plays (the automatic save backup): an own thread in Windows' background mode, which
/// lowers its CPU, disk and memory priority together. It runs in the HUD's process, as the signed-in user, never in the service.
/// </summary>
public static class BackgroundWork
{
    private const int ThreadModeBackgroundBegin = 0x00010000;
    private const int ThreadModeBackgroundEnd = 0x00020000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    public static Task<T> Run<T>(Func<T> work, string name)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var background = SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundBegin);
            try
            {
                done.SetResult(work());
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                if (background) SetThreadPriority(GetCurrentThread(), ThreadModeBackgroundEnd);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Lowest,
            Name = name
        };
        thread.Start();
        return done.Task;
    }
}
