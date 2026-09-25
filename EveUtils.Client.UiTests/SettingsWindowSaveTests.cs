using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Updates;
using EveUtils.Client.Views;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// ET-376: Save applies and waits, but no longer closes the window — only Cancel (the close button) and re-import
/// do that. Covers the window staying open, the "Saved."/"Could not save" status line appearing only once the
/// awaited apply has actually finished, and "Check now" picking up the channel just saved.
/// </summary>
public class SettingsWindowSaveTests
{
    private static SettingsWindow BuildWindow(Func<SettingsResult, Task>? onApply = null, IUpdateService? updates = null) =>
        new(currentDirectory: "", detectedDefault: "",
            shareLocation: false, shareBounty: false, shareCombat: true, loadTypeImages: false,
            currentFaction: EveUtils.Client.Theming.FactionTheme.Gallente,
            sdeVersionLabel: "", onApply: onApply, updates: updates);

    // A1: Save applies but does not close the window — Cancel/the close button is the only way out.
    [AvaloniaFact]
    public void Save_KeepsTheWindowOpen()
    {
        var window = BuildWindow();
        window.Show();
        var closeRequests = 0;
        window.CloseRequested += () => closeRequests++;

        var saveButton = window.FindControl<Button>("SaveButton");
        Assert.NotNull(saveButton);
        saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, closeRequests);
        Assert.True(window.IsVisible);
    }

    // A2 + A3: the status line stays hidden while the awaited apply is still pending (never fire-and-forget), and
    // reflects whichever way it actually went once it settles.
    [AvaloniaTheory]
    [InlineData(null, "Saved.")]
    [InlineData("boom", "Could not save: boom")]
    public async Task Save_ShowsStatusOnlyAfterApplyCompletes(string? failureMessage, string expectedStatus)
    {
        var gate = new TaskCompletionSource();
        var window = BuildWindow(onApply: _ => gate.Task);
        window.Show();

        var saveButton = window.FindControl<Button>("SaveButton");
        var statusBlock = window.FindControl<TextBlock>("SaveStatusBlock");
        Assert.NotNull(saveButton);
        Assert.NotNull(statusBlock);

        saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.False(statusBlock.IsVisible);
        Assert.False(saveButton.IsEnabled);

        _ = failureMessage is null
            ? gate.TrySetResult()
            : gate.TrySetException(new InvalidOperationException(failureMessage));
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(10);
        Dispatcher.UIThread.RunJobs();

        Assert.True(statusBlock.IsVisible);
        Assert.Equal(expectedStatus, statusBlock.Text);
        Assert.True(saveButton.IsEnabled);
    }

    // A4: _effectiveChannel used to be frozen at open — after ET-376 (Save no longer closing) a "Check now" click
    // that follows a Save must ask about the channel just saved, not the one the window opened with.
    [AvaloniaFact]
    public async Task Save_ThenCheckNow_UsesTheSavedChannel()
    {
        var updates = new FakeUpdateService();
        var window = BuildWindow(updates: updates);
        window.Show();
        var categoryNav = window.FindControl<ListBox>("CategoryNav");
        Assert.NotNull(categoryNav);
        categoryNav.SelectedIndex = 4; // Updates

        var nightlyButton = window.FindControl<ToggleButton>("ChannelNightlyButton");
        Assert.NotNull(nightlyButton);
        nightlyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        var saveButton = window.FindControl<Button>("SaveButton");
        Assert.NotNull(saveButton);
        saveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(10);
        Dispatcher.UIThread.RunJobs();

        var checkNowButton = window.FindControl<Button>("CheckNowButton");
        Assert.NotNull(checkNowButton);
        checkNowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(UpdateChannel.Nightly, updates.LastChannel);
    }
}
