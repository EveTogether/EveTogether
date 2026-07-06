using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using EveUtils.Client.Notifications;
using EveUtils.Client.Theming;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// Faithful headless render of the action toasts through the real <see cref="WindowNotificationManager"/> (the same
/// path <see cref="ToastService"/> uses), so the card chrome, padding and button sizing match the running app and can
/// be eyeballed: compact buttons that fit, title that does not overflow, Accept green and Decline red.
/// </summary>
public class ToastRenderTests
{
    private static string Out(string name) => Path.Combine(Path.GetTempPath(), name);

    [AvaloniaFact]
    public void ActionToasts_RenderThroughTheRealManager()
    {
        var instance = TestClientInstance.Create();
        instance.Services.GetRequiredService<IThemeService>().Apply(FactionTheme.Caldari);

        var window = new Window { Width = 900, Height = 520, Background = new SolidColorBrush(Color.Parse("#FF06070A")) };
        window.Show();
        Pump();

        var manager = new WindowNotificationManager(window) { MaxItems = 3, Position = NotificationPosition.TopRight };
        Pump();

        manager.Show(ToastActionContent.Build("Fleet invite: test 2", "You are invited to 'test 2' as Squad Member.",
            ToastKind.Information,
            [new ToastAction("Accept", () => { }, ToastActionStyle.Affirmative),
             new ToastAction("Decline", () => { }, ToastActionStyle.Destructive)]));
        manager.Show(ToastActionContent.Build("Fleet started: Roam", "Roam has started — open its metrics to see the fleet live.",
            ToastKind.Success,
            [new ToastAction("Open metrics", () => { })]));
        Pump();

        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("no rendered frame to save");
        frame.Save(Out("toast-real.png"));
        window.Close();
    }

    private static void Pump()
    {
        for (var i = 0; i < 8; i++)
            Dispatcher.UIThread.RunJobs();
    }
}
