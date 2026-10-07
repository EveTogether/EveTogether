using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace EveUtils.Client.Platform;

/// <summary>Brings another process's main window to the front — the client already running on this data
/// directory (ET-465), for a second start that may not open it again.</summary>
[SupportedOSPlatform("windows")]
internal static class Win32WindowActivation
{
    private const int SW_RESTORE = 9;

    /// <summary>False when the process is gone or has no visible main window (tucked away in the tray), so the caller
    /// can say so instead.</summary>
    public static bool TryBringToFront(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            IntPtr handle = process.MainWindowHandle;
            if (handle == IntPtr.Zero)
                return false;

            if (IsIconic(handle))
                ShowWindow(handle, SW_RESTORE);
            return SetForegroundWindow(handle);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr handle);
}
