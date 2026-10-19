using EveUtils.Shared.Cqrs;
using EveUtils.Shared.Messaging;
using EveUtils.Shared.Modules.RunOutcomes;
using EveUtils.Shared.Modules.RunOutcomes.Commands;

namespace EveUtils.Server.RunOutcomes;

/// <summary>
/// Drops the shared run outcomes older than <see cref="RunOutcomesModule.RetentionDays"/> (ET-514): the statistic
/// never reads past its 365-day fallback window, so keeping more would only be keeping data. Runs once shortly after
/// startup and then every six hours, like the other retention services.
/// </summary>
public sealed class RunOutcomeRetentionService(IServiceScopeFactory scopeFactory, ILogger<RunOutcomeRetentionService> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using IServiceScope scope = scopeFactory.CreateScope();
                DateOnly cutoff = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-RunOutcomesModule.RetentionDays);
                Result<int> pruned = await scope.ServiceProvider.GetRequiredService<IDispatcher>()
                    .Send(new PruneRunOutcomesCommand(cutoff), stoppingToken);
                if (pruned.Value > 0)
                    logger.LogInformation("Pruned {Count} run outcome(s) completed before {Cutoff}.", pruned.Value, cutoff);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Run outcome retention failed.");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
