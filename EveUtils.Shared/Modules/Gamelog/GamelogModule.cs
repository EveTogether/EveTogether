using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Cqrs.Permissions;
using EveUtils.Shared.Messaging.Wire;
using EveUtils.Shared.Modules.Gamelog.Entities;
using EveUtils.Shared.Modules.Gamelog.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Gamelog;

/// <summary>
/// Registration of the Gamelog module. Folds in the demo's log readers (Core) and adds the
/// owner-bearing <see cref="CombatSample"/> entity, the live <c>CombatLoggedEvent</c> stream and the
/// CQRS handlers. Loaded by both hosts so the table lands in the client SQLite and the server DB.
/// </summary>
public static class GamelogModule
{
    public static void ConfigureModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new CombatSampleConfiguration());
        modelBuilder.ApplyConfiguration(new CharacterMetricStateConfiguration()); // persisted bounty + mined
    }

    // Handlers + repository are auto-registered by AddSharedServices; this only adds the
    // permission catalog + wire-event catalog (module metadata).
    public static IServiceCollection AddGamelogModule(this IServiceCollection services)
    {
        services.AddModulePermissions(new GamelogPermissions());
        services.AddSingleton<IWireEventCatalog, GamelogWireEvents>();
        return services;
    }
}
