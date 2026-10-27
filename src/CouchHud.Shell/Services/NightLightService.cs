using System.Runtime.InteropServices;
using CouchHud.Core.Platform;

namespace CouchHud.Shell.Services;

/// <summary>
/// Applies the warm night ramp to the screen and keeps it applied: Windows resets the
/// gamma on mode switches and some games set their own, so it is re-checked every minute.
/// </summary>
public sealed class NightLightService : IDisposable
{
    private readonly Timer _timer;
    private Func<(string Mode, int Strength)> _settings = () => ("off", 0);
    private bool _suspended;
    private bool _applied;

    public NightLightService()
    {
        _timer = new Timer(_ => Apply(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Start(Func<(string Mode, int Strength)> settings)
    {
        _settings = settings;
        Apply();
        _timer.Change(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>A game profile can keep the night mode off while it runs.</summary>
    public void Suspend(bool suspended)
    {
        _suspended = suspended;
        Apply();
    }

    public void Apply()
    {
        var (mode, strength) = _settings();
        var active = !_suspended && NightLight.IsActive(mode, TimeOnly.FromDateTime(DateTime.Now));
        // Leave the gamma alone when night mode is off and we never touched it.
        if (!active && !_applied) return;
        if (SetRamp(NightLight.Ramp(active ? strength : 0))) _applied = active;
    }

    private static bool SetRamp(ushort[] ramp)
    {
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try
        {
            var ok = SetDeviceGammaRamp(dc, ramp);
            if (!ok) Console.Error.WriteLine("[night] the display driver refused the gamma ramp");
            return ok;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_applied) SetRamp(NightLight.Ramp(0));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);
}

/// <summary>Screenshots and clips through the Xbox Game Bar's own shortcuts.</summary>
public static class GameBar
{
    private const byte VkLWin = 0x5B, VkMenu = 0x12, VkSnapshot = 0x2C, VkG = 0x47, VkR = 0x52;
    private const uint KeyUp = 0x2;

    /// <summary>Win + Alt + Print: screenshot of the game in front.</summary>
    public static void Screenshot() => Chord(VkSnapshot);

    /// <summary>Win + Alt + G: saves the last seconds (needs background recording on in the Game Bar).</summary>
    public static void RecordThat() => Chord(VkG);

    /// <summary>Win + Alt + R: starts or stops a recording.</summary>
    public static void ToggleRecording() => Chord(VkR);

    private static void Chord(byte key)
    {
        KeyboardInput.MarkInjected();
        keybd_event(VkLWin, 0, 0, UIntPtr.Zero);
        keybd_event(VkMenu, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkMenu, 0, KeyUp, UIntPtr.Zero);
        keybd_event(VkLWin, 0, KeyUp, UIntPtr.Zero);
        KeyboardInput.MarkInjected();
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
}
