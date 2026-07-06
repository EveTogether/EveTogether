using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Esi;
using EveUtils.Shared.Modules.Fittings.Commands;
using EveUtils.Shared.Modules.Fittings.Entities;
using EveUtils.Shared.Modules.Fittings.Queries;
using EveUtils.Shared.Modules.Fittings.Repositories;
using EveUtils.Shared.Modules.Fittings.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Fittings;

/// <summary>
/// Registration of the Fittings module. Call <see cref="AddFittingsModule"/> (client) or
/// <see cref="AddFittingsServerModule"/> (server) from each host's composition root.
/// </summary>
public static class FittingsModule
{
    /// <summary>Applies <see cref="LocalFitting"/> to the client DbContext (client-local fittings).</summary>
    public static void ConfigureClientModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new LocalFittingConfiguration());

    /// <summary>Applies <see cref="SharedFit"/> to the server DbContext (server-wide shared fits).</summary>
    public static void ConfigureServerModel(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfiguration(new SharedFitConfiguration());

    /// <summary>Client composition: the fittings permission + ESI-scope + wire-event catalogs. Handlers,
    /// the repository and the ESI client are auto-registered by AddSharedServices.</summary>
    public static IServiceCollection AddFittingsModule(this IServiceCollection services)
    {
        services.AddModulePermissions(FittingsPermissions.Catalog);
        services.AddModuleEsiScopes(FittingsScopeCatalog.Catalog);
        services.AddSingleton<IWireEventCatalog, FittingsWireEvents>();
        return services;
    }

    /// <summary>Server composition: the fittings permission + wire-event catalogs. Handlers and
    /// repositories are auto-registered by AddSharedServices; the client-side fittings handlers it
    /// also registers are simply never dispatched on the server (registered-but-unused is fine).</summary>
    public static IServiceCollection AddFittingsServerModule(this IServiceCollection services)
    {
        services.AddModulePermissions(FittingsPermissions.Catalog);
        services.AddSingleton<IWireEventCatalog, FittingsWireEvents>();
        return services;
    }
}
