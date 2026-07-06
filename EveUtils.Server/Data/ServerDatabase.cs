using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Gamelog;
using EveUtils.Shared.Modules.Ships;
using EveUtils.Shared.Modules.Sync;
using EveUtils.Shared.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Server.Data;

public static class ServerDatabase
{
    /// <summary>
    /// Server composition: the provider-selected <see cref="IDbContextFactory{TContext}"/>, the
    /// <see cref="SharedDbContext"/> adapter, the runtime marker (Server) and the modules the
    /// server loads (Ships + Sync).
    /// </summary>
    public static IServiceCollection AddServerDatabase(this IServiceCollection services, IConfiguration configuration, string dataDirectory)
    {
        var providerName = configuration["Database:Provider"]
            ?? throw new InvalidOperationException("Configuration 'Database:Provider' is required.");

        if (!Enum.TryParse<DatabaseProvider>(providerName, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException(
                $"Unknown 'Database:Provider' value '{providerName}'. " +
                $"Valid values: {string.Join(", ", Enum.GetNames<DatabaseProvider>())}.");
        }

        var connectionString = configuration.GetConnectionString(provider.ToString())
            ?? throw new InvalidOperationException(
                $"No connection string named '{provider}' found under 'ConnectionStrings'.");

        // SQLite resolves a relative "Data Source" against the process working directory, which differs
        // per launch method (Rider runs from the bin output, `dotnet run`/`make` from the project dir).
        // That made the server open a *different* database file depending on how it was started, so paired
        // characters silently disappeared on startup. Anchor a relative SQLite file to the stable data
        // directory (same place the TLS cert + token stores live) so the path is launch-independent.
        if (provider == DatabaseProvider.Sqlite)
            connectionString = AnchorRelativeSqliteFile(connectionString, dataDirectory);

        services.AddDbContextFactory<ServerDbContext>(options =>
        {
            switch (provider)
            {
                case DatabaseProvider.Sqlite:
                    options.UseSqlite(connectionString,
                        sqlite => sqlite.MigrationsAssembly("EveUtils.Migrations.Server.Sqlite"));
                    break;

                case DatabaseProvider.MySql:
                    options.UseMySql(connectionString, new MariaDbServerVersion(new Version(11, 4, 0)),
                        mysql => mysql.MigrationsAssembly("EveUtils.Migrations.Server.MySql"));
                    break;

                case DatabaseProvider.SqlServer:
                    options.UseSqlServer(connectionString,
                        sqlServer => sqlServer.MigrationsAssembly("EveUtils.Migrations.Server.SqlServer"));
                    break;

                case DatabaseProvider.PostgreSql:
                    options.UseNpgsql(connectionString,
                        npgsql => npgsql.MigrationsAssembly("EveUtils.Migrations.Server.PostgreSql"));
                    break;

                default:
                    throw new InvalidOperationException($"Provider '{provider}' is not wired up.");
            }
        });

        services.AddSingleton<IDbContextFactory<SharedDbContext>>(sp =>
            new SharedDbContextFactory(
                () => sp.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContext(),
                async cancellationToken => await sp.GetRequiredService<IDbContextFactory<ServerDbContext>>().CreateDbContextAsync(cancellationToken)));

        services.AddSingleton<IRuntimeContext>(new RuntimeContext(ExecutionHost.Server));

        services.AddShipsModule();
        services.AddGamelogModule();
        // Sync has no DI-registration method anymore — its handlers + repository auto-register via
        // AddSharedServices; ConfigureModel is applied by the ServerDbContext.

        return services;
    }

    /// <summary>
    /// Rewrites a relative SQLite <c>Data Source</c> to an absolute path under <paramref name="dataDirectory"/>.
    /// In-memory and already-absolute sources are returned unchanged.
    /// </summary>
    private static string AnchorRelativeSqliteFile(string connectionString, string dataDirectory)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var source = builder.DataSource;

        if (string.IsNullOrEmpty(source)
            || source == ":memory:"
            || source.StartsWith("file::memory:", StringComparison.Ordinal)
            || builder.Mode == SqliteOpenMode.Memory
            || Path.IsPathRooted(source))
        {
            return connectionString;
        }

        Directory.CreateDirectory(dataDirectory);
        builder.DataSource = Path.Combine(dataDirectory, source);
        return builder.ConnectionString;
    }
}
