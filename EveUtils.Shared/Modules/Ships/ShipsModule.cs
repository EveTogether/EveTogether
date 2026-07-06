using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Ships.Entities;
using EveUtils.Shared.Modules.Ships.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Ships;

/// <summary>
/// Registration of the Ships module. Handlers + repository are auto-registered by AddSharedServices
/// this module only adds its wire-event catalog. The EF config is applied by the context
/// (see <see cref="EveUtils.Shared.Data.SharedDbContext"/>).
/// </summary>
public static class ShipsModule
{
    // Scope-gating: Ships is a shared, locally-derived feature → no ESI scope,
    // location Both. (The real scope→feature mapping follows with the Esi module.)
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ShipConfiguration());
        modelBuilder.ApplyConfiguration(new FittingConfiguration());
    }

    // Handlers + repository are auto-registered by AddSharedServices; this only adds the wire-event
    // catalog (module metadata consumed by the remote event-type registry).
    public static IServiceCollection AddShipsModule(this IServiceCollection services)
    {
        services.AddSingleton<IWireEventCatalog, ShipsWireEvents>();
        return services;
    }
}
