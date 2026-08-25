using System.Diagnostics;
using EveUtils.Client.Updates;
using EveUtils.Shared.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

namespace EveUtils.Client.UiTests;

/// <summary>
/// The update check, driven through a real <c>UpdateManager</c> with the feed and the installation stood in for:
/// a fake <see cref="IUpdateSource"/> records the channel it was asked for, and a <c>TestVelopackLocator</c> makes
/// the process look installed. Runs without a network and without an installed copy.
/// </summary>
public class VelopackUpdateServiceTests : IDisposable
{
    private const string PackageId = "EveTogether";

    private readonly string _packages = Directory.CreateTempSubdirectory("et32-").FullName;

    public void Dispose()
    {
        Directory.Delete(_packages, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A checkout, an unpacked zip or a tarball is the state every current user is in — an ordinary answer with its
    /// own code, so the UI can say "not installed" rather than "the check failed".
    /// </summary>
    [Fact]
    public async Task CheckAsync_WithoutAnInstalledCopy_ReportsNotInstalled()
    {
        Result<AppRelease?> result = await VelopackUpdateService.CheckAsync(
            new Feed(), locator: null, TimeSpan.FromSeconds(5), NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(MessageCodes.UpdateNotInstalled, result.Messages.Single().Code);
    }

    /// <summary>A feed that cannot be reached is a failure with a reason; "you are up to date" would be a lie the user believes.</summary>
    [Fact]
    public async Task CheckAsync_WhenTheFeedFails_ReportsFailureRatherThanUpToDate()
    {
        Result<AppRelease?> result = await _CheckAsync(new Feed { Fails = true });

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(MessageCodes.UpdateFailed, result.Messages.Single().Code);
    }

    /// <summary>
    /// The same for a feed that answers too slowly. The elapsed time is asserted too: without it the check could be
    /// ignoring the patience it was handed and waiting out its own ten-second constant, and everything else here
    /// would still hold — slowly.
    /// </summary>
    [Fact]
    public async Task CheckAsync_WhenTheFeedNeverAnswers_GivesUpAfterThePatienceItWasGiven()
    {
        var started = Stopwatch.StartNew();

        Result<AppRelease?> result = await _CheckAsync(new Feed { Hangs = true }, patience: TimeSpan.FromMilliseconds(50));

        started.Stop();

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.Equal(MessageCodes.Timeout, result.Messages.Single().Code);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(2), $"gave up after {started.Elapsed}, which is the built-in wait rather than the one it was given");
    }

    [Fact]
    public async Task CheckAsync_WithANewerBuildOnTheChannel_OffersItWithItsNotesAndReleasePage()
    {
        Result<AppRelease?> result = await _CheckAsync(new Feed(_Package("0.9.0", "what changed")));

        Assert.True(result.IsSuccess);
        Assert.Equal("0.9.0", result.Value?.Version);
        Assert.Equal("what changed", result.Value?.Notes);
        Assert.Equal("https://github.com/EveTogether/EveTogether/releases/tag/v0.9.0", result.Value?.Url);
    }

    [Fact]
    public async Task CheckAsync_WithNothingNewerOnTheChannel_IsUpToDate()
    {
        Result<AppRelease?> result = await _CheckAsync(new Feed(_Package("0.1.0")));

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value);
    }

    /// <summary>The channel that reaches the feed is what keeps a Windows install away from the macOS package in the same release.</summary>
    [Fact]
    public async Task CheckAsync_AsksTheFeed_ForThisBuildsPlatformAndArchitecture()
    {
        var feed = new Feed();

        await _CheckAsync(feed);

        Assert.Equal(UpdateChannelName.Current, feed.AskedFor);
    }

    /// <summary>
    /// The feed is read anonymously, and that is the whole of what keeps draft releases out of it: GitHub lists
    /// drafts only to callers with push access. Pinned because the reason someone would add a token is the rate
    /// limit, which gives no hint that drafts ride on the answer.
    /// </summary>
    [Fact]
    public void AccessToken_IsNone_WhichIsWhatKeepsDraftReleasesOut() =>
        Assert.Null(VelopackUpdateService.AccessToken);

    [Fact]
    public void Feed_IsTheProjectsGitHubReleases() =>
        Assert.IsType<GithubSource>(VelopackUpdateService.Feed());

    [Fact]
    public async Task DownloadAsync_WithoutAnInstalledCopy_ReportsNotInstalled()
    {
        Result result = await new VelopackUpdateService(NullLogger<VelopackUpdateService>.Instance).DownloadAsync(
            new Feed(), locator: null, TimeSpan.FromSeconds(5), NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.UpdateNotInstalled, result.Messages.Single().Code);
    }

    /// <summary>A download with nothing to fetch is a failure — a check finding nothing is "up to date", a download finding nothing is not.</summary>
    [Fact]
    public async Task DownloadAsync_WithNothingNewerToFetch_Fails()
    {
        Result result = await new VelopackUpdateService(NullLogger<VelopackUpdateService>.Instance).DownloadAsync(
            new Feed(_Package("0.1.0")), _Locator(), TimeSpan.FromSeconds(5), NullLogger.Instance, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageCodes.NotFound, result.Messages.Single().Code);
    }

    /// <summary>Applying without a download must do nothing: the alternative restarts into whatever the installer last left on disk.</summary>
    [Fact]
    public void ApplyDownloadedUpdateAndRestart_BeforeAnyDownload_IsANoOp() =>
        new VelopackUpdateService(NullLogger<VelopackUpdateService>.Instance).ApplyDownloadedUpdateAndRestart();

    private Task<Result<AppRelease?>> _CheckAsync(Feed feed, TimeSpan? patience = null) =>
        VelopackUpdateService.CheckAsync(
            feed, _Locator(), patience ?? TimeSpan.FromSeconds(5), NullLogger.Instance, CancellationToken.None);

    private IVelopackLocator _Locator() => new TestVelopackLocator(PackageId, "0.2.0", _packages);

    private static VelopackAsset _Package(string version, string notes = "") => new()
    {
        PackageId = PackageId,
        Version = SemanticVersion.Parse(version),
        Type = VelopackAssetType.Full,
        FileName = $"{PackageId}-{version}-full.nupkg",
        NotesMarkdown = notes,
    };

    /// <summary>Stands in for the release feed, and remembers the channel it was asked for — which is one of the assertions.</summary>
    private sealed class Feed(params VelopackAsset[] assets) : IUpdateSource
    {
        public string? AskedFor { get; private set; }

        public bool Fails { get; init; }

        public bool Hangs { get; init; }

        public async Task<VelopackAssetFeed> GetReleaseFeed(
            IVelopackLogger logger,
            string? appId,
            string channel,
            Guid? stagingId = null,
            VelopackAsset? latestLocalRelease = null)
        {
            AskedFor = channel;

            if (Hangs)
                await Task.Delay(Timeout.Infinite);

            return Fails
                ? throw new HttpRequestException("the feed was unreachable")
                : new VelopackAssetFeed { Assets = assets };
        }

        public Task DownloadReleaseEntry(
            IVelopackLogger logger,
            VelopackAsset releaseEntry,
            string localFile,
            Action<int> progress,
            CancellationToken cancelToken = default) =>
            throw new NotSupportedException("the real transfer is verified against a published release, not here");
    }
}
