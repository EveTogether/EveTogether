using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Notifications;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// A toast may name its own corner, and corners must not disturb each other: a manager stacks every card it owns in
/// one panel, so a shared manager would drag a toast that is still standing to wherever the next one asked to go.
/// </summary>
public class ToastPositionTests
{
    private static Window ShownWindow()
    {
        var window = new Window { Width = 600, Height = 400 };
        window.Show();
        for (var i = 0; i < 4; i++)
            Dispatcher.UIThread.RunJobs();

        return window;
    }

    [AvaloniaFact]
    public void ManagerFor_FollowsTheConfiguredCorner_WhenTheCallerNamesNone()
    {
        var service = new ToastService { Position = ToastPosition.BottomLeft };
        var window = ShownWindow();

        var (manager, isNew) = service.ManagerFor(window, requested: null);

        Assert.True(isNew);
        Assert.Equal(NotificationPosition.BottomLeft, manager.Position);
        window.Close();
    }

    [AvaloniaFact]
    public void ManagerFor_HonoursACallersCorner_OverTheSetting()
    {
        var service = new ToastService { Position = ToastPosition.TopRight };
        var window = ShownWindow();

        var (manager, _) = service.ManagerFor(window, ToastPosition.BottomRight);

        Assert.Equal(NotificationPosition.BottomRight, manager.Position);
        window.Close();
    }

    // The regression this keying exists for: a later toast in the default corner must not relocate the update offer
    // that is still waiting bottom right.
    [AvaloniaFact]
    public void ManagerFor_KeepsCornersApart_SoAStandingToastIsNotDragged()
    {
        var service = new ToastService { Position = ToastPosition.TopRight };
        var window = ShownWindow();

        var (offer, _) = service.ManagerFor(window, ToastPosition.BottomRight);
        var (ordinary, _) = service.ManagerFor(window, requested: null);

        Assert.NotSame(offer, ordinary);
        Assert.Equal(NotificationPosition.BottomRight, offer.Position);
        Assert.Equal(NotificationPosition.TopRight, ordinary.Position);
        window.Close();
    }

    [AvaloniaFact]
    public void ManagerFor_ReusesTheManager_ForTheSameWindowAndCorner()
    {
        var service = new ToastService();
        var window = ShownWindow();

        var (first, firstIsNew) = service.ManagerFor(window, ToastPosition.BottomRight);
        var (second, secondIsNew) = service.ManagerFor(window, ToastPosition.BottomRight);

        Assert.True(firstIsNew);
        Assert.False(secondIsNew);
        Assert.Same(first, second);
        window.Close();
    }

    [AvaloniaFact]
    public void ManagerFor_KeepsWindowsApart()
    {
        var service = new ToastService();
        var first = ShownWindow();
        var second = ShownWindow();

        var (onFirst, _) = service.ManagerFor(first, ToastPosition.BottomRight);
        var (onSecond, _) = service.ManagerFor(second, ToastPosition.BottomRight);

        Assert.NotSame(onFirst, onSecond);
        first.Close();
        second.Close();
    }
}
