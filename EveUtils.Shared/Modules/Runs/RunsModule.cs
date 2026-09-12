using EveUtils.Shared.Modules.Runs.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EveUtils.Shared.Modules.Runs;

public static class RunsModule
{
    public static void ConfigureClientModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new RunConfiguration());
        // I7 (ET-274): one live run per character per group, held by the store itself. Client only: the server keeps
        // what each client publishes under that client's own run ids, and two of its providers have no filtered index.
        // The filter as the relational annotation HasFilter writes: Shared references EF Core alone, not a provider.
        modelBuilder.Entity<Run>().HasIndex(run => new { run.GroupCode, run.CharacterId })
            .IsUnique()
            .HasAnnotation("Relational:Filter", "\"GroupCode\" IS NOT NULL AND \"DeletedAtUtc\" IS NULL");
        modelBuilder.ApplyConfiguration(new RunLootCaptureConfiguration());
        modelBuilder.ApplyConfiguration(new RunLootEntryConfiguration());
        modelBuilder.ApplyConfiguration(new RunBountyEntryConfiguration());
        modelBuilder.ApplyConfiguration(new RunEnemyObservationConfiguration());
        modelBuilder.ApplyConfiguration(new RunParameterConfiguration());
        modelBuilder.ApplyConfiguration(new RunMiningEntryConfiguration());
        modelBuilder.ApplyConfiguration(new RunAttendanceEntryConfiguration());
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
        modelBuilder.ApplyConfiguration(new RunMiningEntryConfiguration());
        modelBuilder.ApplyConfiguration(new RunAttendanceEntryConfiguration());
    }

    public static IServiceCollection AddRunsModule(this IServiceCollection services)
    {
        return services;
    }
}
