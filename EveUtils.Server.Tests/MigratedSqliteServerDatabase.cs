using EveUtils.Shared.Data;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Server.Tests;

/// <summary>
/// A throwaway server installation on disk: its own data directory and a file-backed SQLite database built by the
/// real migration stack. The backup engine drops tables and re-runs migrations to a named target, so unlike the
/// other server tests this cannot use <c>EnsureCreated</c> over an in-memory database — there would be no
/// <c>__EFMigrationsHistory</c> to rebuild against.
/// </summary>
internal sealed class MigratedSqliteServerDatabase : IDbContextFactory<ServerDbContext>, IDisposable
{
    private readonly DbContextOptions<ServerDbContext> _options;

    public MigratedSqliteServerDatabase()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "et99-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataDirectory);

        // Pooling off, not ClearAllPools on dispose: clearing pools is process-wide, and xUnit runs test classes
        // in parallel — one fixture tearing down would yank the connection out from under another one's restore.
        _options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(DataDirectory, "eve-utils-server.db")};Pooling=False",
                sqlite => sqlite.MigrationsAssembly("EveUtils.Migrations.Server.Sqlite"))
            .Options;

        using var db = CreateDbContext();
        db.Database.Migrate();
    }

    public string DataDirectory { get; }

    public ServerDbContext CreateDbContext() => new(_options);

    public Task<ServerDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public void Dispose()
    {
        if (Directory.Exists(DataDirectory))
            Directory.Delete(DataDirectory, recursive: true);
    }
}
