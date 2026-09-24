using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using EveUtils.Client.Updates;
using EveUtils.Client.Views;
using EveUtils.Shared.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The Updates settings category (ET-339): the release-channel segmented control, the moved "Check for updates on
/// startup" row and the "Check now" row under THIS INSTALL. Renders headless so the tab actually loads (Iron Law #9
/// GUI-verify) and drives "Check now" through a fake feed so the outcome text and the no-uncaught-exception
/// guarantee are both exercised.
/// </summary>
public class SettingsWindowUpdatesTests
{
    [AvaloniaFact]
    public void Settings_ShowsUpdatesSection_Renders()
    {
        var window = new SettingsWindow(
            currentDirectory: "/home/raymond/.eve/logs",
            detectedDefault: "/home/raymond/.eve/logs",
            shareLocation: false, shareBounty: false, shareCombat: true, loadTypeImages: false,
            currentFaction: EveUtils.Client.Theming.FactionTheme.Gallente,
            sdeVersionLabel: "build 3374020 (released 2026-06-03)",
            checkUpdatesOnStartup: true,
            includeNightlyBuilds: true,
            updates: new FakeUpdateService())
        {
            Width = 580,
            MaxHeight = 1100 // lift the scroll clip so the whole Updates category is in view for the capture
        };

        window.Show();
        var categoryNav = window.FindControl<ListBox>("CategoryNav");
        Assert.NotNull(categoryNav);
        categoryNav.SelectedIndex = 4; // Updates

        // Rendering is the assertion: a window whose XAML fails to load throws on construction, and one that lays
        // out to nothing hands back no frame — same as UpdateViewRenderTests.Render.
        Assert.NotNull(window.CaptureRenderedFrame());
    }

    [AvaloniaTheory]
    [InlineData(true, "Up to date")]
    [InlineData(false, "The update feed did not answer in time.")]
    public void CheckNow_ShowsTheOutcome_AndNeverThrows(bool feedIsReachable, string expectedSubstring)
    {
        var updates = new FakeUpdateService();
        if (!feedIsReachable)
        {
            updates.OnCheck = () => System.Threading.Tasks.Task.FromResult(Result<AppRelease?>.Failure(
                new ResultMessage(MessageSeverity.Error, MessageCodes.Timeout, "The update feed did not answer in time.", "Updates")));
        }

        var window = new SettingsWindow(
            currentDirectory: "", detectedDefault: "",
            shareLocation: false, shareBounty: false, shareCombat: true, loadTypeImages: false,
            currentFaction: EveUtils.Client.Theming.FactionTheme.Gallente,
            sdeVersionLabel: "", updates: updates);
        window.Show();
        var categoryNav = window.FindControl<ListBox>("CategoryNav");
        Assert.NotNull(categoryNav);
        categoryNav.SelectedIndex = 4; // Updates

        var checkNowButton = window.FindControl<Button>("CheckNowButton");
        Assert.NotNull(checkNowButton);
        checkNowButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

        // FakeUpdateService answers with an already-completed Task, so the async handler's continuation is queued
        // on, not blocked by, the dispatcher — one flush is enough to run it to completion.
        Dispatcher.UIThread.RunJobs();

        var result = window.FindControl<TextBlock>("CheckNowResultBlock");
        Assert.NotNull(result);
        Assert.True(result.IsVisible);
        Assert.Contains(expectedSubstring, result.Text);
    }
}
