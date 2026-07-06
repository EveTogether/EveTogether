using EveUtils.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EveUtils.Migrations.Client.Sqlite;

/// <summary>Design-time factory for the ClientDbContext migrations (SQLite).</summary>
public sealed class ClientSqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ClientDbContext>
{
    public ClientDbContext CreateDbContext(string[] args)
    {
        var assembly = typeof(ClientSqliteDesignTimeDbContextFactory).Assembly.GetName().Name;

        var options = new DbContextOptionsBuilder<ClientDbContext>()
            .UseSqlite("Data Source=design-time-client.db",
                sqlite => sqlite.MigrationsAssembly(assembly))
            .Options;

        return new ClientDbContext(options);
    }
}
