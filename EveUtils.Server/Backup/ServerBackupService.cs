using EveUtils.Shared.App;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.Backup.Entities;
using EveUtils.Shared.Modules.Backup.Repositories;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Backup;

// EveUtils.Server.Stream (the DPS hub) shadows System.IO.Stream inside this project.
using Stream = System.IO.Stream;

/// <summary>
/// What the admin panel talks to: write an archive out to the browser, take one back in, and the record of who
/// carried one off. The engine underneath (<see cref="BackupExporter"/> / <see cref="BackupRestorer"/>) has no
/// idea a panel exists, which is what let it be tested headlessly.
/// </summary>
internal sealed class ServerBackupService(
    BackupExporter exporter,
    BackupRestorer restorer,
    IBackupAuditRepository audit,
    ILogger<ServerBackupService> logger) : IScopedService
{
    /// <summary>
    /// How long the process stays up after a restore so the browser gets the page saying what happened. Short: the
    /// database underneath the running server has just been replaced, and every extra second is a background
    /// service writing stale in-memory state into it.
    /// </summary>
    private static readonly TimeSpan ShutdownDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Records the download and then writes the archive to <paramref name="destination"/>. That order is
    /// deliberate: the archive is encrypted block by block, so a transfer that breaks off still delivered every
    /// block that got through, and an attempt is as much worth recording as a completion. The alternative — record
    /// afterwards — quietly leaves the interesting case out of the list.
    /// </summary>
    public async Task<BackupDownload> WriteArchiveAsync(
        Stream destination, string password, int adminUserId, string adminUsername, DateTimeOffset takenAt,
        CancellationToken cancellationToken)
    {
        var download = new BackupDownload
        {
            AdminUserId = adminUserId,
            AdminUsername = adminUsername,
            DownloadedAt = takenAt,
            AppVersion = AppInfo.Version,
            FileName = BackupFormat.DownloadFileName(takenAt),
        };

        await audit.AddDownloadAsync(download, cancellationToken);
        logger.LogWarning(
            "Admin {Admin} is downloading a full server backup. It decrypts every stored refresh token.", adminUsername);

        var manifest = await exporter.WriteAsync(destination, password, cancellationToken);
        logger.LogInformation("Backup archive written: {Tables} tables, {Rows} rows.",
            manifest.Tables.Count, manifest.Tables.Sum(t => t.Rows));

        return download;
    }

    public Task<Result<BackupRestoreReport>> RestoreAsync(Stream upload, string password, CancellationToken cancellationToken) =>
        restorer.RestoreAsync(upload, password, cancellationToken);

    public Task<IReadOnlyList<BackupDownload>> ListDownloadsAsync(int limit, CancellationToken cancellationToken = default) =>
        audit.ListDownloadsAsync(limit, cancellationToken);

    /// <summary>
    /// Ends the process shortly after a restore, so the container's restart policy brings it back on the restored
    /// data. <c>Environment.Exit</c> rather than a graceful host shutdown on purpose: a graceful stop runs every
    /// hosted service's shutdown path, and those hold state belonging to the database that no longer exists.
    /// Nothing needs flushing — the restore committed its own transaction before returning.
    /// </summary>
    public void ScheduleShutdownAfterRestore()
    {
        logger.LogWarning("Restore complete; stopping in {Delay} so the process restarts on the restored data.", ShutdownDelay);

        _ = Task.Run(async () =>
        {
            await Task.Delay(ShutdownDelay);
            Environment.Exit(0);
        });
    }
}
