using EveUtils.Shared.Modules.RunOutcomes.Entities;
using Microsoft.EntityFrameworkCore;

namespace EveUtils.Shared.Modules.RunOutcomes;

/// <summary>
/// The anonymous run outcomes a server collects for its earnings statistic (ET-514, epic ET-512). Server only: a
/// client keeps its own runs and reads these over gRPC. Handlers and the repository are auto-registered by
/// AddSharedServices; the ones writing here are only ever dispatched on the server.
/// </summary>
public static class RunOutcomesModule
{
    /// <summary>The control panel's switch: a code with no row is on. Off, a server neither takes nor hands out
    /// outcomes; a pilot can still withdraw theirs.</summary>
    public const string EnabledToggle = "runs.outcomes.enabled";

    /// <summary>How far back an outcome is kept: the statistic's 365-day fallback window plus a margin.</summary>
    public const int RetentionDays = 400;

    /// <summary>The widest window a read may ask for: the statistic's fallback (ET-512 decision 4).</summary>
    public const int WidestWindowDays = 365;

    public static void ConfigureServerModel(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new RunOutcomeConfiguration());
        modelBuilder.ApplyConfiguration(new RunOutcomeLootLineConfiguration());
    }
}
