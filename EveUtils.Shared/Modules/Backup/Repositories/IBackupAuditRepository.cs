using EveUtils.Shared.Modules.Backup.Entities;

namespace EveUtils.Shared.Modules.Backup.Repositories;

/// <summary>Persists and reads the record of who downloaded a server backup archive, and when.</summary>
public interface IBackupAuditRepository
{
    /// <summary>Records one completed download. Called only after the archive has been written in full.</summary>
    Task AddDownloadAsync(BackupDownload download, CancellationToken cancellationToken = default);

    /// <summary>Most recent downloads first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<BackupDownload>> ListDownloadsAsync(int limit, CancellationToken cancellationToken = default);
}
