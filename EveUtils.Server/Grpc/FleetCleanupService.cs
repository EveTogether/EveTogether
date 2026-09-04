using EveUtils.Shared.Modules.Esi.Http;
using EveUtils.Shared.Modules.Fleet.Cleanup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Grpc;

/// <summary>
/// The server's periodic pass over the fleet tables. Two rules ride the same timer, because they need the same two
/// numbers and the same startup delay:
/// <list type="bullet">
/// <item>the automatic stop (<see cref="FleetAutoStopRunner"/>, ET-167) — a started fleet whose roster emptied or
/// whose members all went quiet goes back to standing by;</item>
/// <item>the cleanup sweep (<see cref="FleetCleanupRunner"/>) — inactive concluded fleets are archived and
/// long-archived ones removed, keeping the tables from accumulating dead plans.</item>
/// </list>
/// Auto-stop runs first: it only ever produces Forming fleets, which the cleanup rule then leaves alone, so the two
/// cannot act on the same fleet in one pass. Mirrors
/// <see cref="EveUtils.Server.Auth.ServerSessionCleanupService"/>: an initial pass shortly after startup, then every
/// few minutes.
/// </summary>
public sealed class FleetCleanupService(
    IServiceScopeFactory scopeFactory,
    IEsiAvailabilityState availability,
    ILogger<FleetCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // The first pass waits long enough for clients to reconnect after a server restart (heartbeat + token refresh +
    // pairing take well over a few seconds). Sweeping at +15s would see zero connected members for every fleet and
    // archive any that looked stale before its members were back — and, since ET-167, would stand down every fleet on
    // the server for the same wrong reason. Same number as FleetCleanupOptions.ReconnectGrace, and now read from it
    // so the two cannot drift.
    private static TimeSpan StartupDelay => FleetCleanupOptions.Default.ReconnectGrace;

    /// <summary>
    /// The last pass at which ESI was seen to be unreachable. Held across passes because a pass runs every five
    /// minutes and would otherwise only ever meet the recovered state — releasing the brake at precisely the moment
    /// the pilots are still queueing to reconnect.
    /// </summary>
    private DateTimeOffset? _lastSeenUnavailableAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Fleet cleanup failed.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    private async Task SweepOnceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var options = FleetCleanupOptions.Default;
        if (!availability.IsUsable)
            _lastSeenUnavailableAt = now;

        var brakeEngaged = FleetAutoStopBrake.IsEngaged(
            now, availability.IsUsable, _lastSeenUnavailableAt, options.ReconnectGrace);

        using var scope = scopeFactory.CreateScope();

        var stopped = await scope.ServiceProvider.GetRequiredService<FleetAutoStopRunner>()
            .SweepAsync(now, options, brakeEngaged, cancellationToken);
        if (stopped.Total > 0)
            logger.LogInformation(
                "Fleet auto-stop: {RosterEmpty} with an empty roster, {AllOffline} with every member offline.",
                stopped.RosterEmpty, stopped.AllOffline);

        var cleaned = await scope.ServiceProvider.GetRequiredService<FleetCleanupRunner>()
            .SweepAsync(now, options, cancellationToken);
        if (cleaned.Archived > 0 || cleaned.Deleted > 0)
            logger.LogInformation("Fleet cleanup: archived {Archived}, deleted {Deleted}.", cleaned.Archived, cleaned.Deleted);
    }
}
