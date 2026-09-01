using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Backup.Entities;
using EveUtils.Shared.Modules.Backup.Repositories;
using EveUtils.Shared.Modules.Backup.Repositories.Implementations;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace EveUtils.Server.Tests;

/// <summary>
/// The record of who downloaded a backup, against a real SQLite database rather than a fake. The listing is the
/// query the panel runs on every visit, and it has to translate on the provider self-hosters actually run:
/// ordering an append-only audit on its <c>DateTimeOffset</c> column throws on SQLite, so it orders on the key.
/// </summary>
public class BackupAuditRepositoryTests : IDisposable
{
    private readonly MigratedSqliteServerDatabase _database = new();
    private readonly IBackupAuditRepository _repository;

    public BackupAuditRepositoryTests() =>
        _repository = new BackupAuditRepository(new SharedContextFactory(_database));

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task ListDownloadsAsync_ReturnsTheMostRecentFirst()
    {
        await AddAsync("first", new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero));
        await AddAsync("second", new DateTimeOffset(2026, 8, 2, 10, 0, 0, TimeSpan.Zero));
        await AddAsync("third", new DateTimeOffset(2026, 8, 3, 10, 0, 0, TimeSpan.Zero));

        var downloads = await _repository.ListDownloadsAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal(["third", "second", "first"], downloads.Select(d => d.AdminUsername));
    }

    [Fact]
    public async Task ListDownloadsAsync_HonoursTheLimit()
    {
        for (var i = 0; i < 5; i++)
            await AddAsync($"admin{i}", DateTimeOffset.UtcNow);

        Assert.Equal(2, (await _repository.ListDownloadsAsync(2, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task ListDownloadsAsync_NoDownloadsYet_IsEmpty()
    {
        Assert.Empty(await _repository.ListDownloadsAsync(10, TestContext.Current.CancellationToken));
    }

    private Task AddAsync(string username, DateTimeOffset at) =>
        _repository.AddDownloadAsync(new BackupDownload
        {
            AdminUserId = 1,
            AdminUsername = username,
            DownloadedAt = at,
            AppVersion = "0.2.0",
            FileName = "eve-together-backup.etbackup",
        }, TestContext.Current.CancellationToken);

    /// <summary>The repository takes the Shared context type; the test database hands out the server one.</summary>
    private sealed class SharedContextFactory(IDbContextFactory<ServerDbContext> inner) : IDbContextFactory<SharedDbContext>
    {
        public SharedDbContext CreateDbContext() => inner.CreateDbContext();
    }
}
