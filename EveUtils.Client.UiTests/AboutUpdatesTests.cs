using System.Threading.Tasks;
using EveUtils.Client.Updates;
using EveUtils.Client.ViewModels;
using EveUtils.Shared.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// About is the only place that explains how this copy updates, so it has to be right for a copy that cannot update
/// itself — today the state of every existing user — and for a check that could not reach anything.
/// </summary>
public class AboutUpdatesTests
{
    private sealed class Probe(UpdateSupport support) : IUpdateSupportProbe
    {
        public UpdateSupport Detect() => support;
    }

    private static AboutViewModel Build(
        UpdateSupport support = UpdateSupport.Supported,
        FakeUpdateService? updates = null,
        System.Func<AppRelease, Task>? onInstall = null) =>
        new(null, null, updates ?? new FakeUpdateService(), new Probe(support), onInstall);

    [Fact]
    public void NotInstalledCopy_ExplainsItself_AndOffersNoCheckButton()
    {
        var about = Build(UpdateSupport.NotInstalled);

        Assert.Equal("This copy updates manually.", about.UpdateHeadline);
        Assert.Contains("unpacked yourself", about.UpdateDetail);
        Assert.False(about.CanCheckForUpdates);
        Assert.True(about.ShowReleasesLink);
    }

    // Not a fault: nothing here may read as a breakage, because this is how an unpacked copy simply works.
    [Fact]
    public void NotInstalledCopy_IsNotWordedAsAFailure()
    {
        var about = Build(UpdateSupport.NotInstalled);
        var wording = $"{about.UpdateHeadline} {about.UpdateDetail}";

        Assert.DoesNotContain("error", wording, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("failed", wording, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InstalledCopy_OffersTheCheckButton()
    {
        var about = Build();

        Assert.True(about.CanCheckForUpdates);
        Assert.False(about.ShowReleasesLink);
    }

    [Fact]
    public async Task Check_ConfirmsTheInstalledVersion_WhenTheFeedAnswered()
    {
        var about = Build();

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Contains("latest version", about.UpdateHeadline);
        Assert.Contains("GitHub releases", about.UpdateDetail);
        Assert.False(about.CanInstallUpdate);
    }

    // AC-6 again, on the manual path: the user asked a question and gets the honest answer, not a reassuring one.
    [Fact]
    public async Task FailedCheck_SaysItCouldNotAsk_AndNeverThatYouAreUpToDate()
    {
        var updates = new FakeUpdateService
        {
            OnCheck = () => Task.FromResult(Result<AppRelease?>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.Timeout, "The update feed did not answer in time.", "Updates"))),
        };
        var about = Build(updates: updates);

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't check for updates.", about.UpdateHeadline);
        Assert.Contains("says nothing about whether a newer version exists", about.UpdateDetail);
        Assert.DoesNotContain("latest version", about.UpdateHeadline);
        Assert.Equal("Try again", about.CheckForUpdatesLabel);
        Assert.True(about.ShowReleasesLink);
    }

    [Fact]
    public async Task Check_FindingAnOffer_HandsItToTheMainWindow()
    {
        AppRelease? handed = null;
        var release = new AppRelease("0.3.0", "notes", "https://example.invalid", 81_788_928);
        var updates = new FakeUpdateService { OnCheck = () => Task.FromResult(Result<AppRelease?>.Success(release)) };
        var about = Build(updates: updates, onInstall: offered =>
        {
            handed = offered;
            return Task.CompletedTask;
        });

        await about.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(about.CanInstallUpdate);
        Assert.Contains("v0.3.0 is available", about.UpdateHeadline);

        await about.InstallUpdateCommand.ExecuteAsync(null);
        Assert.Same(release, handed);
    }

    // A supported copy that turns out not to be installed after all still ends up in the manual-update wording.
    [Fact]
    public async Task Check_ReportingNotInstalled_FallsBackToTheManualExplanation()
    {
        var updates = new FakeUpdateService
        {
            OnCheck = () => Task.FromResult(Result<AppRelease?>.Failure(new ResultMessage(
                MessageSeverity.Warning, MessageCodes.UpdateNotInstalled, "Not placed by the installer.", "Updates"))),
        };
        var about = Build(updates: updates);

        await about.CheckForUpdatesCommand.ExecuteAsync(null);

        Assert.Equal("This copy updates manually.", about.UpdateHeadline);
        Assert.False(about.CanCheckForUpdates);
    }
}
