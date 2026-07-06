using EveUtils.Shared.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace EveUtils.Migrations.Server.MySql;

/// <summary>Design-time factory for the ServerDbContext migrations (MySQL/MariaDB).</summary>
public sealed class ServerMySqlDesignTimeDbContextFactory : IDesignTimeDbContextFactory<ServerDbContext>
{
    public ServerDbContext CreateDbContext(string[] args)
    {
        var assembly = typeof(ServerMySqlDesignTimeDbContextFactory).Assembly.GetName().Name;

        var options = new DbContextOptionsBuilder<ServerDbContext>()
            .UseMySql("Server=localhost;Database=eve_utils;User=root;Password=placeholder;",
                new MariaDbServerVersion(new Version(11, 4, 0)),
                mysql => mysql.MigrationsAssembly(assembly))
            .Options;

        return new ServerDbContext(options);
    }
}
