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
    }

    public static IServiceCollection AddRunsModule(this IServiceCollection services)
    {
        return services;
    }
}
