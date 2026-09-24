using EveUtils.Shared.Modules.ServerAuth.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace EveUtils.Server.Auth;

/// <summary>
/// Periodically purges server sessions that can no longer become a working credential — past their hard
/// refresh window, or silent for longer than <see cref="ServerSessionService.IdleLifetime"/> — and then releases the
/// paired characters left without a session. On-encounter cleanup in <see cref="ServerSessionService.ValidateAsync"/>
/// handles the rest. Runs every 5 minutes (plus once shortly after startup).
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
                await SweepAsync(stoppingToken);
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

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IServerAuthRepository>();
        var now = DateTimeOffset.UtcNow;
        var removed = await repo.DeleteLapsedSessionsAsync(now, now - ServerSessionService.IdleLifetime, cancellationToken);
        if (removed > 0)
            logger.LogInformation(
                "Purged {Count} lapsed server session(s) — past the refresh window, or with no sign of life for {IdleDays:0} days.",
                removed, ServerSessionService.IdleLifetime.TotalDays);

        // Sessions vanish here, from the panel and (before ET-344) from decouples without leaving a trace of the
        // character behind. The first sweep after startup is what clears the rows that were already orphaned.
        var releaser = scope.ServiceProvider.GetRequiredService<SyncedCharacterReleaser>();
        var released = await releaser.ReleaseAllWithoutSessionAsync(cancellationToken);
        if (released > 0)
            logger.LogInformation("Released {Count} paired character(s) that no session is coupled to any more.", released);
    }
}
