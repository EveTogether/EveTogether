using EveUtils.Shared.Data;
using EveUtils.Shared.DependencyInjection;
using EveUtils.Shared.Modules.Backup.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.Backup.Repositories.Implementations;

internal sealed class BackupAuditRepository(IDbContextFactory<SharedDbContext> contextFactory)
    : IBackupAuditRepository, IScopedService
{
    public async Task AddDownloadAsync(BackupDownload download, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.Add(download);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Newest first, ordered on the key rather than on <see cref="BackupDownload.DownloadedAt"/>. The table is
    /// append-only, so the identity order is the chronological one — and SQLite, the default provider, refuses to
    /// sort a <c>DateTimeOffset</c> column at all.
    /// </summary>
    public async Task<IReadOnlyList<BackupDownload>> ListDownloadsAsync(int limit, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Set<BackupDownload>()
            .AsNoTracking()
            .OrderByDescending(d => d.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
