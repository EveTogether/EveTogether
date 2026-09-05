using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Runs;

public static class RunsModule
{
    public static void ConfigureClientModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new RunConfiguration());
        modelBuilder.ApplyConfiguration(new RunLootCaptureConfiguration());
        modelBuilder.ApplyConfiguration(new RunLootEntryConfiguration());
        modelBuilder.ApplyConfiguration(new RunBountyEntryConfiguration());
        modelBuilder.ApplyConfiguration(new RunEnemyObservationConfiguration());
        modelBuilder.ApplyConfiguration(new RunParameterConfiguration());
        modelBuilder.ApplyConfiguration(new ActivitySummaryConfiguration());
        // Client-only, never synced (ET-182): where a group code came from is this client's own observation, not a
        // fact the fleet's other members need to agree on.
        modelBuilder.ApplyConfiguration(new RunGroupOriginConfiguration());
    }

    public static void ConfigureServerModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new RunConfiguration());
        modelBuilder.ApplyConfiguration(new RunLootCaptureConfiguration());
        modelBuilder.ApplyConfiguration(new RunLootEntryConfiguration());
        modelBuilder.ApplyConfiguration(new RunBountyEntryConfiguration());
        modelBuilder.ApplyConfiguration(new RunEnemyObservationConfiguration());
        modelBuilder.ApplyConfiguration(new RunParameterConfiguration());
    }

    public static IServiceCollection AddRunsModule(this IServiceCollection services)
    {
        return services;
    }
}
