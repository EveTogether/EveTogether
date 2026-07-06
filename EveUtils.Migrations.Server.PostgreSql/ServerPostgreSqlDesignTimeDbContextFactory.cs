using EveUtils.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EveUtils.Migrations.Server.PostgreSql;

/// <summary>Design-time factory for the ServerDbContext migrations (PostgreSQL).</summary>
public sealed class ServerPostgreSqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ServerDbContext>
{
    public ServerDbContext CreateDbContext(string[] args)
    {
        var assembly = typeof(ServerPostgreSqlDesignTimeDbContextFactory).Assembly.GetName().Name;

        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseNpgsql("Host=localhost;Database=eve_utils;Username=postgres;Password=placeholder",
                npgsql => npgsql.MigrationsAssembly(assembly))
            .Options;

        return new ServerDbContext(options);
    }
}
