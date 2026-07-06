using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Layout;
using EveUtils.Client.Notifications;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The visual a toast with action buttons is built from (<see cref="ToastActionContent"/>). The toast plumbing itself
/// needs a window's overlay layer, but the content — a button per action, each dismissing the toast (CloseOnClick) and
/// running its callback — is pure and asserted here. Guards the "open metrics" / Accept-Decline toast support.
/// </summary>
public class ToastActionContentTests
{
    [AvaloniaFact]
    public void Build_RendersOneButtonPerAction_EachDismissingAndRunningItsCallback()
    {
        var openMetrics = false;
        var dismissed = false;
        var actions = new List<ToastAction>
        {
            new("Open metrics", () => openMetrics = true),
            new("Dismiss", () => dismissed = true),
        };

        var content = ToastActionContent.Build("Fleet started", "Home Defense Fleet", ToastKind.Success, actions);

        var buttons = ButtonsOf(content);
        Assert.Equal(2, buttons.Count);
        Assert.Equal(new[] { "Open metrics", "Dismiss" }, buttons.Select(b => (string?)b.Content));
        Assert.All(buttons, b => Assert.True(NotificationCard.GetCloseOnClick(b), "every action button must dismiss the toast"));

        buttons[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(openMetrics);
        Assert.False(dismissed);

        buttons[1].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(dismissed);
    }

    [AvaloniaFact]
    public void Build_WithoutMessage_OmitsTheMessageLine_AndKeepsTheButtons()
    {
        var content = ToastActionContent.Build("Invite", null, ToastKind.Information,
            new List<ToastAction> { new("Accept", () => { }), new("Decline", () => { }) });

        var texts = LayoutOf(content).Children.OfType<TextBlock>().ToList();
        Assert.Single(texts); // title only — no secondary message line
        Assert.Contains("Invite", texts[0].Text);
        Assert.Equal(2, ButtonsOf(content).Count);
    }

    // The content is a padding Border wrapping the vertical layout StackPanel (title/message/button row).
    private static StackPanel LayoutOf(Control content) =>
        Assert.IsType<StackPanel>(Assert.IsType<Border>(content).Child);

    // The button row is the one horizontal StackPanel in the layout; pull its buttons.
    private static IReadOnlyList<Button> ButtonsOf(Control content) =>
        LayoutOf(content).Children
            .OfType<StackPanel>()
            .Single(p => p.Orientation == Orientation.Horizontal)
            .Children.OfType<Button>()
            .ToList();
}
