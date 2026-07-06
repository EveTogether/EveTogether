using EveUtils.Shared.Data;
using EveUtils.Shared.Modules.Gamelog;
using EveUtils.Shared.Modules.Settings;
using EveUtils.Shared.Modules.Ships;
using EveUtils.Shared.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Client.Data;

public static class ClientDatabase
{
    /// <summary>
    /// Client composition: the SQLite <see cref="IDbContextFactory{TContext}"/>, the
    /// <see cref="SharedDbContext"/> adapter, the runtime marker (Client) and the modules the
    /// client loads (Ships + Settings). Each module registers its own handlers.
    /// </summary>
    public static IServiceCollection AddClientDatabase(this IServiceCollection services, string? connectionString = null)
    {
        var sqliteConnection = connectionString ?? "Data Source=eve-utils-client.db";

        services.AddDbContextFactory<ClientDbContext>(options =>
            options.UseSqlite(sqliteConnection,
                sqlite => sqlite.MigrationsAssembly("EveUtils.Migrations.Client.Sqlite")));

        services.AddSingleton<IDbContextFactory<SharedDbContext>>(sp =>
            new SharedDbContextFactory(
                () => sp.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContext(),
                async cancellationToken => await sp.GetRequiredService<IDbContextFactory<ClientDbContext>>().CreateDbContextAsync(cancellationToken)));

        services.AddSingleton<IRuntimeContext>(new RuntimeContext(ExecutionHost.Client));

        services.AddShipsModule();
        services.AddGamelogModule();
        // Settings has no DI-registration method anymore — its handlers + repository auto-register via
        // AddSharedServices; ConfigureModel is applied by the ClientDbContext.

        return services;
    }
}
