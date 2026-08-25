using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using EveUtils.Client.Dialogs;
using EveUtils.Client.Notifications;
using EveUtils.Client.Updates;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Settings.Commands;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The startup update check end to end, through the real client DI: what reaches the status bar, what reaches the
/// toast, and what deliberately reaches neither.
/// </summary>
public class UpdateFlowTests
{
    private static AppRelease Release => new("0.3.0", "- Fleet compositions", "https://example.invalid", 81_788_928);

    private sealed record Harness(
        TestClientInstance Instance,
        MainWindowViewModel ViewModel,
        FakeUpdateService Updates,
        RecordingToastService Toasts,
        RecordingDialogService Dialogs);

    private static Harness Build()
    {
        var updates = new FakeUpdateService();
        var toasts = new RecordingToastService();
        var dialogs = new RecordingDialogService();

        var instance = TestClientInstance.Create(services =>
        {
            services.AddSingleton<IUpdateService>(updates);
            services.AddSingleton<IToastService>(toasts);
            services.AddSingleton<IDialogService>(dialogs);
        });

        return new Harness(instance, new MainWindowViewModel(instance.Services), updates, toasts, dialogs);
    }

    private static async Task<string?> RunStartupCheckAsync(Harness harness)
    {
        harness.ViewModel.StartUpdateCheck();

        // The check is deliberately fire-and-forget, so wait for its effect rather than for the call.
        for (var attempt = 0; attempt < 200 && harness.Updates.Checks == 0; attempt++)
            await Task.Delay(10);

        await Task.Delay(50);

        return harness.ViewModel.ActivityStatus;
    }

    // AC-7: the window is up and usable before anything about updates happens — a feed that never answers must not
    // be something the caller waits on.
    [AvaloniaFact]
    public async Task StartUpdateCheck_ReturnsBeforeTheFeedAnswers()
    {
        var harness = Build();
        using var instance = harness.Instance;

        var hanging = new TaskCompletionSource<Result<AppRelease?>>();
        harness.Updates.OnCheck = () => hanging.Task;

        var started = System.Diagnostics.Stopwatch.StartNew();
        harness.ViewModel.StartUpdateCheck();
        started.Stop();

        Assert.True(started.ElapsedMilliseconds < 1000, $"StartUpdateCheck blocked for {started.ElapsedMilliseconds} ms");

        // The feed answers only now; the call above had already returned, which is the whole point.
        hanging.SetResult(Result<AppRelease?>.Success(null));
        await Task.Delay(50);
    }

    [AvaloniaFact]
    public async Task StartupCheck_ConfirmsTheInstalledVersion_WhenTheFeedAnswered()
    {
        var harness = Build();
        using var instance = harness.Instance;

        var status = await RunStartupCheckAsync(harness);

        Assert.Contains("latest version", status);
        Assert.Empty(harness.Toasts.Toasts);
    }

    // AC-6: nothing was reachable, so nothing is known — the status line must say the check failed and must not
    // claim the build is current.
    [AvaloniaFact]
    public async Task StartupCheck_SaysTheCheckFailed_AndNeverThatYouAreUpToDate()
    {
        var harness = Build();
        using var instance = harness.Instance;
        harness.Updates.OnCheck = () => Task.FromResult(Result<AppRelease?>.Failure(new ResultMessage(
            MessageSeverity.Error, MessageCodes.Timeout, "The update feed did not answer in time.", "Updates")));

        var status = await RunStartupCheckAsync(harness);

        Assert.Equal("The update feed did not answer in time.", status);
        Assert.DoesNotContain("latest version", status);
    }

    // AC-5: an unpacked zip or a checkout can do nothing about this, so the main window says nothing at all —
    // About is where it is explained.
    [AvaloniaFact]
    public async Task StartupCheck_StaysSilent_ForACopyTheInstallerNeverPlaced()
    {
        var harness = Build();
        using var instance = harness.Instance;
        harness.Updates.OnCheck = () => Task.FromResult(Result<AppRelease?>.Failure(new ResultMessage(
            MessageSeverity.Warning, MessageCodes.UpdateNotInstalled, "This copy was not placed by the installer.", "Updates")));

        var status = await RunStartupCheckAsync(harness);

        Assert.True(string.IsNullOrEmpty(status), $"expected no status line, got '{status}'");
        Assert.Empty(harness.Toasts.Toasts);
    }

    // The offer lands bottom right and carries buttons, which is what keeps it standing: an action toast has no
    // expiry, and this is the only time this session the user is told.
    [AvaloniaFact]
    public async Task StartupCheck_OffersTheUpdate_AsAPersistentBottomRightToast()
    {
        var harness = Build();
        using var instance = harness.Instance;
        harness.Updates.OnCheck = () => Task.FromResult(Result<AppRelease?>.Success(Release));

        var status = await RunStartupCheckAsync(harness);

        Assert.Equal("Update available: v0.3.0", status);
        Assert.Equal(ToastPosition.BottomRight, Assert.Single(harness.Toasts.Positions));

        var offer = Assert.Single(harness.Toasts.ActionToasts);
        Assert.Equal("Update available", offer.Title);
        Assert.Equal(["Later", "What's new"], offer.Actions.Select(action => action.Label));
    }

    [AvaloniaFact]
    public async Task StartupCheck_IsSkipped_WhenTheStartupSettingIsOff()
    {
        var harness = Build();
        using var instance = harness.Instance;

        using (var scope = instance.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                .Send(new SetSettingCommand("updates.check-on-startup", "false"));

        harness.ViewModel.StartUpdateCheck();
        await Task.Delay(200);

        Assert.Equal(0, harness.Updates.Checks);
    }

    // Nothing is fetched on the strength of the toast alone: the offer dialog is the consent step.
    [AvaloniaFact]
    public async Task Offer_DownloadsNothing_WhenTheUserPicksLater()
    {
        var harness = Build();
        using var instance = harness.Instance;
        harness.Dialogs.OnShowUpdateAvailable = _ => false;

        await harness.ViewModel.ShowUpdateOfferAsync(Release);

        Assert.Single(harness.Dialogs.UpdateOffers);
        Assert.False(harness.ViewModel.IsUpdateReady);
    }

    // A downloaded package that is never applied leaves the app on the old build, so the banner stays put.
    [AvaloniaFact]
    public async Task AcceptedOffer_DownloadsAndRaisesTheRestartBanner()
    {
        var harness = Build();
        using var instance = harness.Instance;
        harness.Dialogs.OnShowUpdateAvailable = _ => true;

        await harness.ViewModel.ShowUpdateOfferAsync(Release);

        Assert.True(harness.ViewModel.IsUpdateReady);
        Assert.Contains("Restart to finish updating", harness.ViewModel.UpdateReadyMessage);
        Assert.Contains("restart to finish updating", harness.ViewModel.ActivityStatus);

        harness.ViewModel.RestartForUpdateCommand.Execute(null);
        Assert.Equal(1, harness.Updates.Applied);
    }

    [AvaloniaFact]
    public async Task FailedDownload_SaysSo_AndRaisesNoBanner()
    {
        var harness = Build();
        using var instance = harness.Instance;
        harness.Dialogs.OnShowUpdateAvailable = _ => true;
        harness.Updates.OnDownload = () => Task.FromResult(Result.Failure(new ResultMessage(
            MessageSeverity.Error, MessageCodes.UpdateFailed, "The update download failed: the connection was reset.", "Updates")));

        await harness.ViewModel.ShowUpdateOfferAsync(Release);

        Assert.False(harness.ViewModel.IsUpdateReady);
        Assert.Contains("download failed", harness.ViewModel.ActivityStatus);
    }

    // "Later" on the banner promises nothing: it hides the banner and applies nothing.
    [AvaloniaFact]
    public async Task DismissingTheBanner_AppliesNothing()
    {
        var harness = Build();
        using var instance = harness.Instance;
        harness.Dialogs.OnShowUpdateAvailable = _ => true;
        await harness.ViewModel.ShowUpdateOfferAsync(Release);

        harness.ViewModel.DismissUpdateReadyCommand.Execute(null);

        Assert.False(harness.ViewModel.IsUpdateReady);
        Assert.Equal(0, harness.Updates.Applied);
    }
}
