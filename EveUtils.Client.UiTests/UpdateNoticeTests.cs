using EveUtils.Client.Updates;
using EveUtils.Shared.Messaging;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// What a finished check tells the user. The rule under test is AC-6: a check that could not ask is never reported
/// as "up to date", and the four outcomes are told apart by message code rather than by the text they carry.
/// </summary>
public class UpdateNoticeTests
{
    private const string Installed = "v0.2.0-beta";

    private static Result<AppRelease?> Offer(string version = "0.3.0") =>
        Result<AppRelease?>.Success(new AppRelease(version, "notes", "https://example.invalid", 81_788_928));

    private static Result<AppRelease?> Current() => Result<AppRelease?>.Success(null);

    private static Result<AppRelease?> Failed(string code, string text) =>
        Result<AppRelease?>.Failure(new ResultMessage(MessageSeverity.Error, code, text, "Updates"));

    [Fact]
    public void Classify_ReportsAnOffer_WhenTheFeedCarriesARelease() =>
        Assert.Equal(UpdateNoticeKind.Available, UpdateNotice.Classify(Offer()));

    [Fact]
    public void Classify_ReportsUpToDate_OnlyWhenTheFeedActuallyAnswered() =>
        Assert.Equal(UpdateNoticeKind.UpToDate, UpdateNotice.Classify(Current()));

    [Theory]
    [InlineData(MessageCodes.Timeout, "The update feed did not answer in time.")]
    [InlineData(MessageCodes.UpdateFailed, "The update check failed: no such host is known.")]
    public void Classify_ReportsFailure_ForAnUnreachableFeed(string code, string text) =>
        Assert.Equal(UpdateNoticeKind.Failed, UpdateNotice.Classify(Failed(code, text)));

    [Fact]
    public void Classify_ReportsNotInstalled_ForACopyTheInstallerNeverPlaced()
    {
        var check = Result<AppRelease?>.Failure(new ResultMessage(
            MessageSeverity.Warning, MessageCodes.UpdateNotInstalled, "This copy was not placed by the installer.", "Updates"));

        Assert.Equal(UpdateNoticeKind.NotInstalled, UpdateNotice.Classify(check));
    }

    // The failure that reads most like success: a feed whose message happens to mention being up to date must still
    // classify as failed, because the code — not the prose — is what says whether anything was asked.
    [Fact]
    public void Classify_IgnoresTheMessageText_AndGoesOnTheCode()
    {
        var check = Failed(MessageCodes.UpdateFailed, "Could not confirm you are up to date.");

        Assert.Equal(UpdateNoticeKind.Failed, UpdateNotice.Classify(check));
    }

    [Theory]
    [InlineData(MessageCodes.Timeout, "The update feed did not answer in time.")]
    [InlineData(MessageCodes.UpdateFailed, "The update check failed: no such host is known.")]
    public void StartupStatus_NeverClaimsUpToDate_WhenNothingWasReachable(string code, string text)
    {
        var status = UpdateNotice.StartupStatus(Failed(code, text), Installed);

        Assert.Equal(text, status);
        Assert.DoesNotContain("latest version", status);
        Assert.DoesNotContain("up to date", status, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupStatus_NamesTheOfferedVersion() =>
        Assert.Equal("Update available: v0.3.0", UpdateNotice.StartupStatus(Offer(), Installed));

    [Fact]
    public void StartupStatus_ConfirmsTheInstalledVersion_WhenTheFeedAnswered() =>
        Assert.Equal($"You're on the latest version ({Installed}).", UpdateNotice.StartupStatus(Current(), Installed));

    // A copy that cannot replace itself can do nothing with this at startup, so About is the only place that says it.
    [Fact]
    public void StartupStatus_StaysSilent_ForACopyTheInstallerNeverPlaced()
    {
        var check = Result<AppRelease?>.Failure(new ResultMessage(
            MessageSeverity.Warning, MessageCodes.UpdateNotInstalled, "This copy was not placed by the installer.", "Updates"));

        Assert.Null(UpdateNotice.StartupStatus(check, Installed));
    }

    [Fact]
    public void Reason_PrefersTheError_OverAWarningBesideIt()
    {
        var check = Result.Failure(
            new ResultMessage(MessageSeverity.Warning, MessageCodes.NotFound, "A warning.", "Updates"),
            new ResultMessage(MessageSeverity.Error, MessageCodes.UpdateFailed, "The real reason.", "Updates"));

        Assert.Equal("The real reason.", UpdateNotice.Reason(check));
    }
}
