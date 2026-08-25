using System;
using System.Threading;
using System.Threading.Tasks;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Exceptions;
using Velopack.Locators;
using Velopack.Sources;

namespace EveUtils.Client.Updates;

// One source of truth: the Velopack feed on the GitHub release, never the releases API beside it. Two answers to
// one question are free to disagree.
internal sealed class VelopackUpdateService(ILogger<VelopackUpdateService> logger) : IUpdateService, ISingletonService
{
    private const string RepositoryUrl = "https://github.com/EveTogether/EveTogether";
    private const string Source = "Updates";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // The build a successful DownloadAsync fetched, and the manager that fetched it: the only pair an apply call
    // needs, and there is deliberately no way to apply anything else.
    private UpdateManager? _pendingManager;
    private VelopackAsset? _pendingRelease;

    public Task<Result<AppRelease?>> CheckAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(Feed(), locator: null, Patience, logger, cancellationToken);

    /// <summary>The check with the feed and the installation handed in, so a test needs neither the network nor an installed copy.</summary>
    internal static async Task<Result<AppRelease?>> CheckAsync(
        IUpdateSource source,
        IVelopackLocator? locator,
        TimeSpan patience,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Result<(UpdateManager Manager, UpdateInfo? Info)> lookup =
            await _LookUpAsync(source, locator, patience, logger, cancellationToken);

        if (!lookup.IsSuccess)
            return Result<AppRelease?>.Failure([.. lookup.Messages]);

        return Result<AppRelease?>.Success(
            lookup.Value.Info is { TargetFullRelease: { } release } ? _ToRelease(release) : null);
    }

    public Task<Result> DownloadAsync(CancellationToken cancellationToken = default) =>
        DownloadAsync(Feed(), locator: null, Patience, logger, cancellationToken);

    internal async Task<Result> DownloadAsync(
        IUpdateSource source,
        IVelopackLocator? locator,
        TimeSpan patience,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Result<(UpdateManager Manager, UpdateInfo? Info)> lookup =
            await _LookUpAsync(source, locator, patience, logger, cancellationToken);

        if (!lookup.IsSuccess)
            return Result.Failure([.. lookup.Messages]);

        if (lookup.Value.Info is not { TargetFullRelease: { } release } info)
            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.NotFound, "There is no newer build to download.", Source));

        try
        {
            await lookup.Value.Manager.DownloadUpdatesAsync(info, cancelToken: cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The update download failed.");

            return Result.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.UpdateFailed, $"The update download failed: {exception.Message}", Source));
        }

        // Kept only once the transfer finished, so a failed attempt leaves an earlier successful one untouched.
        _pendingManager = lookup.Value.Manager;
        _pendingRelease = release;

        return Result.Success();
    }

    // Velopack's own restart rather than relaunching Environment.ProcessPath: on an AppImage that path is a mount
    // Velopack has just replaced.
    public void ApplyDownloadedUpdateAndRestart()
    {
        if (_pendingManager is null || _pendingRelease is null)
            return;

        _pendingManager.ApplyUpdatesAndRestart(_pendingRelease);
    }

    private static async Task<Result<(UpdateManager Manager, UpdateInfo? Info)>> _LookUpAsync(
        IUpdateSource source,
        IVelopackLocator? locator,
        TimeSpan patience,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        // Two ways to be a copy the installer never placed, neither of them an error: no locator at all (a test
        // host), or one that knows of no installed version (a checkout, an unpacked zip).
        if ((locator ?? (VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null))?.CurrentlyInstalledVersion is null)
            return Result<(UpdateManager, UpdateInfo?)>.Failure(_NotInstalled());

        try
        {
            var manager = new UpdateManager(
                source, new UpdateOptions { ExplicitChannel = UpdateChannelName.Current }, locator);

            Task<UpdateInfo?> check = manager.CheckForUpdatesAsync();

            using var waited = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waited.CancelAfter(patience);

            // Velopack's check takes no cancellation token, so a slow feed is waited out rather than stopped.
            if (await Task.WhenAny(check, Task.Delay(Timeout.Infinite, waited.Token)) != check)
            {
                cancellationToken.ThrowIfCancellationRequested();

                return Result<(UpdateManager, UpdateInfo?)>.Failure(new ResultMessage(
                    MessageSeverity.Error, MessageCodes.Timeout, "The update feed did not answer in time.", Source));
            }

            return Result<(UpdateManager, UpdateInfo?)>.Success((manager, await check));
        }
        catch (NotInstalledException)
        {
            // Velopack also calls an installation with no application id uninstalled — same answer, from one place.
            return Result<(UpdateManager, UpdateInfo?)>.Failure(_NotInstalled());
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The update check failed.");

            return Result<(UpdateManager, UpdateInfo?)>.Failure(new ResultMessage(
                MessageSeverity.Error, MessageCodes.UpdateFailed, $"The update check failed: {exception.Message}", Source));
        }
    }

    private static ResultMessage _NotInstalled() => new(
        MessageSeverity.Warning,
        MessageCodes.UpdateNotInstalled,
        $"This copy was not placed by the EVE Together installer, so it cannot update itself. The latest release is at {RepositoryUrl}/releases",
        Source);

    internal static IUpdateSource Feed() => new GithubSource(RepositoryUrl, AccessToken, prerelease: false);

    // Deliberately none: GitHub lists draft releases only to callers with push access, so asking anonymously is
    // what keeps a half-finished draft from counting as an update candidate.
    internal static string? AccessToken => null;

    private static AppRelease _ToRelease(VelopackAsset release)
    {
        var version = release.Version.ToFullString();

        return new AppRelease(version, release.NotesMarkdown ?? string.Empty, $"{RepositoryUrl}/releases/tag/v{version}");
    }
}
