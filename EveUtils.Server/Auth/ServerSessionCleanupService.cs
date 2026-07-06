using EveUtils.Shared.Modules.ServerAuth.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Auth;

/// <summary>
/// Periodically purges expired server sessions so the ServerSession table doesn't accumulate
/// stale entries. On-encounter cleanup in <see cref="ServerSessionService.ValidateAsync"/> handles the
/// rest. Runs every 5 minutes (plus once shortly after startup).
/// </summary>
public sealed class ServerSessionCleanupService(
    IServiceScopeFactory scopeFactory,
    ILogger<ServerSessionCleanupService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Initial sweep shortly after startup, then on the interval.
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var repo = scope.ServiceProvider.GetRequiredService<IServerAuthRepository>();
                var removed = await repo.DeleteExpiredSessionsAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (removed > 0)
                    logger.LogInformation("Purged {Count} expired server session(s).", removed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Server session cleanup failed.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
