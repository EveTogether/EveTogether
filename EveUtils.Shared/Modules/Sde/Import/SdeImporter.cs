using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Sde.Dtos;
using Microsoft.Extensions.Logging;

namespace EveUtils.Shared.Modules.Sde.Import;

/// <summary>
/// Default <see cref="ISdeImporter"/>. Downloads to the work dir, builds a throwaway SQLite next to the live
/// store, then <see cref="File.Move(string,string,bool)"/>-swaps it in (atomic on the same volume) and reopens
/// the accessor. A failed/partial build never touches the live store — it stays on the previous good build.
/// </summary>
public sealed class SdeImporter(
    ISdeSource source,
    ISdeAccessor accessor,
    SdeOptions options,
    ILogger<SdeImporter> logger) : ISdeImporter, ISingletonService
{
    public async Task<SdeUpdateCheck> CheckForUpdateAsync(CancellationToken cancellationToken = default)
    {
        var remote = await source.GetLatestAsync(cancellationToken);
        var local = accessor.Version;
        var updateAvailable = local is null || remote.BuildNumber > local.BuildNumber;
        return new SdeUpdateCheck(updateAvailable, local, remote);
    }

    public async Task<SdeImportResult> EnsureUpToDateAsync(
        IProgress<SdeImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new SdeImportProgress(SdeImportPhase.CheckingVersion));
        SdeUpdateCheck check;
        try
        {
            check = await CheckForUpdateAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(progress, ex);
        }

        if (!check.UpdateAvailable)
        {
            logger.LogInformation("SDE up to date (build {Build}).", check.Local?.BuildNumber);
            progress?.Report(new SdeImportProgress(SdeImportPhase.AlreadyUpToDate, ProcessedItems: 0, TotalItems: 0));
            return SdeImportResult.UpToDate(check.Local!);
        }

        return await ImportInternalAsync(check.Remote, progress, cancellationToken);
    }

    public async Task<SdeImportResult> ImportAsync(
        IProgress<SdeImportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new SdeImportProgress(SdeImportPhase.CheckingVersion));
        SdeVersion remote;
        try
        {
            remote = await source.GetLatestAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(progress, ex);
        }

        return await ImportInternalAsync(remote, progress, cancellationToken);
    }

    private async Task<SdeImportResult> ImportInternalAsync(
        SdeVersion target, IProgress<SdeImportProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(options.WorkDirectory);
        var directory = Path.GetDirectoryName(options.DatabasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var zipPath = Path.Combine(options.WorkDirectory, $"sde-{target.BuildNumber}.zip");
        var buildPath = $"{options.DatabasePath}.build-{target.BuildNumber}";

        try
        {
            logger.LogInformation("SDE update available (build {Build}); downloading…", target.BuildNumber);
            progress?.Report(new SdeImportProgress(SdeImportPhase.Downloading, DownloadedBytes: 0, TotalBytes: 0));
            await source.DownloadZipAsync(
                target.BuildNumber,
                zipPath,
                new SyncProgress<(long Downloaded, long Total)>(
                    p => progress?.Report(new SdeImportProgress(SdeImportPhase.Downloading, p.Downloaded, p.Total))),
                cancellationToken);

            // CPU-bound; off the caller's thread so a UI dispatcher stays responsive.
            await Task.Run(() => new SdeSqliteBuilder().Build(zipPath, buildPath, target, progress, cancellationToken),
                cancellationToken);

            // Release the live store first: on Windows an open (pooled/mmap) handle makes File.Move-overwrite fail
            // with UnauthorizedAccessException, so the accessor must let go before the atomic swap.
            accessor.Close();
            await SwapInAsync(buildPath, options.DatabasePath, cancellationToken);
            accessor.Reopen();
            TryDelete(zipPath);

            logger.LogInformation("SDE import complete (build {Build}).", target.BuildNumber);
            progress?.Report(new SdeImportProgress(SdeImportPhase.Completed));
            return SdeImportResult.Imported(target);
        }
        catch (OperationCanceledException)
        {
            TryDelete(buildPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(buildPath);
            return Fail(progress, ex);
        }
    }

    // Atomic swap with a short retry: after the accessor releases the file, a lingering OS/AV handle can still briefly
    // block the overwrite, so retry a few times before giving up.
    private static async Task SwapInAsync(string buildPath, string databasePath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(buildPath, databasePath, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 10)
            {
                await Task.Delay(150, cancellationToken);
            }
        }
    }

    private SdeImportResult Fail(IProgress<SdeImportProgress>? progress, Exception ex)
    {
        logger.LogError(ex, "SDE import failed.");
        progress?.Report(new SdeImportProgress(SdeImportPhase.Failed, Error: ex.Message));
        return SdeImportResult.Failed(ex.Message);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup of a temp artifact; ignore.
        }
    }
}
