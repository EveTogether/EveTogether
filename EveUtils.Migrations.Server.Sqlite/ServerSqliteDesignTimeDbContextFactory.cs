using EveUtils.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EveUtils.Migrations.Server.Sqlite;

/// <summary>Design-time factory for the ServerDbContext migrations (SQLite, dev/test).</summary>
public sealed class ServerSqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ServerDbContext>
{
    public ServerDbContext CreateDbContext(string[] args)
    {
        var assembly = typeof(ServerSqliteDesignTimeDbContextFactory).Assembly.GetName().Name;

        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseSqlite("Data Source=design-time-server.db",
                sqlite => sqlite.MigrationsAssembly(assembly))
            .Options;

        return new ServerDbContext(options);
    }
}
