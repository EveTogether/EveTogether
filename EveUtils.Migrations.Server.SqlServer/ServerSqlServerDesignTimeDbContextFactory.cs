using EveUtils.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EveUtils.Migrations.Server.SqlServer;

/// <summary>Design-time factory for the ServerDbContext migrations (SQL Server).</summary>
public sealed class ServerSqlServerDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ServerDbContext>
{
    public ServerDbContext CreateDbContext(string[] args)
    {
        var assembly = typeof(ServerSqlServerDesignTimeDbContextFactory).Assembly.GetName().Name;

        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseSqlServer("Server=localhost;Database=eve_utils;Trusted_Connection=True;TrustServerCertificate=True",
                sqlServer => sqlServer.MigrationsAssembly(assembly))
            .Options;

        return new ServerDbContext(options);
    }
}
