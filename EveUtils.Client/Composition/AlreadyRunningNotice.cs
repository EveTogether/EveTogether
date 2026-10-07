using System.Threading;
using Avalonia;
using Avalonia.Threading;
using EveUtils.Client.Platform;
using EveUtils.Client.Views;

namespace EveUtils.Client.Composition;

/// <summary>What a second start on a data directory another client holds does instead of opening it (ET-465): hand
/// the pilot the window that is already there, or tell them it is running somewhere they cannot see.</summary>
internal static class AlreadyRunningNotice
{
    public const string Message = "EVE Together is already running.";

    public static void Show(AppBuilder appBuilder, int? ownerProcessId)
    {
        if (OperatingSystem.IsWindows() && ownerProcessId is { } processId && Win32WindowActivation.TryBringToFront(processId))
            return;

        // Without the desktop lifetime: App's own start-up builds the main window out of services this start never made.
        appBuilder.SetupWithoutStarting();
        using var closed = new CancellationTokenSource();
        var notice = new MessageBoxWindow("EVE Together", Message);
        notice.Closed += (_, _) => closed.Cancel();
        notice.Show();
        Dispatcher.UIThread.MainLoop(closed.Token);
    }
}
