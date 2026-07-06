using EveUtils.Shared.Modules.Fleet.Cleanup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Grpc;

/// <summary>
/// Periodically runs the fleet cleanup sweep so inactive fleets are archived and long-archived fleets
/// are removed, keeping the fleet tables from accumulating dead plans. Mirrors
/// <see cref="EveUtils.Server.Auth.ServerSessionCleanupService"/>: an initial sweep shortly after startup, then
/// every few minutes. The decision + persistence live in <see cref="FleetCleanupRunner"/>.
/// </summary>
public sealed class FleetCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<FleetCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    // The first sweep waits long enough for clients to reconnect after a server restart (heartbeat + token refresh +
    // pairing take well over a few seconds). Sweeping at +15s would see zero connected members for every fleet and
    // archive any that looked stale before its members were back.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var runner = scope.ServiceProvider.GetRequiredService<FleetCleanupRunner>();
                var result = await runner.SweepAsync(DateTimeOffset.UtcNow, FleetCleanupOptions.Default, stoppingToken);
                if (result.Archived > 0 || result.Deleted > 0)
                    logger.LogInformation("Fleet cleanup: archived {Archived}, deleted {Deleted}.", result.Archived, result.Deleted);
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
}
