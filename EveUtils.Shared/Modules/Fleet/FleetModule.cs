using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Fleet.Composition;
using EveUtils.Shared.Modules.Fleet.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Fleet;

/// <summary>
/// Registration of the Fleet module. The entities live in Shared but their tables land in
/// a host DB via <see cref="ConfigureModel"/> (server) / <see cref="ConfigureClientModel"/> (client).
/// The same Shared handlers + repository are auto-registered by AddSharedServices on both hosts; this
/// module only adds the app-permission catalog.
/// </summary>
public static class FleetModule
{
    /// <summary>Applies the fleet entities to the server DbContext (the <c>IsClientOnly</c> flag is ignored — it
    /// is a client-only concept — so the server table is unchanged).</summary>
    public static void ConfigureModel(ModelBuilder modelBuilder) => ApplyEntities(modelBuilder, mapClientOnlyFlag: false);

    /// <summary>Applies the same fleet entities to the client DbContext, plus the client-only <c>IsClientOnly</c>
    /// column: a client-only fleet lives purely in local SQLite and is never published to a server.</summary>
    public static void ConfigureClientModel(ModelBuilder modelBuilder) => ApplyEntities(modelBuilder, mapClientOnlyFlag: true);

    private static void ApplyEntities(ModelBuilder modelBuilder, bool mapClientOnlyFlag)
    {
        modelBuilder.ApplyConfiguration(new FleetConfiguration(mapClientOnlyFlag));
        modelBuilder.ApplyConfiguration(new FleetWingConfiguration());
        modelBuilder.ApplyConfiguration(new FleetSquadConfiguration());
        modelBuilder.ApplyConfiguration(new FleetMemberConfiguration());
        modelBuilder.ApplyConfiguration(new FleetInviteConfiguration());
        modelBuilder.ApplyConfiguration(new FleetJoinRequestConfiguration());

        // Fleet Compositions: the doctrine + role-groups + fit-entries. The composition mirrors the
        // client-only split of the fleet itself; the role/entry tables are host-agnostic.
        modelBuilder.ApplyConfiguration(new FleetCompositionConfiguration(mapClientOnlyFlag));
        modelBuilder.ApplyConfiguration(new FleetCompositionRoleConfiguration());
        modelBuilder.ApplyConfiguration(new FleetCompositionEntryConfiguration());
    }

    /// <summary>Server composition: the fleet lifecycle handlers, repository and app-permission catalog.</summary>
    // Handlers + repository are auto-registered by AddSharedServices; this only adds the
    // app-permission catalog (fleet.create/edit/disband).
    public static IServiceCollection AddFleetModule(this IServiceCollection services)
    {
        services.AddModulePermissions(FleetPermissions.Catalog);
        services.AddModulePermissions(FleetCompositionPermissions.Catalog); // fleet-composition.manage
        services.AddSingleton<IWireEventCatalog, FleetWireEvents>(); // invite events over the remote bus
        return services;
    }
}
