using EveUtils.Shared.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Server.Tests;

/// <summary>
/// An <see cref="IDbContextFactory{TContext}"/> over a throwaway in-memory SQLite database, built from the
/// <see cref="ServerDbContext"/> model via <c>EnsureCreated</c> (no migration projects needed). The connection is kept
/// open for the factory's lifetime so the in-memory schema survives across the per-method contexts each repository
/// creates. Returns <see cref="ServerDbContext"/> typed as <see cref="SharedDbContext"/> for the Shared repositories
/// that depend on <c>IDbContextFactory&lt;SharedDbContext&gt;</c>.
/// </summary>
internal sealed class SqliteServerDbContextFactory : IDbContextFactory<SharedDbContext>, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ServerDbContext> _options;

    public SqliteServerDbContextFactory()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = new ServerDbContext(_options);
        context.Database.EnsureCreated();
    }

    public SharedDbContext CreateDbContext() => new ServerDbContext(_options);

    public void Dispose() => _connection.Dispose();
}
